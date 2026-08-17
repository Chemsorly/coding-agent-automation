using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Orchestration.Telemetry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CodingAgentWebUI.Hub;

/// <summary>
/// Concrete implementation of <see cref="IAgentHubFacade"/> that delegates to the
/// underlying orchestration services. Registered as a singleton in DI.
/// </summary>
public sealed class AgentHubFacade : IAgentHubFacade
{
    private readonly IAgentRegistryService _registry;
    private readonly OrchestratorRunService _runService;
    private readonly JobDeduplicationGuardService _dispatcher;
    private readonly JobQueueDrainService _drainService;
    private readonly IPipelineRunHistoryService _historyService;
    private readonly IConfigurationStore _configStore;
    private readonly IProviderFactory _providerFactory;
    private readonly WorkItemTransitionService? _workItemTransition;
    private readonly IWorkItemFallbackTransitionService? _workItemFallbackTransition;
    private readonly IDbContextFactory<PipelineDbContext>? _dbFactory;
    private readonly IProjectStore? _projectStore;
    private readonly ILogger<AgentHubFacadeDependencies> _logger;

    public AgentHubFacade(AgentHubFacadeDependencies deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentNullException.ThrowIfNull(deps.Registry);
        ArgumentNullException.ThrowIfNull(deps.RunService);
        ArgumentNullException.ThrowIfNull(deps.Dispatcher);
        ArgumentNullException.ThrowIfNull(deps.DrainService);
        ArgumentNullException.ThrowIfNull(deps.HistoryService);
        ArgumentNullException.ThrowIfNull(deps.ConfigStore);
        ArgumentNullException.ThrowIfNull(deps.ProviderFactory);
        ArgumentNullException.ThrowIfNull(deps.Logger);

        _registry = deps.Registry;
        _runService = deps.RunService;
        _dispatcher = deps.Dispatcher;
        _drainService = deps.DrainService;
        _historyService = deps.HistoryService;
        _configStore = deps.ConfigStore;
        _providerFactory = deps.ProviderFactory;
        _logger = deps.Logger;
        _workItemTransition = deps.WorkItemTransition;
        _workItemFallbackTransition = deps.WorkItemFallbackTransition;
        _dbFactory = deps.DbFactory;
        _projectStore = deps.ProjectStore;
    }

    // ── Registry operations ─────────────────────────────────────────────

    /// <inheritdoc />
    public AgentEntry Register(AgentRegistrationMessage message, string connectionId)
        => _registry.Register(message, connectionId);

    /// <inheritdoc />
    public bool Deregister(AgentId agentId)
        => _registry.Deregister(agentId);

    /// <inheritdoc />
    public AgentEntry? GetByAgentId(AgentId agentId)
        => _registry.GetByAgentId(agentId);

    /// <inheritdoc />
    public AgentEntry? GetByConnectionId(string connectionId)
        => _registry.GetByConnectionId(connectionId);

    /// <inheritdoc />
    public void TransitionStatus(AgentId agentId, AgentStatus newStatus)
        => _registry.TransitionStatus(agentId, newStatus);

    /// <inheritdoc />
    public void UpdateHeartbeat(AgentId agentId, DateTimeOffset timestamp)
        => _registry.UpdateHeartbeat(agentId, timestamp);

    // ── Run state operations ────────────────────────────────────────────

    /// <inheritdoc />
    public PipelineRun? GetRun(JobId jobId)
        => _runService.GetRun(jobId.Value);

