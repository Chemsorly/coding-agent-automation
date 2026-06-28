using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Hubs;

/// <summary>
/// Concrete implementation of <see cref="IAgentHubFacade"/> that delegates to the
/// underlying orchestration services. Registered as a singleton in DI.
/// </summary>
public sealed class AgentHubFacade : IAgentHubFacade
{
    private readonly AgentRegistryService _registry;
    private readonly OrchestratorRunService _runService;
    private readonly JobDispatcherService _dispatcher;
    private readonly JobQueueDrainService _drainService;
    private readonly IPipelineRunHistoryService _historyService;
    private readonly IConfigurationStore _configStore;
    private readonly IProviderFactory _providerFactory;
    private readonly WorkItemTransitionService? _workItemTransition;

    public AgentHubFacade(
        AgentRegistryService registry,
        OrchestratorRunService runService,
        JobDispatcherService dispatcher,
        JobQueueDrainService drainService,
        IPipelineRunHistoryService historyService,
        IConfigurationStore configStore,
        IProviderFactory providerFactory,
        WorkItemTransitionService? workItemTransition = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(runService);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(drainService);
        ArgumentNullException.ThrowIfNull(historyService);
        ArgumentNullException.ThrowIfNull(configStore);
        ArgumentNullException.ThrowIfNull(providerFactory);

        _registry = registry;
        _runService = runService;
        _dispatcher = dispatcher;
        _drainService = drainService;
        _historyService = historyService;
        _configStore = configStore;
        _providerFactory = providerFactory;
        _workItemTransition = workItemTransition;
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

        await _workItemTransition.TransitionAsync(workItemId, status, item =>
        {
            item.CompletedAt = DateTimeOffset.UtcNow;
        }, ct);
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
        => _drainService.Signal();

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
}
