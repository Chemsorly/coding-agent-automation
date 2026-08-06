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
    private readonly ILabelService? _labelService;
    private readonly IAgentProfileStore? _agentProfileStore;
    private readonly IOrchestratorRunService? _runService;
    private readonly DispatchEligibilityChecker _eligibilityChecker;
    private readonly TokenBucketRateLimiter _rateLimiter;
    // TODO: [WARNING] _startupValidationRun is declared volatile to ensure reads/writes are not
    // reordered by the JIT or CPU. However, the check-then-set pattern in RunStartupValidationIfNeededAsync
    // is not atomic — two concurrent threads could both pass the `if (!_startupValidationRun)` check before
    // either sets the flag to true. In practice this is benign (duplicate validation is just redundant log
    // noise), and no concurrent reader outside the poll loop is visible in the codebase. If a future
    // health-check or config-reload path reads this field from a different thread, consider using
    // Interlocked.CompareExchange for a proper check-and-set.
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
        _labelService = coreDeps.LabelService;
        _agentProfileStore = coreDeps.AgentProfileStore;
        _runService = coreDeps.RunService;
        _templateProvider = templateProvider;
        _options = DispatchServiceOptionsFactory.Create(configuration);
        _eligibilityChecker = new DispatchEligibilityChecker(_templateProvider, _agentProfileStore);
        _rateLimiter = RateLimiterFactory.CreateTokenBucket(_options.RateLimitPerSecond);
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
            // Wait for leadership
            while (!stoppingToken.IsCancellationRequested && !_leaderElection.IsLeader)
            {
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }

            if (stoppingToken.IsCancellationRequested) break;

            await RunLeadershipTenureAsync(stoppingToken);
        }

        Log.Information("DispatchService: exiting (stopping)");
    }

    /// <summary>
    /// Runs the dispatch poll loop for a single leadership tenure.
    /// Returns when leadership is lost or <paramref name="stoppingToken"/> is cancelled.
    /// </summary>
    private async Task RunLeadershipTenureAsync(CancellationToken stoppingToken)
    {
        Log.Information("DispatchService: leader acquired, entering poll loop");

        // Reset so validation re-runs on each leadership tenure.
        // Allows detection of ConfigMap changes during leadership loss/re-acquisition.
        _startupValidationRun = false;

        // Create linked token: cancels on EITHER host stop OR leadership loss
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken, _leaderElection.LeaderToken);
        var ct = linked.Token;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await PollAndDispatchAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "DispatchService: unhandled error in poll cycle");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        if (!stoppingToken.IsCancellationRequested)
        {
            Log.Information("DispatchService: leadership lost, re-entering wait loop");
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

        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        var pendingItems = await db.WorkItems
            .Where(w => w.Status == WorkItemStatus.Pending && w.TaskType != WorkItemTaskType.Consolidation)
            .OrderBy(w => w.CreatedAt)
            .Select(w => new PendingWorkItemProjection
            {
                Id = w.Id,
                AgentSelector = w.AgentSelector,
                CreatedAt = w.CreatedAt,
                TimeoutSeconds = w.TimeoutSeconds,
                ProjectId = w.ProjectId,
                IssueIdentifier = w.IssueIdentifier,
                IssueProviderConfigId = w.IssueProviderConfigId,
                TaskType = w.TaskType
            })
            .ToListAsync(ct);

        WorkDistributionTelemetry.RecordLastPollEpoch();

        if (pendingItems.Count == 0)
        {
            WorkDistributionTelemetry.DispatcherPollCount.Add(1);
            return;
        }

        var (concurrencyBySelector, availablePvcs) = await BuildDispatchStateAsync(db, ct);

        foreach (var item in pendingItems)
        {
            if (!await ProcessDispatchCandidateAsync(db, item, concurrencyBySelector, availablePvcs, ct))
                break;
        }

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

    /// <summary>
    /// Queries the database to build concurrency state (active counts per selector group)
    /// and determines available PVCs for kiro agents.
    /// </summary>
    // TODO: The DB GROUP BY aggregation that produces the concurrency map (formerly tested by
    // DispatchStateBuilderTests.BuildStateAsync_BuildsConcurrencyMap) no longer has a dedicated
    // unit test. The logic is covered only incidentally by higher-level lifecycle tests. Consider
    // adding a direct test for BuildDispatchStateAsync that seeds Dispatched/Running items and
    // asserts the resulting dictionary counts per selector.
    private async Task<(Dictionary<string, int> ConcurrencyBySelector, List<string> AvailablePvcs)> BuildDispatchStateAsync(
        PipelineDbContext db, CancellationToken ct)
    {
        var activeCounts = await db.WorkItems
            .Where(w => w.Status == WorkItemStatus.Dispatched || w.Status == WorkItemStatus.Running)
            .GroupBy(w => w.AgentSelector)
            .Select(g => new { Selector = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var concurrencyBySelector = activeCounts.ToDictionary(x => x.Selector, x => x.Count);

        var pvcResult = await DispatchLifecycleService.QueryAvailablePvcsAsync(db, _options.KiroPvcPool, ct);
        WorkDistributionTelemetry.UpdateCredentialPoolMetrics(pvcResult.AvailablePvcs.Count, pvcResult.ClaimedCount);
        var availablePvcs = pvcResult.AvailablePvcs;

        return (concurrencyBySelector, availablePvcs);
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
            async workItem => await OnPipelineDispatchSuccessAsync(workItem, item, ct),
            ct);
    }

    /// <summary>
    /// Post-dispatch success callback for pipeline work items.
    /// Resets the in-memory run's StartedAt to actual dispatch time (BUG-14 fix) and
    /// swaps the issue label to <c>agent:in-progress</c> (best-effort, non-fatal).
    /// </summary>
    private async Task OnPipelineDispatchSuccessAsync(
        WorkItemEntity workItem, PendingWorkItemProjection item, CancellationToken ct)
    {
        // Update in-memory PipelineRun StartedAt to actual dispatch time (BUG-14 fix).
        // Without this, StartedAt reflects preparation/enqueue time which can be
        // hours earlier for queued work, inflating the Duration shown in the UI.
        _runService?.GetRun(item.Id.ToString())?.ResetStartedAt(workItem.DispatchedAt!.Value);

        // Swap issue label to agent:in-progress (non-fatal — best effort)
        if (_labelService is not null &&
            !string.IsNullOrEmpty(item.IssueIdentifier) &&
            !string.IsNullOrEmpty(item.IssueProviderConfigId))
        {
            try
            {
                await _labelService.SwapLabelAsync(
                    item.IssueProviderConfigId, item.IssueIdentifier, AgentLabels.InProgress, ct);
            }
            catch (Exception ex)
            {
                Log.Warning(ex,
                    "DispatchService: failed to swap label to agent:in-progress for {IssueIdentifier}",
                    item.IssueIdentifier);
            }
        }
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
