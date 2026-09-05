using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Telemetry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.Extensions.Logging;

namespace CodingAgentWebUI.Hub;

/// <summary>
/// Concrete implementation of <see cref="IAgentHubFacade"/> that delegates to the
/// underlying orchestration services. Registered as a singleton in DI.
/// </summary>
public sealed class AgentHubFacade : IAgentHubFacade
{
    private readonly IAgentRegistryService _registry;
    private readonly IOrchestratorRunService _runService;
    private readonly IPipelineRunHistoryService _historyService;
    private readonly IProviderConfigStore _configStore;
    private readonly IProviderFactory _providerFactory;
    private readonly IWorkItemTransitionStore? _transitionStore;
    private readonly IWorkItemFallbackTransitionService? _workItemFallbackTransition;
    private readonly IProjectStore? _projectStore;
    private readonly ILogger<AgentHubFacadeDependencies> _logger;
    private readonly TimeProvider _timeProvider;

    public AgentHubFacade(AgentHubFacadeDependencies deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentNullException.ThrowIfNull(deps.Registry);
        ArgumentNullException.ThrowIfNull(deps.RunService);
        ArgumentNullException.ThrowIfNull(deps.Dispatcher);
        ArgumentNullException.ThrowIfNull(deps.HistoryService);
        ArgumentNullException.ThrowIfNull(deps.ConfigStore);
        ArgumentNullException.ThrowIfNull(deps.ProviderFactory);
        ArgumentNullException.ThrowIfNull(deps.Logger);

        _registry = deps.Registry;
        _runService = deps.RunService;
        _historyService = deps.HistoryService;
        _configStore = deps.ConfigStore;
        _providerFactory = deps.ProviderFactory;
        _logger = deps.Logger;
        _transitionStore = deps.TransitionStore;
        _workItemFallbackTransition = deps.WorkItemFallbackTransition;
        _projectStore = deps.ProjectStore;
        _timeProvider = deps.TimeProvider ?? TimeProvider.System;
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

    public void ReplaceRun(PipelineRun run)
        => _runService.ReplaceRun(run);

    /// <inheritdoc />
    public async Task<bool> TransitionWorkItemAsync(JobId jobId, WorkItemStatus status, CancellationToken ct,
        string? errorMessage = null, FailureReason? failureReason = null)
    {
        if (_workItemFallbackTransition is null || !Guid.TryParse(jobId.Value, out var workItemId))
            return true; // No DB configured — treat as success (no-op)

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
                    return true;

                _logger.LogWarning(
                    "WorkItem {WorkItemId} transition to {Status} rejected (may already be terminal)",
                    workItemId, status);
                return false;
            }
            catch (Exception ex) when (attempt < maxAttempts - 1
                && ex is not Polly.CircuitBreaker.BrokenCircuitException)
            {
                _logger.LogWarning(ex,
                    "WorkItem {WorkItemId} transition to {Status} failed on attempt {Attempt}, retrying",
                    workItemId, status, attempt + 1);
                // Wait 2s before final retry — gives brief recovery window after Polly exhaustion
                await Task.Delay(TimeSpan.FromSeconds(2), _timeProvider, ct);
            }
        }

        _logger.LogError(
            "WorkItem {WorkItemId} transition to {Status} failed after all retry attempts",
            workItemId, status);
        return false;
    }

    /// <inheritdoc />
    public void AddRun(PipelineRun run)
        => _runService.AddRun(run);

    /// <inheritdoc />
    public OutputRingBuffer GetOutputBuffer(JobId jobId)
        => _runService.GetOutputBuffer(jobId.Value);

    /// <inheritdoc />
    public void AppendOutputLines(JobId jobId, IReadOnlyList<string> lines)
        => _runService.AppendOutputLines(jobId.Value, lines);

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetOutputBacklogAsync(JobId jobId)
    {
        // For DistributedRunService, fetch from Redis. For in-memory, use the ring buffer.
        if (_runService is DistributedRunService distributed)
            return distributed.GetOutputBacklogAsync(jobId.Value)
                .ContinueWith(t => (IReadOnlyList<string>)t.Result);

        var buffer = _runService.GetOutputBuffer(jobId.Value);
        return Task.FromResult<IReadOnlyList<string>>(buffer.GetAll());
    }

    /// <inheritdoc />
    public void RemoveRun(JobId jobId)
        => _runService.RemoveRun(jobId.Value);

    /// <inheritdoc />
    public IReadOnlyList<PipelineRun> GetActiveRunsByAgent(AgentId agentId)
        => _runService.GetActiveRuns().Where(r => r.AgentId == agentId.Value).ToList();

    // ── Dispatch operations ─────────────────────────────────────────────

    /// <inheritdoc />
    public async Task<int> GetWorkItemRetryCountAsync(JobId jobId, CancellationToken ct)
    {
        if (_transitionStore is null || !Guid.TryParse(jobId.Value, out var workItemId))
            return 0;

        try
        {
            return await _transitionStore.GetRetryCountAsync(workItemId, ct);
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
        if (_transitionStore is null || !Guid.TryParse(jobId.Value, out var workItemId))
            return;

        await _transitionStore.RequeueAsync(workItemId, ct);
        _logger.LogInformation("WorkItem {WorkItemId} re-queued as Pending (retry after rejection)", workItemId);
    }

    /// <inheritdoc />
    public async Task<(string? RepoProviderConfigId, string? BrainProviderConfigId)?> GetWorkItemProviderConfigIdsAsync(
        JobId jobId, CancellationToken ct)
    {
        if (_transitionStore is null || !Guid.TryParse(jobId.Value, out var id))
            return null;

        try
        {
            return await _transitionStore.GetWorkItemProviderConfigIdsAsync(id, ct);
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

    /// <inheritdoc />
    public async Task TouchLastProgressAsync(JobId jobId, DateTimeOffset timestamp, CancellationToken ct)
    {
        if (_transitionStore is null || !Guid.TryParse(jobId.Value, out var workItemId))
            return;

        // The throttle (skip when the DB value is recent enough) lives in the store's
        // TouchLastProgressAsync — the facade only translates failures into telemetry.
        try
        {
            await _transitionStore.TouchLastProgressAsync(workItemId, timestamp, ct);
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
        if (_transitionStore is null || !Guid.TryParse(jobId.Value, out var id))
            return null;

        try
        {
            return await _transitionStore.GetWorkItemIssueMetadataAsync(id, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to read issue metadata from WorkItem {WorkItemId}", jobId.Value);
            return null;
        }
    }

    /// <inheritdoc />
    public Task UpdateAgentFieldAsync(AgentId agentId, string field, string? value)
        => _registry.UpdateAgentFieldAsync(agentId, field, value);
}
