using System.Threading.RateLimiting;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Orchestration.Telemetry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// K8s mode only: polls WorkItems WHERE Status=Pending AND TaskType!=Consolidation ORDER BY CreatedAt ASC,
/// resolves container image via JobTemplateStore, creates K8s Jobs via JobSpecBuilder,
/// updates to Dispatched. Runs under leader election (same Lease as PipelineLoopService).
/// Rate-limited: default 10 Jobs/s. Skips items whose selector group is at concurrency limit.
/// Consolidation items are handled by <see cref="ConsolidationDispatchHandler"/>.
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
    private readonly DispatchTemplateResolver _templateResolver;
    private readonly ILabelService? _labelService;
    private readonly IOrchestratorRunService? _runService;
    private readonly TokenBucketRateLimiter _rateLimiter;
    private readonly DispatchStateBuilder _stateBuilder;

    protected override string ServiceName => "DispatchService";
    protected override int PollIntervalSeconds => _options.PollIntervalSeconds;

    internal DispatchService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        ILeaderElectionService leaderElection,
        DispatchLifecycleService lifecycle,
        IConfiguration configuration,
        ILabelService? labelService = null,
        IAgentProfileStore? agentProfileStore = null,
        IOrchestratorRunService? runService = null)
        : this(dbFactory, leaderElection, lifecycle, configuration,
               LoadTemplateProvider(configuration), labelService,
               agentProfileStore, runService)
    { }

    /// <summary>
    /// Constructor overload accepting a pre-built JobTemplateStore (for testing).
    /// </summary>
    internal DispatchService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        ILeaderElectionService leaderElection,
        DispatchLifecycleService lifecycle,
        IConfiguration configuration,
        JobTemplateStore templateProvider,
        ILabelService? labelService = null,
        IAgentProfileStore? agentProfileStore = null,
        IOrchestratorRunService? runService = null)
        : base(leaderElection)
    {
        _dbFactory = dbFactory;
        _lifecycle = lifecycle;
        _labelService = labelService;
        _runService = runService;
        _templateProvider = templateProvider;
        _templateResolver = new DispatchTemplateResolver(agentProfileStore, templateProvider);
        _options = DispatchServiceOptionsFactory.Create(configuration);
        _rateLimiter = _options.CreateRateLimiter();
        _stateBuilder = new DispatchStateBuilder(dbFactory, lifecycle, templateProvider, _templateResolver, _options);
    }

    /// <summary>
    /// Test constructor accepting pre-built options (skips IConfiguration binding).
    /// </summary>
    internal DispatchService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        ILeaderElectionService leaderElection,
        DispatchLifecycleService lifecycle,
        JobTemplateStore templateProvider,
        DispatchServiceOptions options,
        ILabelService? labelService = null,
        IAgentProfileStore? agentProfileStore = null,
        IOrchestratorRunService? runService = null)
        : base(leaderElection)
    {
        _dbFactory = dbFactory;
        _lifecycle = lifecycle;
        _labelService = labelService;
        _runService = runService;
        _templateProvider = templateProvider;
        _templateResolver = new DispatchTemplateResolver(agentProfileStore, templateProvider);
        _options = options;
        _rateLimiter = _options.CreateRateLimiter();
        _stateBuilder = new DispatchStateBuilder(dbFactory, lifecycle, templateProvider, _templateResolver, _options);
    }

    private static JobTemplateStore LoadTemplateProvider(IConfiguration configuration)
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
        Log.Information("DispatchService: loaded {Count} job template(s) from {Path}",
            provider.GetAllTemplates().Count, templatesPath);
        return provider;
    }

    protected override Task OnPollCycleAsync(CancellationToken ct) => PollAndDispatchAsync(ct);

    /// <inheritdoc/>
    public override void Dispose()
    {
        _rateLimiter.Dispose();
        base.Dispose();
    }

    private async Task PollAndDispatchAsync(CancellationToken ct)
    {
        var state = await _stateBuilder.BuildStateAsync(
            w => w.TaskType != WorkItemTaskType.Consolidation,
            recordTelemetry: true,
            ct);

        if (state is null)
            return;

        await using (state.Db)
        {
            await foreach (var candidate in _stateBuilder.GetEligibleCandidatesAsync(
                state, LeaderElection, _rateLimiter, nameof(DispatchService),
                async (item, errorMessage, innerCt) =>
                {
                    await _lifecycle.FailWorkItemAsync(item.Id, errorMessage, innerCt);
                },
                ct))
            {
                await DispatchSingleItemAsync(state.Db, candidate.Item, candidate.Template,
                    candidate.IsKiroAgent, state.AvailablePvcs, state.ConcurrencyBySelector, ct);
            }

            WorkDistributionTelemetry.DispatcherPollCount.Add(1);
        }
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
        await _lifecycle.ExecuteDispatchLifecycleAsync(db, item, template, isKiroAgent, availablePvcs, concurrencyBySelector, "",
            async _ =>
            {
                // Load project secrets if project has them
                Dictionary<string, string>? projectSecrets = null;
                if (!string.IsNullOrEmpty(item.ProjectId))
                {
                    projectSecrets = await _lifecycle.LoadProjectSecretsAsync(db, item.ProjectId, ct);
                }
                return (true, projectSecrets);
            },
            async workItem =>
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
}
