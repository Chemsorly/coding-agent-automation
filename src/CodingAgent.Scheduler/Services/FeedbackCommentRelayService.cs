using CodingAgent.Api.Client;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Scheduler.Services;

/// <summary>
/// Scheduler-hosted, leader-gated background service that drains the
/// <c>FeedbackCommentOutbox</c> table by posting deferred feedback comments
/// to their respective issue providers.
///
/// Design mirrors <see cref="OrphanedLabelRecoveryService"/>:
/// <list type="bullet">
///   <item>Plain <see cref="BackgroundService"/> with an inline leader-gate null check.</item>
///   <item>60-second grace period on startup.</item>
///   <item><see cref="PeriodicTimer"/> sweep every 60 seconds.</item>
///   <item>Leader-gated to prevent duplicate delivery across Scheduler replicas.</item>
///   <item>At-least-once delivery: if the comment posts but <c>MarkCompleted</c> fails,
///         the relay will re-post on the next sweep (acceptable per decisions.md).</item>
/// </list>
/// </summary>
public sealed class FeedbackCommentRelayService : BackgroundService
{
    private static readonly TimeSpan DefaultGracePeriod = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromSeconds(60);

    private readonly IPipelineApiFeedbackCommentOutboxClient _outboxClient;
    private readonly IProviderFactory _providerFactory;
    private readonly IPipelineApiConfigClient _configClient;
    private readonly ILeaderGate? _leaderGate;
    private readonly ILogger _logger;
    private readonly TimeSpan _gracePeriod;
    private readonly TimeSpan _sweepInterval;

    public FeedbackCommentRelayService(
        IPipelineApiFeedbackCommentOutboxClient outboxClient,
        IProviderFactory providerFactory,
        IPipelineApiConfigClient configClient,
        ILeaderGate? leaderGate,
        ILogger logger)
        : this(outboxClient, providerFactory, configClient, leaderGate, logger, DefaultGracePeriod, DefaultSweepInterval)
    {
    }

