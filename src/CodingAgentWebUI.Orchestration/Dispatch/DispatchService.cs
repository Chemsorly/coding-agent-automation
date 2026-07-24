using System.Threading.RateLimiting;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Orchestration.Telemetry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Serilog;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// K8s mode only: polls WorkItems WHERE Status=Pending AND TaskType!=Consolidation ORDER BY CreatedAt ASC,
/// resolves container image via JobTemplateProvider, creates K8s Jobs via JobSpecBuilder,
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
    private readonly JobTemplateProvider _templateProvider;
    private readonly ILabelService? _labelService;
    private readonly IAgentProfileStore? _agentProfileStore;
    private readonly IOrchestratorRunService? _runService;
    private readonly TokenBucketRateLimiter _rateLimiter;

    internal DispatchService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        ILeaderElectionService leaderElection,
        DispatchLifecycleService lifecycle,
        WorkItemTransitionService transitionService, // TODO: Dead parameter — never stored or used after consolidation extraction. Remove from both constructor overloads and DI registration.
        IConfiguration configuration,
        ILabelService? labelService = null,
        IAgentProfileStore? agentProfileStore = null,
        IOrchestratorRunService? runService = null)
        : this(dbFactory, leaderElection, lifecycle, transitionService, configuration,
               LoadTemplateProvider(configuration), labelService,
               agentProfileStore, runService)
    { }

    /// <summary>
    /// Constructor overload accepting a pre-built JobTemplateProvider (for testing).
    /// </summary>
    internal DispatchService(
        IDbContextFactory<PipelineDbContext> dbFactory,
        ILeaderElectionService leaderElection,
        DispatchLifecycleService lifecycle,
        WorkItemTransitionService transitionService, // TODO: Dead parameter — never stored or used after consolidation extraction. Remove from both constructor overloads and DI registration.
        IConfiguration configuration,
        JobTemplateProvider templateProvider,
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
        _rateLimiter = CreateRateLimiter();
    }

    /// <summary>
    /// Reads configuration values and binds them to <see cref="_options"/>.
    /// Called from the primary constructor; the public constructor delegates here via chaining.
    /// </summary>
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

    private TokenBucketRateLimiter CreateRateLimiter() => new(new TokenBucketRateLimiterOptions
    {
        TokenLimit = _options.RateLimitPerSecond,
        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
        QueueLimit = 0,
        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
        TokensPerPeriod = _options.RateLimitPerSecond,
        AutoReplenishment = true
    });

    private static JobTemplateProvider LoadTemplateProvider(IConfiguration configuration)
    {
        var templatesPath = configuration.GetValue<string>("WorkDistribution:JobTemplatesPath") ?? DefaultJobTemplatesPath;
        // Also check .json path for format flexibility
        if (!File.Exists(templatesPath) && templatesPath.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase))
        {
            var jsonFallback = Path.ChangeExtension(templatesPath, ".json");
            if (File.Exists(jsonFallback))
                templatesPath = jsonFallback;
        }
        var provider = JobTemplateProvider.LoadFromFile(templatesPath);
        Log.Information("DispatchService: loaded {Count} job template(s) from {Path}",
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

    private async Task PollAndDispatchAsync(CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);

        // Column projection — no Payload loading. Excludes consolidation items (handled by ConsolidationDispatchHandler).
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

        // Build concurrency state: count running/dispatched per selector group
        var activeCounts = await db.WorkItems
            .Where(w => w.Status == WorkItemStatus.Dispatched || w.Status == WorkItemStatus.Running)
            .GroupBy(w => w.AgentSelector)
            .Select(g => new { Selector = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        var concurrencyBySelector = activeCounts.ToDictionary(x => x.Selector, x => x.Count);

        // PVC pool: determine available PVCs for kiro agents
        var claimedPvcs = await db.WorkItems
            .Where(w => w.ClaimedPvcName != null &&
                        (w.Status == WorkItemStatus.Pending ||
                         w.Status == WorkItemStatus.Dispatched ||
                         w.Status == WorkItemStatus.Running))
            .Select(w => w.ClaimedPvcName!)
            .ToListAsync(ct);

        var availablePvcs = _options.KiroPvcPool
            .Except(claimedPvcs, StringComparer.Ordinal)
            .ToList();

        // Update credential pool metrics
        WorkDistributionTelemetry.UpdateCredentialPoolMetrics(availablePvcs.Count, claimedPvcs.Count);

        foreach (var item in pendingItems)
        {
            if (ct.IsCancellationRequested || !_leaderElection.IsLeader)
                break;

            // Rate limit
            using var lease = await _rateLimiter.AcquireAsync(1, ct);
            if (!lease.IsAcquired)
            {
                Log.Warning("DispatchService: rate limit hit, stopping dispatch cycle");
                break;
            }

            // Check concurrency limit from template
            var maxConcurrent = _templateProvider.GetMaxConcurrent(item.AgentSelector);
            if (maxConcurrent > 0)
            {
                var current = concurrencyBySelector.GetValueOrDefault(item.AgentSelector, 0);
                if (current >= maxConcurrent)
                {
                    Log.Debug("DispatchService: selector {Selector} at concurrency limit ({Current}/{Max}), skipping {WorkItemId}",
                        item.AgentSelector, current, maxConcurrent, item.Id);
                    continue;
                }
            }

            // Resolve template — fail immediately if no match (before PVC gating)
            var template = _templateProvider.Resolve(item.AgentSelector);
            var effectiveSelector = item.AgentSelector;
            if (template is null)
            {
                // Fallback: AgentSelector might be a subset of the template's label set.
                // Resolve profile to get the full label set, then retry template lookup.
                var (fallbackTemplate, resolvedSelector) = await ResolveTemplateViaProfileAsync(item.AgentSelector, ct);
                if (fallbackTemplate is null)
                {
                    await _lifecycle.FailWorkItemAsync(item.Id, $"No job template for selector: {item.AgentSelector}", ct);
                    continue;
                }
                template = fallbackTemplate;
                effectiveSelector = resolvedSelector!;

                // Re-check concurrency limit against the resolved selector (the actual template key)
                var resolvedMaxConcurrent = template.MaxConcurrent;
                if (resolvedMaxConcurrent > 0)
                {
                    var current = concurrencyBySelector.GetValueOrDefault(effectiveSelector, 0);
                    if (current >= resolvedMaxConcurrent)
                    {
                        Log.Debug("DispatchService: resolved selector {Selector} at concurrency limit ({Current}/{Max}), skipping {WorkItemId}",
                            effectiveSelector, current, resolvedMaxConcurrent, item.Id);
                        continue;
                    }
                }
            }

            var isKiroAgent = string.Equals(template.ProviderType, "kiro", StringComparison.OrdinalIgnoreCase);

            if (isKiroAgent && availablePvcs.Count == 0)
            {
                // No PVC available — skip, leave Pending for next poll cycle (NOT failed)
                Log.Information("DispatchService: no PVC available for kiro agent, skipping WorkItem {WorkItemId}", item.Id);
                continue;
            }

            await DispatchSingleItemAsync(db, item, template, isKiroAgent, availablePvcs, concurrencyBySelector, ct);
        }

        WorkDistributionTelemetry.DispatcherPollCount.Add(1);
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

    /// <summary>
    /// Fallback template resolution: when the work item's AgentSelector is a subset of the template's
    /// label set, resolve the matching profile to get the full MatchLabels, then retry template lookup.
    /// </summary>
    private async Task<(JobTemplate? Template, string? ResolvedSelector)> ResolveTemplateViaProfileAsync(string agentSelector, CancellationToken ct)
    {
        if (_agentProfileStore is null)
            return (null, null);

        var selectorLabels = agentSelector
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (selectorLabels.Count == 0)
            return (null, null);

        var profiles = await _agentProfileStore.LoadAgentProfilesAsync(ct);

        var resolver = new ProfileResolver();
        var profile = resolver.ResolveByRequiredLabels(profiles, selectorLabels);

        if (profile is null)
        {
            Log.Debug("DispatchService: no profile covers selector [{Selector}] for fallback template resolution",
                agentSelector);
            return (null, null);
        }

        // Use profile's MatchLabels as the template key (same as DispatchOrchestrationService.MapToRequest)
        var profileSelector = string.Join(",",
            profile.MatchLabels.OrderBy(l => l, StringComparer.Ordinal));

        var template = _templateProvider.Resolve(profileSelector);
        if (template is not null)
        {
            Log.Warning("DispatchService: AgentSelector [{Selector}] required profile expansion to resolve template. " +
                "Upstream code path may not be setting AgentSelector to full profile.MatchLabels. " +
                "Resolved via profile '{ProfileId}' → [{ProfileSelector}]",
                agentSelector, profile.Id, profileSelector);
        }

        return (template, profileSelector);
    }

    // ── Static helpers (internal for testability) ────────────────────────

    /// <summary>
    /// Generates deterministic K8s Job name: caa-{workItemId first 8 hex chars}.
    /// </summary>
    internal static string GenerateJobName(Guid workItemId)
        => $"caa-{workItemId.ToString("N")[..8]}";

    /// <summary>
    /// Normalizes agent selector by sorting labels and joining with comma.
    /// Delegates to <see cref="JobTemplateProvider.NormalizeLabels"/>.
    /// </summary>
    internal static string NormalizeSelector(string agentSelector)
        => JobTemplateProvider.NormalizeLabels(agentSelector);

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
    /// Lightweight projection of pending work items (no Payload loaded).
    /// </summary>
    internal sealed record PendingWorkItemProjection
    {
        public required Guid Id { get; init; }
        public required string AgentSelector { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required int TimeoutSeconds { get; init; }
        public WorkItemTaskType TaskType { get; init; }
        public string? ProjectId { get; init; }
        public string? IssueIdentifier { get; init; }
        public string? IssueProviderConfigId { get; init; }
    }
}
