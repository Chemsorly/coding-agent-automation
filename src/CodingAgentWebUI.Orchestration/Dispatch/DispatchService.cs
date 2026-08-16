using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Orchestration.Telemetry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// K8s mode only: polls WorkItems WHERE Status=Pending AND TaskType!=Consolidation ORDER BY CreatedAt ASC,
/// resolves container image via JobTemplateStore, creates K8s Jobs via JobSpecBuilder,
/// updates to Dispatched. Runs under leader election (same Lease as PipelineLoopService).
/// Rate-limited: default 10 Jobs/s. Skips items whose selector group is at concurrency limit.
/// Consolidation items are handled by <see cref="ConsolidationWorkItemDispatchService"/>.
/// </summary>
public sealed class DispatchService : LeaderElectedPollingService
{
    private static readonly ILogger Log = Serilog.Log.ForContext<DispatchService>();

    /// <summary>Default path for job templates ConfigMap mount.</summary>
    internal const string DefaultJobTemplatesPath = "/app/config/job-templates.yaml";

    private readonly IDbContextFactory<PipelineDbContext> _dbFactory;
    private readonly DispatchLifecycleService _lifecycle;
    private readonly DispatchServiceOptions _options;
    private readonly JobTemplateStore _templateProvider;
    private readonly ILabelSwapService? _labelSwapper;
    private readonly IAgentProfileStore? _agentProfileStore;
    private readonly IOrchestratorRunService? _runService;
    private readonly DispatchEligibilityChecker _eligibilityChecker;
    private readonly DispatchStateBuilder _stateBuilder;
    private volatile bool _startupValidationRun;