    /// <summary>Internal constructor for testing — allows overriding grace period and sweep interval.</summary>
    internal FeedbackCommentRelayService(
        IPipelineApiFeedbackCommentOutboxClient outboxClient,
        IProviderFactory providerFactory,
        IPipelineApiConfigClient configClient,
        ILeaderGate? leaderGate,
        ILogger logger,
        TimeSpan gracePeriod,
        TimeSpan sweepInterval = default)
    {
        _outboxClient = outboxClient;
        _providerFactory = providerFactory;
        _configClient = configClient;
        _leaderGate = leaderGate;
        _logger = logger.ForContext<FeedbackCommentRelayService>();
        _gracePeriod = gracePeriod == default ? DefaultGracePeriod : gracePeriod;
        _sweepInterval = sweepInterval == default ? DefaultSweepInterval : sweepInterval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            _logger.Information("Feedback comment relay: waiting {GracePeriod} grace period before first sweep", _gracePeriod);
            await Task.Delay(_gracePeriod, stoppingToken);

            await RunInitialSweepAsync(stoppingToken);

            using var timer = new PeriodicTimer(_sweepInterval);
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                try
                {
                    await SweepAsync(stoppingToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning(ex, "Feedback comment relay: sweep failed — will retry next interval");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.Information("Feedback comment relay service stopping");
        }
    }

    private async Task RunInitialSweepAsync(CancellationToken stoppingToken)
    {
        try
        {
            await SweepAsync(stoppingToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Feedback comment relay: initial sweep failed — will continue to periodic loop");
        }
    }

    /// <summary>
    /// Exposed for testing: runs a single sweep synchronously.
    /// In production, sweeps are triggered by the periodic timer in ExecuteAsync.
    /// </summary>
    internal Task SweepOnceForTestAsync(CancellationToken ct) => SweepAsync(ct);

    private async Task SweepAsync(CancellationToken ct)
    {
        // Gate check: skip when not the leader to prevent duplicate delivery.
        // Null gate = unconditional (dev / single-replica mode).
        if (_leaderGate is { IsLeader: false })
        {
            _logger.Debug("Feedback comment relay: skipping sweep — not the leader");
            return;
        }

        // Link LeaderToken so in-flight provider calls abort immediately on leadership loss.
        using var linked = _leaderGate is not null
            ? CancellationTokenSource.CreateLinkedTokenSource(ct, _leaderGate.LeaderToken)
            : null;
        var sweepCt = linked?.Token ?? ct;

        // Load maxAttempts from config; fall back to default on transient failure.
        var maxAttempts = await LoadMaxAttemptsAsync(sweepCt);
        const int pageSize = 20;

        var pending = await _outboxClient.GetPendingAsync(maxAttempts, pageSize, sweepCt);
        if (pending is null || pending.Count == 0) return;

        _logger.Information("Feedback comment relay: processing {Count} pending entry/entries", pending.Count);

        // Load provider configs once per sweep (all needed kinds: Issue + Repository)
        var issueProviders = await LoadProviderConfigsAsync(ProviderKind.Issue, sweepCt);
        var repoProviders = await LoadProviderConfigsAsync(ProviderKind.Repository, sweepCt);

        foreach (var entry in pending)
        {
            sweepCt.ThrowIfCancellationRequested();
            await ProcessEntryAsync(entry, issueProviders, repoProviders, maxAttempts, sweepCt);
        }
    }

    private async Task ProcessEntryAsync(
        FeedbackCommentOutboxEntry entry,
        IReadOnlyList<ProviderConfig> issueProviderConfigs,
        IReadOnlyList<ProviderConfig> repoProviderConfigs,
        int maxAttempts,
        CancellationToken ct)
    {
        try
        {
            // Deserialize stored feedback
            var feedback = System.Text.Json.JsonSerializer.Deserialize<IssueFeedback>(
                entry.FeedbackJson,
                PipelineJsonOptions.Default);

            var commentBody = FeedbackCommentFormatter.FormatComment(feedback);
            if (commentBody is null)
            {
                // This entry has no deliverable comment body — mark completed so it won't be retried.
                _logger.Warning(
                    "Feedback comment relay: entry {EntryId} (run {RunId}) has no formatted comment body — marking completed without posting",
                    entry.Id, entry.RunId);
                await _outboxClient.MarkCompletedAsync(entry.Id, ct);
                return;
            }

            // Resolve issue provider config
            var issueConfig = issueProviderConfigs.FirstOrDefault(p => p.Id == entry.IssueProviderConfigId);
            if (issueConfig is null)
            {
                _logger.Warning(
                    "Feedback comment relay: issue provider config '{ConfigId}' not found for run {RunId} — will retry",
                    entry.IssueProviderConfigId, entry.RunId);
                await _outboxClient.MarkFailedAsync(
                    entry.Id,
                    $"Issue provider config '{entry.IssueProviderConfigId}' not found",
                    maxAttempts,
                    ct);
                return;
            }

            // Post the comment
            await using var issueProvider = _providerFactory.CreateIssueProvider(issueConfig);
            await issueProvider.ValidateAsync(ct);
            var commentUrl = await issueProvider.PostCommentAsync(entry.IssueIdentifier, commentBody, ct);

            _logger.Information(
                "Feedback comment relay: posted comment for run {RunId} on issue {IssueIdentifier}",
                entry.RunId, entry.IssueIdentifier);

            // Optionally append feedback link to PR body
            if (commentUrl is not null && !string.IsNullOrEmpty(entry.PullRequestNumber))
            {
                await TryAppendFeedbackLinkToPrBodyAsync(
                    entry,
                    commentUrl,
                    repoProviderConfigs,
                    ct);
            }

            await _outboxClient.MarkCompletedAsync(entry.Id, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(
                ex,
                "Feedback comment relay: failed to deliver comment for run {RunId} (entryId={EntryId}) — incrementing attempt count",
                entry.RunId, entry.Id);
            try
            {
                await _outboxClient.MarkFailedAsync(entry.Id, ex.Message, maxAttempts, ct);
            }
            catch (Exception markEx) when (markEx is not OperationCanceledException)
            {
                _logger.Warning(markEx,
                    "Feedback comment relay: failed to mark entry {EntryId} as failed", entry.Id);
            }
        }
    }

    private async Task TryAppendFeedbackLinkToPrBodyAsync(
        FeedbackCommentOutboxEntry entry,
        string commentUrl,
        IReadOnlyList<ProviderConfig> repoProviderConfigs,
        CancellationToken ct)
    {
        try
        {
            var repoConfig = repoProviderConfigs.FirstOrDefault(p => p.Id == entry.RepoProviderConfigId);
            if (repoConfig is null)
            {
                _logger.Warning(
                    "Feedback comment relay: repo provider config '{ConfigId}' not found for run {RunId}, skipping PR-body append",
                    entry.RepoProviderConfigId, entry.RunId);
                return;
            }

            if (!int.TryParse(entry.PullRequestNumber, out var prNumber))
                return;

            await using var repoProvider = _providerFactory.CreateRepositoryProvider(repoConfig);

            // Idempotency guard: fetch current remote body and check for existing marker.
            // The relay cannot rely on the in-memory PipelineRun.PullRequestBody (it doesn't exist here).
            var currentBody = await repoProvider.GetPullRequestBodyAsync(prNumber, ct) ?? "";

            if (currentBody.Contains("## Agent Feedback"))
            {
                _logger.Debug(
                    "Feedback comment relay: '## Agent Feedback' section already present in PR #{PrNumber} for run {RunId}, skipping append",
                    prNumber, entry.RunId);
                return;
            }

            var feedbackSection = $"\n\n## Agent Feedback\n⚠️ Agent posted feedback on the issue [here]({commentUrl}). Read before merging.";
            var newBody = currentBody + feedbackSection;
            await repoProvider.UpdatePullRequestAsync(prNumber, newBody, null, ct);

            _logger.Information(
                "Feedback comment relay: appended feedback link to PR #{PrNumber} for run {RunId}",
                prNumber, entry.RunId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Non-fatal: PR-body append failure should not prevent marking the outbox entry completed.
            _logger.Warning(ex,
                "Feedback comment relay: failed to append feedback link to PR #{PrNumber} for run {RunId} (non-fatal)",
                entry.PullRequestNumber, entry.RunId);
        }
    }

    private async Task<int> LoadMaxAttemptsAsync(CancellationToken ct)
    {
        try
        {
            var config = await _configClient.GetPipelineConfigAsync(ct);
            return config.FeedbackCommentOutboxMaxAttempts > 0
                ? config.FeedbackCommentOutboxMaxAttempts
                : PipelineConstants.DefaultFeedbackCommentOutboxMaxAttempts;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Feedback comment relay: failed to load config — using default maxAttempts");
            return PipelineConstants.DefaultFeedbackCommentOutboxMaxAttempts;
        }
    }

    private async Task<IReadOnlyList<ProviderConfig>> LoadProviderConfigsAsync(ProviderKind kind, CancellationToken ct)
    {
        try
        {
            return await _configClient.GetProviderConfigsWithSecretsAsync(kind, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // TODO [WARNING]: Returning an empty list on transient failure causes every pending entry
            // in this sweep to hit the "provider config not found" path and call MarkFailedAsync with
            // a spurious error. If this happens maxAttempts times consecutively (e.g. during a
            // prolonged API outage), entries are permanently marked Failed and never delivered.
            // Consider aborting the sweep entirely (re-throw) when provider config loading fails,
            // so entries remain Pending until the API recovers. The circuit breaker on the HttpClient
            // reduces the probability but does not eliminate the data-loss window.
            _logger.Warning(ex, "Feedback comment relay: failed to load {Kind} provider configs", kind);
            return Array.Empty<ProviderConfig>();
        }
    }
}