    /// <inheritdoc />
    public async Task TransitionWorkItemAsync(JobId jobId, WorkItemStatus status, CancellationToken ct,
        string? errorMessage = null, FailureReason? failureReason = null)
    {
        if (_workItemFallbackTransition is null || !Guid.TryParse(jobId.Value, out var workItemId))
            return;

        // Single retry with longer backoff — acts as a safety net above the Polly pipeline
        // in WorkItemTransitionService (which handles transient DB errors with 5 retries).
        // This outer retry only fires if the entire Polly pipeline fails or the circuit breaks.
        // If all retries fail, ReconciliationService will eventually mark it Failed
        // (which may be incorrect if the agent actually succeeded).
        const int maxAttempts = 2;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try
            {
                if (await _workItemFallbackTransition.TryFallbackChainAsync(workItemId, status, errorMessage, failureReason, ct))
                    return;

                _logger.LogWarning(
                    "WorkItem {WorkItemId} transition to {Status} rejected (may already be terminal)",
                    workItemId, status);
                return;
            }
            catch (Exception ex) when (attempt < maxAttempts - 1
                && ex is not Polly.CircuitBreaker.BrokenCircuitException)
            {
                _logger.LogWarning(ex,
                    "WorkItem {WorkItemId} transition to {Status} failed on attempt {Attempt}, retrying",
                    workItemId, status, attempt + 1);
                // Wait 2s before final retry — gives brief recovery window after Polly exhaustion
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
            }
        }

