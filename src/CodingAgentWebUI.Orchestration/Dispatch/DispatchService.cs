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
    {
        _dbFactory = dbFactory;
        _leaderElection = leaderElection;
        _lifecycle = lifecycle;
        _labelService = labelService;
        _agentProfileStore = agentProfileStore;
        _runService = runService;
        _templateProvider = templateProvider;
        _options = new DispatchServiceOptions();
        InitializeOptions(configuration);
        _eligibilityChecker = new DispatchEligibilityChecker(_templateProvider, _agentProfileStore);
        _rateLimiter = CreateRateLimiter();
    }

    /// <summary>
    /// Reads configuration values and binds them to <see cref="_options"/>.
    /// Called from the primary constructor; the public constructor delegates here via chaining.
    /// </summary>
    // TODO: Replace with DispatchServiceOptionsFactory.Create() to eliminate duplication with ConsolidationDispatchHandler
    private void InitializeOptions(IConfiguration configuration)
    {
        configuration.GetSection("WorkDistribution:Dispatch").Bind(_options);

        var pvcList = configuration.GetSection("WorkDistribution:CredentialPools:Kiro").Get<List<string>>();
        if (pvcList is not null)
            _options.KiroPvcPool = pvcList;

        _options.OrchestratorUrl = configuration.GetValue<string>("WorkDistribution:OrchestratorUrl") ?? "";
        _options.AgentApiKeySecretName = configuration.GetValue<string>("WorkDistribution:AgentApiKeySecretName") ?? "";
        _options.AgentServiceAccountName = configuration.GetValue<string>("WorkDistribution:AgentServiceAccountName") ?? "";
        _options.Namespace = configuration.GetValue<string>("WorkDistribution:Namespace")
            ?? Environment.GetEnvironmentVariable("POD_NAMESPACE")
            ?? "default";
        _options.OpencodeConfigSecretName = configuration.GetValue<string>("WorkDistribution:OpencodeConfigSecretName") ?? "";
    }

    // TODO: Replace with DispatchServiceOptions.CreateRateLimiter() to eliminate duplication with ConsolidationDispatchHandler
    private TokenBucketRateLimiter CreateRateLimiter() => new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = _options.RateLimitPerSecond,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = 0,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        TokensPerPeriod = _options.RateLimitPerSecond,
        AutoReplenishment = true
    });

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

            Log.Information("DispatchService: leader acquired, entering poll loop");

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

        Log.Information("DispatchService: exiting (stopping)");
    }

    /// <inheritdoc/>
    public override void Dispose()
    {
        _rateLimiter.Dispose();
        base.Dispose();
    }

    private async Task PollAndDispatchAsync(CancellationToken ct)
    {
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
            if (ct.IsCancellationRequested || !_leaderElection.IsLeader)
                break;

            using var lease = await _rateLimiter.AcquireAsync(1, ct);
            if (!lease.IsAcquired)
            {
                Log.Warning("DispatchService: rate limit hit, stopping dispatch cycle");
                break;
            }

            var result = await _eligibilityChecker.CheckEligibilityAsync(item, concurrencyBySelector, availablePvcs.Count, ct);

            // TODO: Add explicit default/Eligible case to prevent silent fall-through if new EligibilityOutcome values are added
            switch (result.Outcome)
            {
                case EligibilityOutcome.AtConcurrencyLimit:
                case EligibilityOutcome.NoPvcAvailable:
                    continue;
                case EligibilityOutcome.NoTemplate:
                    await _lifecycle.FailWorkItemAsync(item.Id, result.ErrorMessage!, ct);
                    continue;
            }

            await DispatchSingleItemAsync(db, item, result.Template!, result.IsKiroAgent, availablePvcs, concurrencyBySelector, ct);
        }

        WorkDistributionTelemetry.DispatcherPollCount.Add(1);
    }

    /// <summary>
    /// Queries the database to build concurrency state (active counts per selector group)
    /// and determines available PVCs for kiro agents.
    /// </summary>
    // TODO: Consider reusing DispatchLifecycleService.QueryAvailablePvcsAsync instead of inlining PVC logic (duplicated in ConsolidationDispatchHandler)
    private async Task<(Dictionary<string, int> ConcurrencyBySelector, List<string> AvailablePvcs)> BuildDispatchStateAsync(
        PipelineDbContext db, CancellationToken ct)
    {
        var activeCounts = await db.WorkItems
            .Where(w => w.Status == WorkItemStatus.Dispatched || w.Status == WorkItemStatus.Running)
            .GroupBy(w => w.AgentSelector)
            .Select(g => new { Selector = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        var concurrencyBySelector = activeCounts.ToDictionary(x => x.Selector, x => x.Count);

        var claimedPvcs = await db.WorkItems
            .Where(w => w.ClaimedPvcName != null &&
                        (w.Status == WorkItemStatus.Pending ||
                         w.Status == WorkItemStatus.Dispatched ||
                         w.Status == WorkItemStatus.Running))
            .Select(w => w.ClaimedPvcName!)
            .ToListAsync(ct);
        var inflightClaims = _lifecycle.GetInflightPvcClaims();
        var availablePvcs = _options.KiroPvcPool
            .Except(claimedPvcs, StringComparer.Ordinal)
            .Where(pvc => !inflightClaims.Contains(pvc))
            .ToList();
        WorkDistributionTelemetry.UpdateCredentialPoolMetrics(availablePvcs.Count, claimedPvcs.Count);

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
