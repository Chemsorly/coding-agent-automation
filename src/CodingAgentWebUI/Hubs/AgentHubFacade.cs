using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.Extensions.Logging;

namespace CodingAgentWebUI.Hubs;

/// <summary>
/// Concrete implementation of <see cref="IAgentHubFacade"/> that delegates to the
/// underlying orchestration services. Registered as a singleton in DI.
/// </summary>
public sealed class AgentHubFacade : IAgentHubFacade
{
    private readonly IAgentRegistryService _registry;
    private readonly OrchestratorRunService _runService;
    private readonly JobDispatcherService _dispatcher;
    private readonly JobQueueDrainService _drainService;
    private readonly IPipelineRunHistoryService _historyService;
    private readonly IConfigurationStore _configStore;
    private readonly IProviderFactory _providerFactory;
    private readonly WorkItemTransitionService? _workItemTransition;
    private readonly PendingWorkItemDrainService? _pendingDrainService;
    private readonly ILogger<AgentHubFacade> _logger;

    public AgentHubFacade(
        IAgentRegistryService registry,
        OrchestratorRunService runService,
        JobDispatcherService dispatcher,
        JobQueueDrainService drainService,
        IPipelineRunHistoryService historyService,
        IConfigurationStore configStore,
        IProviderFactory providerFactory,
        ILogger<AgentHubFacade> logger,
        WorkItemTransitionService? workItemTransition = null,
        PendingWorkItemDrainService? pendingDrainService = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(runService);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(drainService);
        ArgumentNullException.ThrowIfNull(historyService);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(logger);

        _registry = registry;
        _runService = runService;
        _dispatcher = dispatcher;
        _drainService = drainService;
        _historyService = historyService;
        _configStore = configStore;
        _providerFactory = providerFactory;
        _logger = logger;
        _workItemTransition = workItemTransition;
        _pendingDrainService = pendingDrainService;
    }

    // ── Registry operations ─────────────────────────────────────────────

    /// <inheritdoc />
    public AgentEntry Register(AgentRegistrationMessage message, string connectionId)
        => _registry.Register(message, connectionId);

    /// <inheritdoc />
    public bool Deregister(string agentId)
        => _registry.Deregister(agentId);

    /// <inheritdoc />
    public AgentEntry? GetByAgentId(string agentId)
        => _registry.GetByAgentId(agentId);

    /// <inheritdoc />
    public AgentEntry? GetByConnectionId(string connectionId)
        => _registry.GetByConnectionId(connectionId);

    /// <inheritdoc />
    public void TransitionStatus(string agentId, AgentStatus newStatus)
        => _registry.TransitionStatus(agentId, newStatus);

    /// <inheritdoc />
    public void UpdateHeartbeat(string agentId, DateTimeOffset timestamp)
        => _registry.UpdateHeartbeat(agentId, timestamp);

    // ── Run state operations ────────────────────────────────────────────

    /// <inheritdoc />
    public PipelineRun? GetRun(string jobId)
        => _runService.GetRun(jobId);

    /// <inheritdoc />
    public async Task TransitionWorkItemAsync(string jobId, WorkItemStatus status, CancellationToken ct)
    {
        if (_workItemTransition is null || !Guid.TryParse(jobId, out var workItemId))
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
                var result = await _workItemTransition.TransitionAsync(workItemId, status, item =>
                {
                    if (status is WorkItemStatus.Succeeded or WorkItemStatus.Failed or WorkItemStatus.Cancelled)
                        item.CompletedAt = DateTimeOffset.UtcNow;
                }, ct);

                if (result)
                {
                    _logger.LogInformation(
                        "WorkItem {WorkItemId} transitioned to {Status}",
                        workItemId, status);
                    return;
                }

                // Transition rejected — likely Dispatched → Succeeded/Cancelled (skipped Running).
                // Attempt two-step: Dispatched → Running → terminal status.
                if (status is WorkItemStatus.Succeeded or WorkItemStatus.Cancelled)
                {
                    _logger.LogWarning(
                        "WorkItem {WorkItemId} direct transition to {Status} rejected, attempting two-step via Running",
                        workItemId, status);

                    var intermediateResult = await _workItemTransition.TransitionAsync(
                        workItemId, WorkItemStatus.Running, ct: ct);

                    if (intermediateResult)
                    {
                        var finalResult = await _workItemTransition.TransitionAsync(workItemId, status, item =>
                        {
                            if (status is WorkItemStatus.Succeeded or WorkItemStatus.Failed or WorkItemStatus.Cancelled)
                                item.CompletedAt = DateTimeOffset.UtcNow;
                        }, ct);

                        if (finalResult)
                        {
                            _logger.LogInformation(
                                "WorkItem {WorkItemId} two-step transition to {Status} succeeded (via Running)",
                                workItemId, status);
                            return;
                        }
                    }
                }

                // If we get here, transition was rejected for a non-recoverable reason
                // (e.g., already terminal). Log and exit — not an error.
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
    public OutputRingBuffer GetOutputBuffer(string jobId)
        => _runService.GetOutputBuffer(jobId);

    /// <inheritdoc />
    public void RemoveRun(string jobId)
        => _runService.RemoveRun(jobId);

    /// <inheritdoc />
    public IReadOnlyList<PipelineRun> GetActiveRunsByAgent(string agentId)
        => _runService.GetActiveRuns().Where(r => r.AgentId == agentId).ToList();

    // ── Dispatch operations ─────────────────────────────────────────────

    /// <inheritdoc />
    public void MarkIssueComplete(string issueIdentifier, string issueProviderConfigId)
        => _dispatcher.MarkIssueComplete(issueIdentifier, issueProviderConfigId);

    /// <inheritdoc />
    public void Signal()
    {
        _drainService.Signal();
        _pendingDrainService?.Signal();
    }

    // ── History ─────────────────────────────────────────────────────────

    /// <inheritdoc />
    public void AddRunToHistory(PipelineRun run)
        => _historyService.AddRunToHistory(run);

    /// <inheritdoc />
    public IReadOnlyList<PipelineRunSummary> GetRunHistory()
        => _historyService.GetRunHistory();

    // ── Issue provider operations ───────────────────────────────────────

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
}