    protected override string ServiceName => "DispatchService";
    protected override int PollIntervalSeconds => _options.PollIntervalSeconds;

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
        // TODO: coreDeps and configuration are not null-guarded before the base-constructor initializer
        // dereferences them (coreDeps.LeaderElection, DispatchServiceOptionsFactory.Create(configuration)).
        // A null coreDeps produces NullReferenceException instead of ArgumentNullException; a null configuration
        // produces NullReferenceException inside the factory rather than at the call boundary.
        // The sibling (coreDeps, templateProvider, options) constructor correctly guards with
        // (coreDeps ?? throw new ArgumentNullException(nameof(coreDeps))). Apply the same pattern here.
        // See DotNetSpecialist WARNING (Issue #1994).
        : base(coreDeps.LeaderElection, DispatchServiceOptionsFactory.Create(configuration).RateLimitPerSecond)
    {
        _dbFactory = coreDeps.DbFactory;
        _lifecycle = coreDeps.Lifecycle;
        _labelSwapper = coreDeps.LabelSwapper;
        _agentProfileStore = coreDeps.AgentProfileStore;
        _runService = coreDeps.RunService;
        _templateProvider = templateProvider;
        // TODO: DispatchServiceOptionsFactory.Create(configuration) is called twice: once in the base-constructor
        // initializer expression (to extract RateLimitPerSecond) and again here to populate _options. This creates
        // two DispatchServiceOptions instances from the same IConfiguration. If the factory is ever made non-idempotent
        // or side-effectful, the rate limiter and _options.PollIntervalSeconds could silently diverge.
        // Fix: use a `this(...)` constructor chain or a static helper to resolve options once before base() is called.
        // See DotNetSpecialist WARNING (Issue #1912).
        _options = DispatchServiceOptionsFactory.Create(configuration);
        _eligibilityChecker = new DispatchEligibilityChecker(_templateProvider, _agentProfileStore);
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

    /// <summary>
    /// Test-only constructor accepting a pre-built <see cref="DispatchServiceOptions"/> instead of
    /// <see cref="IConfiguration"/>. Avoids the double-call to
    /// <see cref="DispatchServiceOptionsFactory.Create"/> and allows precise option injection in tests.
    /// Throws <see cref="ArgumentNullException"/> if <paramref name="coreDeps"/> or
    /// <paramref name="options"/> is null, including when <c>coreDeps.StateBuilder</c> is null
    /// (enforcing the AC4 requirement from Issue #1989).
    /// </summary>
    internal DispatchService(
        DispatchServiceCoreDependencies coreDeps,
        JobTemplateStore templateProvider,
        DispatchServiceOptions options)
        : base((coreDeps ?? throw new ArgumentNullException(nameof(coreDeps))).LeaderElection,
               (options ?? throw new ArgumentNullException(nameof(options))).RateLimitPerSecond)
    {
        if (coreDeps.StateBuilder is null)
            throw new ArgumentNullException("StateBuilder",
                "DispatchService requires a non-null StateBuilder. Provide it via coreDeps.StateBuilder.");

        _dbFactory = coreDeps.DbFactory;
        _lifecycle = coreDeps.Lifecycle;
        _labelSwapper = coreDeps.LabelSwapper;
        _agentProfileStore = coreDeps.AgentProfileStore;
        _runService = coreDeps.RunService;
        _templateProvider = templateProvider ?? throw new ArgumentNullException(nameof(templateProvider));
        _options = options;
        _eligibilityChecker = new DispatchEligibilityChecker(_templateProvider, _agentProfileStore);
        _stateBuilder = coreDeps.StateBuilder;
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

    /// <summary>
    /// Resets <see cref="_startupValidationRun"/> at the start of each leadership tenure so that
    /// startup validation re-runs on re-acquisition (e.g. to detect ConfigMap changes).
    /// The base class emits the "leader acquired, entering poll loop" log message.
    /// </summary>
    protected override async Task RunLeadershipTermAsync(CancellationToken ct)
    {
        _startupValidationRun = false;
        await base.RunLeadershipTermAsync(ct);
    }

    /// <inheritdoc/>
    protected override Task OnPollCycleAsync(CancellationToken ct) => PollAndDispatchAsync(ct);

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
        if (ct.IsCancellationRequested || !LeaderElection.IsLeader)
            return false;

        // TODO: The rate limiter lease is acquired here and held for the entire duration of
        // CheckEligibilityAsync + DispatchSingleItemAsync. If DispatchSingleItemAsync blocks on I/O
        // (database write, HTTP call to the agent API), the token-bucket slot is occupied for the
        // full dispatch duration rather than just the dispatch-decision window. This changes the
        // effective rate from "N dispatches initiated per second" to "N concurrent dispatches per second".
        // With a burst size of 1 and a slow DispatchSingleItemAsync, the loop will process at most one
        // item per dispatch duration, not per second. Consider acquiring the lease only around the
        // dispatch-decision window (eligibility check + job submission trigger) and releasing it
        // before awaiting the full dispatch lifecycle. See Correctness WARNING (Issue #1994).
        using var lease = await (RateLimiter ?? throw new InvalidOperationException(
            "DispatchService requires a rate limiter but RateLimiter is null. " +
            "Ensure the constructor passes rateLimitPerSecond to the base class."))
            .AcquireAsync(1, ct);
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
                // TODO: result.ErrorMessage! uses a null-forgiving operator. Currently safe because
                // EligibilityResult.NoTemplate(string errorMessage) always sets ErrorMessage to a non-null value.
                // If EligibilityResult is ever constructed directly without the factory (e.g. new EligibilityResult
                // { Outcome = EligibilityOutcome.NoTemplate }), ErrorMessage will be null and FailWorkItemAsync
                // receives a null argument without a compile-time warning. Consider guarding:
                // result.ErrorMessage ?? "No matching template found"
                // See Correctness/DotNetSpecialist WARNING (Issue #1994).
                await _lifecycle.FailWorkItemAsync(item.Id, result.ErrorMessage!, ct);
                return true;
        }

        // TODO: result.Template! uses a null-forgiving operator on the EligibilityResult.Template property.
        // The implicit fall-through (eligible) path of the switch is reached for every outcome not explicitly
        // listed, including any future EligibilityOutcome values added to the enum. If a new outcome is added
        // where the checker returns a non-null outcome but a null Template, DispatchSingleItemAsync receives
        // a null JobTemplate with no compile-time or runtime guard, producing a NullReferenceException deep
        // inside the dispatch lifecycle. Adding a default: return true; arm (already noted in the TODO above
        // the switch) would prevent silent fall-through, and replacing ! with
        // result.Template ?? throw new InvalidOperationException(...) would complete the guard.
        // See DotNetSpecialist WARNING (Issue #1994).
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
                // which handles retry and reconciliation flagging on failure. (#1868)
                // Note: unlike the previous fire-and-forget, SwapLabelWithRetryAsync can propagate
                // OperationCanceledException during shutdown. The item is already persisted as
                // Dispatched before this runs, so OCE only interrupts the label swap. The outer
                // poll loop handles OCE gracefully (breaks the loop).
                if (_labelSwapper is not null &&
                    !string.IsNullOrEmpty(item.IssueIdentifier) &&
                    !string.IsNullOrEmpty(item.IssueProviderConfigId))
                {
                    // TODO: LabelTargetKind.Issue is hardcoded here, but Review (PR) work items
                    // dispatched in K8s mode should use LabelTargetKind.PullRequest with
                    // item.RepoProviderConfigId (matching PendingWorkItemDrainService's run-type
                    // branching logic). This is NOT a regression — the old code also defaulted to
                    // LabelTargetKind.Issue unconditionally — but if K8s mode is intended to handle
                    // PR review work items, this should branch on item.TaskType == WorkItemTaskType.Review
                    // (or a serialized RunType from the payload) and select RepoProviderConfigId +
                    // LabelTargetKind.PullRequest accordingly. Track as follow-up to #1868.
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
