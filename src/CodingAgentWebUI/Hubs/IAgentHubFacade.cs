using CodingAgentWebUI.Infrastructure.Persistence.Entities;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Hubs;

/// <summary>
/// Facade interface that groups related operations currently spread across multiple
/// injected services in <see cref="AgentHub"/>. Reduces the hub's constructor
/// parameter count from 11 to 5 (facade, token vending, orchestration, model fetch, logger).
/// </summary>
/// <remarks>
/// <para>
/// The facade absorbs: <see cref="CodingAgentWebUI.Orchestration.Registry.AgentRegistryService"/>,
/// <see cref="CodingAgentWebUI.Orchestration.OrchestratorRunService"/>,
/// <see cref="CodingAgentWebUI.Orchestration.Dispatch.JobDispatcherService"/>,
/// <see cref="CodingAgentWebUI.Orchestration.Dispatch.JobQueueDrainService"/>,
/// <see cref="IPipelineRunHistoryService"/>, <see cref="IConfigurationStore"/>,
/// and <see cref="IProviderFactory"/>.
/// </para>
/// <para>
/// Direct dependencies remaining on <see cref="AgentHub"/>:
/// <see cref="ITokenVendingService"/>, <see cref="PipelineOrchestrationService"/>,
/// <see cref="CodingAgentWebUI.Orchestration.Health.ModelFetchService"/>, and <c>ILogger</c>.
/// </para>
/// </remarks>
public interface IAgentHubFacade
{
    // ── Registry operations ─────────────────────────────────────────────

    /// <summary>
    /// Registers an agent or updates an existing entry on reconnection.
    /// </summary>
    AgentEntry Register(AgentRegistrationMessage message, string connectionId);

    /// <summary>
    /// Removes an agent from the registry entirely.
    /// </summary>
    bool Deregister(string agentId);

    /// <summary>
    /// Looks up an agent by its unique agent identifier.
    /// </summary>
    AgentEntry? GetByAgentId(string agentId);

    /// <summary>
    /// Looks up an agent by its current SignalR connection ID.
    /// </summary>
    AgentEntry? GetByConnectionId(string connectionId);

    /// <summary>
    /// Transitions an agent to a new status.
    /// </summary>
    void TransitionStatus(string agentId, AgentStatus newStatus);

    /// <summary>
    /// Updates the heartbeat timestamp for the specified agent.
    /// </summary>
    void UpdateHeartbeat(string agentId, DateTimeOffset timestamp);

    // ── Run state operations ────────────────────────────────────────────

    /// <summary>
    /// Gets a specific run by its RunId.
    /// </summary>
    PipelineRun? GetRun(string jobId);

    /// <summary>
    /// Transitions the WorkItem row in Postgres to the given terminal status.
    /// Called from ReportJobCompleted to ensure DB state matches in-memory state.
    /// No-op if no DB-backed work distribution is configured.
    /// </summary>
    Task TransitionWorkItemAsync(string jobId, WorkItemStatus status, CancellationToken ct,
        string? errorMessage = null, FailureReason? failureReason = null);

    /// <summary>
    /// Adds a pipeline run to the active runs collection.
    /// </summary>
    void AddRun(PipelineRun run);

    /// <summary>
    /// Gets or creates the per-run output ring buffer.
    /// </summary>
    OutputRingBuffer GetOutputBuffer(string jobId);

    /// <summary>
    /// Removes a pipeline run from the active runs collection.
    /// </summary>
    void RemoveRun(string jobId);

    /// <summary>
    /// Returns all active runs assigned to the specified agent.
    /// </summary>
    IReadOnlyList<PipelineRun> GetActiveRunsByAgent(string agentId);

    // ── Dispatch operations ─────────────────────────────────────────────

    /// <summary>
    /// Marks an issue as no longer being processed in the dispatcher.
    /// </summary>
    void MarkIssueComplete(string issueIdentifier, string issueProviderConfigId);

    /// <summary>
    /// Signals the drain service to attempt dispatch for idle agents.
    /// </summary>
    void Signal();

    /// <summary>
    /// Gets the current retry count for a work item (how many times it has been rejected and re-queued).
    /// Returns 0 if the work item doesn't exist or has no retries.
    /// </summary>
    Task<int> GetWorkItemRetryCountAsync(string jobId, CancellationToken ct);

    /// <summary>
    /// Re-queues a rejected work item: transitions it back to Pending status,
    /// increments RetryCount, clears DispatchedAt and AssignedAgentId.
    /// The drain service will pick it up again on the next cycle.
    /// </summary>
    Task RequeueWorkItemAsync(string jobId, CancellationToken ct);

    /// <summary>
    /// Resolves provider config IDs from a WorkItem's payload (K8s mode fallback).
    /// Returns null if the work item doesn't exist or has no payload.
    /// Used by token vending when no in-memory PipelineRun exists.
    /// </summary>
    Task<(string? RepoProviderConfigId, string? BrainProviderConfigId)?> GetWorkItemProviderConfigIdsAsync(
        string workItemId, CancellationToken ct);

    // ── History ─────────────────────────────────────────────────────────

    /// <summary>
    /// Persists a completed pipeline run to history (async).
    /// </summary>
    Task AddRunToHistoryAsync(PipelineRun run, CancellationToken ct = default);

    /// <summary>
    /// Returns all completed pipeline run summaries from history (async).
    /// </summary>
    Task<IReadOnlyList<PipelineRunSummary>> GetRunHistoryAsync(CancellationToken ct = default);

    // ── Issue provider operations ───────────────────────────────────────

    /// <summary>
    /// Loads provider configurations of the specified kind.
    /// </summary>
    Task<IReadOnlyList<ProviderConfig>> LoadProviderConfigsAsync(ProviderKind kind, CancellationToken ct);

    /// <summary>
    /// Gets a single provider configuration by ID and kind.
    /// </summary>
    Task<ProviderConfig?> GetProviderConfigByIdAsync(string id, ProviderKind kind, CancellationToken ct);

    /// <summary>
    /// Creates an issue provider from the given configuration.
    /// </summary>
    IIssueProvider CreateIssueProvider(ProviderConfig config);

    /// <summary>
    /// Creates a repository provider from the given configuration.
    /// </summary>
    IRepositoryProvider CreateRepositoryProvider(ProviderConfig config);

    /// <summary>
    /// Updates WorkItemEntity.LastProgressAt in the DB with throttling.
    /// Only writes if the current DB value is null or older than the throttle interval (5 minutes).
    /// Called from ReportStepTransition and Heartbeat to persist progress evidence for
    /// timeout enforcement across replicas.
    /// </summary>
    Task TouchLastProgressAsync(string jobId, DateTimeOffset timestamp, CancellationToken ct);

    /// <summary>
    /// Reads IssueIdentifier and IssueProviderConfigId from a WorkItem in the database.
    /// Used for best-effort label recovery when no in-memory PipelineRun is available.
    /// Returns null if the work item doesn't exist or DB is not configured.
    /// </summary>
    Task<(string IssueIdentifier, string IssueProviderConfigId)?> GetWorkItemIssueMetadataAsync(
        string workItemId, CancellationToken ct);
}