        _logger.LogError(
            "WorkItem {WorkItemId} transition to {Status} failed after all retry attempts",
            workItemId, status);
    }

    /// <inheritdoc />
    public void AddRun(PipelineRun run)
        => _runService.AddRun(run);

    /// <inheritdoc />
    public OutputRingBuffer GetOutputBuffer(JobId jobId)
        => _runService.GetOutputBuffer(jobId.Value);

    /// <inheritdoc />
    public void RemoveRun(JobId jobId)
        => _runService.RemoveRun(jobId.Value);

    /// <inheritdoc />
    public IReadOnlyList<PipelineRun> GetActiveRunsByAgent(AgentId agentId)
        => _runService.GetActiveRuns().Where(r => r.AgentId == agentId.Value).ToList();

    // ── Dispatch operations ─────────────────────────────────────────────

    /// <inheritdoc />
    public void MarkIssueComplete(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
        => _dispatcher.MarkIssueComplete(issueIdentifier, issueProviderConfigId);

    /// <inheritdoc />
    public void Signal()
    {
        _drainService.Signal();
    }

    /// <inheritdoc />
    public async Task<int> GetWorkItemRetryCountAsync(JobId jobId, CancellationToken ct)
    {
        if (_workItemTransition is null || !Guid.TryParse(jobId.Value, out var workItemId))
            return 0;

        try
        {
            return await _workItemTransition.GetRetryCountAsync(workItemId, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get RetryCount for WorkItem {WorkItemId}", workItemId);
            return 0;
        }
    }

    /// <inheritdoc />
    public async Task RequeueWorkItemAsync(JobId jobId, CancellationToken ct)
    {
        if (_workItemTransition is null || !Guid.TryParse(jobId.Value, out var workItemId))
            return;

        await _workItemTransition.RequeueAsync(workItemId, ct);
        _logger.LogInformation("WorkItem {WorkItemId} re-queued as Pending (retry after rejection)", workItemId);
    }

    /// <inheritdoc />
    public async Task<(string? RepoProviderConfigId, string? BrainProviderConfigId)?> GetWorkItemProviderConfigIdsAsync(
        JobId jobId, CancellationToken ct)
    {
        if (_dbFactory is null || !Guid.TryParse(jobId.Value, out var id))
            return null;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var payload = await db.WorkItems
                .AsNoTracking()
                .Where(w => w.Id == id)
                .Select(w => w.Payload)
                .FirstOrDefaultAsync(ct);

            if (payload is null) return null;

            using var doc = System.Text.Json.JsonDocument.Parse(payload);
            var root = doc.RootElement;

            var repoConfigId = root.TryGetProperty("repoProviderConfigId", out var repoProp)
                ? repoProp.GetString() : null;
            var brainConfigId = root.TryGetProperty("brainProviderConfigId", out var brainProp)
                ? brainProp.GetString() : null;

            return (repoConfigId, brainConfigId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to resolve provider config IDs from WorkItem {WorkItemId}", jobId.Value);
            return null;
        }
    }

    // ── History ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public Task AddRunToHistoryAsync(PipelineRun run, CancellationToken ct = default)
        => _historyService.AddRunToHistoryAsync(run, ct);

    /// <inheritdoc />
    public Task<IReadOnlyList<PipelineRunSummary>> GetRunHistoryAsync(CancellationToken ct = default)
        => _historyService.GetRunHistoryAsync(ct);

    // ── Issue provider operations ───────────────────────────────────────

    /// <inheritdoc />
    public Task<IReadOnlyList<PipelineJobTemplate>> LoadTemplatesForProjectAsync(string projectId, CancellationToken ct)
    {
        if (_projectStore is null)
            return Task.FromResult<IReadOnlyList<PipelineJobTemplate>>(Array.Empty<PipelineJobTemplate>());
        return _projectStore.LoadTemplatesForProjectAsync(projectId, ct);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ProviderConfig>> LoadProviderConfigsAsync(ProviderKind kind, CancellationToken ct)
        => _configStore.LoadProviderConfigsAsync(kind, ct);

    /// <inheritdoc />
    public Task<ProviderConfig?> GetProviderConfigByIdAsync(string id, ProviderKind kind, CancellationToken ct)
        => _configStore.GetProviderConfigByIdAsync(id, kind, ct);

    /// <inheritdoc />
    public IIssueProvider CreateIssueProvider(ProviderConfig config)
        => _providerFactory.CreateIssueProvider(config);

    /// <inheritdoc />
    public IRepositoryProvider CreateRepositoryProvider(ProviderConfig config)
        => _providerFactory.CreateRepositoryProvider(config);

    // ── Progress tracking ───────────────────────────────────────────────

    /// <summary>
    /// Throttle interval for LastProgressAt DB writes. Only writes when the existing
    /// DB value is null or older than this threshold.
    /// </summary>
    private static readonly TimeSpan ProgressWriteThrottle = TimeSpan.FromMinutes(5);

    /// <inheritdoc />
    public async Task TouchLastProgressAsync(JobId jobId, DateTimeOffset timestamp, CancellationToken ct)
    {
        if (_dbFactory is null || !Guid.TryParse(jobId.Value, out var workItemId))
            return;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var item = await db.WorkItems.FindAsync([workItemId], ct);

            if (item is null)
                return;

            // Throttle: skip write if DB value is recent enough (uses wall clock, not agent timestamp,
            // to avoid clock-skew issues where a behind-clock agent could permanently suppress writes)
            if (item.LastProgressAt.HasValue &&
                (DateTimeOffset.UtcNow - item.LastProgressAt.Value) < ProgressWriteThrottle)
                return;

            item.LastProgressAt = timestamp;
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            WorkDistributionTelemetry.ProgressWriteFailures.Add(1);
            _logger.LogWarning(ex, "Failed to update LastProgressAt for WorkItem {WorkItemId}", workItemId);
        }
    }

    /// <inheritdoc />
    public async Task<(string IssueIdentifier, string IssueProviderConfigId)?> GetWorkItemIssueMetadataAsync(
        JobId jobId, CancellationToken ct)
    {
        if (_dbFactory is null || !Guid.TryParse(jobId.Value, out var id))
            return null;

        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct);
            var result = await db.WorkItems
                .AsNoTracking()
                .Where(w => w.Id == id)
                .Select(w => new { w.IssueIdentifier, w.IssueProviderConfigId })
                .FirstOrDefaultAsync(ct);

            if (result is null || string.IsNullOrEmpty(result.IssueIdentifier))
                return null;

            return (result.IssueIdentifier, result.IssueProviderConfigId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read issue metadata from WorkItem {WorkItemId}", jobId.Value);
            return null;
        }
    }
}
