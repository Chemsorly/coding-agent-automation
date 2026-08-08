using System.Threading.RateLimiting;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Orchestration.Telemetry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// K8s mode only: polls WorkItems WHERE Status=Pending AND TaskType!=Consolidation ORDER BY CreatedAt ASC,
/// resolves container image via JobTemplateStore, creates K8s Jobs via JobSpecBuilder,
/// updates to Dispatched. Runs under leader election (same Lease as PipelineLoopService).
/// Rate-limited: default 10 Jobs/s. Skips items whose selector group is at concurrency limit.
/// Consolidation items are handled by <see cref="ConsolidationDispatchHandler"/>.
/// </summary>
public sealed class DispatchService : BackgroundService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DispatchService>();

    /// <summary>Default path for job templates ConfigMap mount.</summary>
    internal const string DefaultJobTemplatesPath = "/app/config/job-templates.yaml";

    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly ILeaderElectionService _leaderElection;
    private readonly DispatchLifecycleService _lifecycle;
    private readonly DispatchServiceOptions _options;
    private readonly JobTemplateStore _templateProvider;
    private readonly ILabelSwapService? _labelSwapper;
    private readonly IAgentProfileStore? _agentProfileStore;
    private readonly IOrchestratorRunService? _runService;
    private readonly DispatchEligibilityChecker _eligibilityChecker;
    private readonly TokenBucketRateLimiter _rateLimiter;
    private readonly DispatchStateBuilder _stateBuilder;
    private volatile bool _startupValidationRun;

    internal DispatchService(
        DispatchServiceCoreDependencies coreDeps,
        IConfiguration configuration)
        : this(coreDeps, configuration,
               LoadTemplateProvider(configuration))
    { }

    /// <summary>
    /// Constructor overload accepting a pre-built JobTemplateStore (for testing).
    /// </summary>
    internal DispatchService(
        DispatchServiceCoreDependencies coreDeps,
        IConfiguration configuration,
        JobTemplateStore templateProvider)
    {
        _dbFactory = coreDeps.DbFactory;
        _leaderElection = coreDeps.LeaderElection;
        _lifecycle = coreDeps.Lifecycle;
        _labelSwapper = coreDeps.LabelSwapper;
        _agentProfileStore = coreDeps.AgentProfileStore;
        _runService = coreDeps.RunService;
        _templateProvider = templateProvider;
        _options = DispatchServiceOptionsFactory.Create(configuration);
        _eligibilityChecker = new DispatchEligibilityChecker(_templateProvider, _agentProfileStore);
        _rateLimiter = RateLimiterFactory.CreateTokenBucket(_options.RateLimitPerSecond);
        // TODO: The null-coalescing fallback here silently constructs a live DispatchStateBuilder when
        // coreDeps.StateBuilder is not provided (e.g. in tests that omit it). In production the DI-injected
        // singleton is always passed, so this path is never taken at runtime. If _dbFactory or _lifecycle
        // are null in a test scenario, the new DispatchStateBuilder(...) expression will compile fine
        // but throw a NullReferenceException at the first BuildStateAsync call rather than at construction,
        // making failures harder to diagnose. Consider removing the fallback and requiring StateBuilder
        // explicitly. See DotNetSpecialist WARNING (Issue #1910).
        _stateBuilder = coreDeps.StateBuilder ?? new DispatchStateBuilder(
            _dbFactory,
            _lifecycle,
            _templateProvider,
            new DispatchTemplateResolver(_agentProfileStore, _templateProvider),
            _options);
    }

    internal static JobTemplateStore LoadTemplateProvider(IConfiguration configuration)
    {
        var templatesPath = configuration.GetValue<string>("WorkDistribution:JobTemplatesPath") ?? DefaultJobTemplatesPath;
        // Also check .json path for format flexibility
        if (!File.Exists(templatesPath) && templatesPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            var jsonFallback = Path.ChangeExtension(templatesPath, ".json");
            if (File.Exists(jsonFallback))
                templatesPath = jsonFallback;
        }
        var provider = JobTemplateStore.LoadFromFile(templatesPath);
        Log.Information("Loaded {Count} job template(s) from {Path}",
            provider.GetAllTemplates().Count, templatesPath);
        return provider;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Log.Information("DispatchService started — waiting for leader election");

        while (!stoppingToken.IsCancellationRequested)
        {
            await WaitForLeadershipAsync(stoppingToken);

            if (stoppingToken.IsCancellationRequested) break;

            Log.Information("DispatchService: leader acquired, entering poll loop");

            // Reset so validation re-runs on each leadership tenure.
            // Allows detection of ConfigMap changes during leadership loss/re-acquisition.
            _startupValidationRun = false;

            // Create linked token: cancels on EITHER host stop OR leadership loss.
            // The using block must remain here so the CTS is disposed after RunLeaderPollLoopAsync
            // returns — not inside the extracted method.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                stoppingToken, _leaderElection.LeaderToken);
            var ct = linked.Token;

            await RunLeaderPollLoopAsync(ct);

            if (!stoppingToken.IsCancellationRequested)
            {
                Log.Information("DispatchService: leadership lost, re-entering wait loop");
            }
        }

        Log.Information("DispatchService: exiting (stopping)");
    }

    /// <summary>
    /// Waits until this instance becomes the leader or <paramref name="stoppingToken"/> is cancelled.
    /// </summary>
    private async Task WaitForLeadershipAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && !_leaderElection.IsLeader)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    /// <summary>
    /// Runs the poll loop while this instance holds leadership. Returns when
    /// <paramref name="linkedCt"/> is cancelled (either host stop or leadership loss).
    /// </summary>
    private async Task RunLeaderPollLoopAsync(CancellationToken linkedCt)
    {
        while (!linkedCt.IsCancellationRequested)
        {
            try
            {
                await PollAndDispatchAsync(linkedCt);
            }
            catch (OperationCanceledException) when (linkedCt.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "DispatchService: unhandled error in poll cycle");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), linkedCt);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _rateLimiter.Dispose();
        base.Dispose();
    }

    private async Task PollAndDispatchAsync(CancellationToken ct)
    {
        await RunStartupValidationIfNeededAsync(ct);

        var state = await _stateBuilder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: true,
            ct);
        if (state is null)
            return;

        await using (state.Db)
        {
            foreach (var item in state.PendingItems)
            {
                if (!await ProcessDispatchCandidateAsync(state.Db, item,
                        state.ConcurrencyBySelector, state.AvailablePvcs, ct))
                    break;
            }
        }

        // TODO: DispatcherPollCount is now incremented unconditionally after every poll that found pending items
        // (i.e. when state is non-null). Previously it was only incremented on the early-return path when
        // pendingItems.Count == 0 (the "nothing to do" poll). This is a behavioral change: dashboards and
        // alerts keyed on this counter will see higher values after deployment. If the intent is to count
        // ALL polls (empty + non-empty), this is correct — but it should be documented. If the original
        // intent was to count only empty polls, remove this call and leave it solely inside BuildStateAsync.
        // See Correctness WARNING (Issue #1910).
        WorkDistributionTelemetry.DispatcherPollCount.Add(1);
    }

    /// <summary>
    /// Runs startup validation once per leadership tenure: warns about enabled AgentProfiles
    /// with no matching JobTemplate. K8s mode only — templates are static for the pod lifetime.
    /// </summary>
    private async Task RunStartupValidationIfNeededAsync(CancellationToken ct)
    {
        if (_startupValidationRun)
            return;
        _startupValidationRun = true;
        if (_agentProfileStore is not null)
        {
            var profiles = await _agentProfileStore.LoadAgentProfilesAsync(ct);
            await ValidateAgentProfileTemplateMappingAsync(profiles, _templateProvider, Log);
        }
    }

    /// <summary>
    /// Processes a single pending work item through eligibility checks and dispatch.
    /// Returns false if the dispatch loop should stop (rate limit hit or cancellation).
    /// </summary>
    private async Task<bool> ProcessDispatchCandidateAsync(
        PipelineDbContext db,
        PendingWorkItemProjection item,
        Dictionary<string, int> concurrencyBySelector,
        List<string> availablePvcs,
        CancellationToken ct)
    {
        if (ct.IsCancellationRequested || !_leaderElection.IsLeader)
            return false;

        using var lease = await _rateLimiter.AcquireAsync(1, ct);
        if (!lease.IsAcquired)
        {
            Log.Warning("DispatchService: rate limit hit, stopping dispatch cycle");
            return false;
        }

        var result = await _eligibilityChecker.CheckEligibilityAsync(item, concurrencyBySelector, availablePvcs.Count, ct);

        // TODO: Add explicit default/Eligible case to prevent silent fall-through if new EligibilityOutcome values are added
        switch (result.Outcome)
        {
            case EligibilityOutcome.AtConcurrencyLimit:
            case EligibilityOutcome.NoPvcAvailable:
                return true;
            case EligibilityOutcome.NoTemplate:
                await _lifecycle.FailWorkItemAsync(item.Id, result.ErrorMessage!, ct);
                return true;
        }

        await DispatchSingleItemAsync(db, item, result.Template!, result.IsKiroAgent, availablePvcs, concurrencyBySelector, ct);
        return true;
    }

    private async Task DispatchSingleItemAsync(
        PipelineDbContext db,
        PendingWorkItemProjection item,
        JobTemplate template,
        bool isKiroAgent,
        List<string> availablePvcs,
        Dictionary<string, int> concurrencyBySelector,
        CancellationToken ct)
    {
        await _lifecycle.ExecuteDispatchLifecycleAsync(
            new DispatchLifecycleContext(db, item, template, isKiroAgent, availablePvcs, concurrencyBySelector, ""),
            async _ =>
            {
                // Load project secrets if project has them
                Dictionary<string, string>? projectSecrets = null;
                if (!string.IsNullOrEmpty(item.ProjectId))
                {
                    projectSecrets = await DispatchLifecycleService.LoadProjectSecretsAsync(db, item.ProjectId, ct);
                }
                return (true, projectSecrets);
            },
            async workItem =>
            {
                // Update in-memory PipelineRun StartedAt to actual dispatch time (BUG-14 fix).
                // Without this, StartedAt reflects preparation/enqueue time which can be
                // hours earlier for queued work, inflating the Duration shown in the UI.
                _runService?.GetRun(item.Id.ToString())?.ResetStartedAt(workItem.DispatchedAt!.Value);

                // Swap issue label to agent:in-progress — delegates to shared LabelSwapService
                // which handles reconciliation flagging on failure. (#1868)
                // TODO: Behavior change from old fire-and-forget: SwapLabelWithRetryAsync can now
                // propagate OperationCanceledException during shutdown (the old code swallowed all
                // exceptions). The item is already persisted as Dispatched before this runs, so OCE
                // here only interrupts the label swap — not the dispatch itself. The outer poll loop
                // handles OCE gracefully (breaks the loop). This is intentional per #1868 acceptance
                // criteria, but callers should be aware the swap is no longer purely non-fatal.
                // See review finding: Correctness WARNING DispatchService.cs:320
                if (_labelSwapper is not null &&
                    !string.IsNullOrEmpty(item.IssueIdentifier) &&
                    !string.IsNullOrEmpty(item.IssueProviderConfigId))
                {
                    await _labelSwapper.SwapLabelWithRetryAsync(
                        item.Id,
                        item.IssueProviderConfigId,
                        item.IssueIdentifier,
                        LabelTargetKind.Issue,
                        ct);
                }
            },
            ct);
    }

    // ── Static helpers (internal for testability) ────────────────────────

    /// <summary>
    /// Generates deterministic K8s Job name: caa-{workItemId first 8 hex chars}.
    /// </summary>
    internal static string GenerateJobName(Guid workItemId)
        => $"caa-{workItemId.ToString("N")[..8]}";

    /// <summary>
    /// Normalizes agent selector by sorting labels and joining with comma.
    /// Delegates to <see cref="JobTemplateStore.NormalizeLabels"/>.
    /// </summary>
    internal static string NormalizeSelector(string agentSelector)
        => JobTemplateStore.NormalizeLabels(agentSelector);

    /// <summary>
    /// Calculates available PVCs from the configured pool minus currently claimed.
    /// Exposed for property testing.
    /// </summary>
    internal static List<string> CalculateAvailablePvcs(
        IReadOnlyList<string> configuredPvcs,
        IEnumerable<string> claimedPvcs)
    {
        return configuredPvcs
            .Except(claimedPvcs, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Validates that every enabled <see cref="AgentProfile"/> has a matching entry in the
    /// <see cref="JobTemplateStore"/>. Logs a warning for each profile whose
    /// <see cref="AgentProfile.MatchLabels"/> do not resolve to any template.
    /// <para>
    /// Called once per leadership tenure at the start of the first poll cycle.
    /// K8s mode only: templates are loaded from a static ConfigMap mount and do not change
    /// for the lifetime of the pod, so there are no false positives.
    /// </para>
    /// </summary>
    /// <returns>Display names of profiles with no matching template (for testing).</returns>
    internal static async Task<IReadOnlyList<string>> ValidateAgentProfileTemplateMappingAsync(
        IReadOnlyList<AgentProfile> profiles,
        JobTemplateStore templateStore,
        ILogger logger)
    {
        var missing = new List<string>();

        foreach (var profile in profiles)
        {
            if (!profile.Enabled || profile.MatchLabels.Count == 0)
                continue;

            var selector = NormalizeSelector(string.Join(",", profile.MatchLabels));
            if (templateStore.Resolve(selector) is null)
            {
                missing.Add(profile.DisplayName);
                logger.Warning(
                    "DispatchService: AgentProfile '{ProfileName}' (labels=[{Labels}]) has no matching JobTemplate. " +
                    "Work items requiring this profile will fail with 'No job template for selector'. " +
                    "Add a job template with labels matching [{MatchLabels}] to the job-templates ConfigMap.",
                    profile.DisplayName,
                    selector,
                    selector);
            }
        }

        if (missing.Count == 0)
            logger.Information("DispatchService: startup validation — all {Count} enabled profile(s) have a matching job template",
                profiles.Count(p => p.Enabled));

        return await Task.FromResult<IReadOnlyList<string>>(missing);
    }

}
