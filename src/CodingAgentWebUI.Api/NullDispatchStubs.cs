using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api;

/// <summary>
/// No-op IJobDispatcher stub.
/// JobQueueDrainService needs this but Legacy queue dispatch is dead after Spec 041.
/// TODO(Spec 043/044, same branch): remove when hub moves out of the monolith.
/// </summary>
internal sealed class NullJobDispatcher : IJobDispatcher
{
    public bool HasRegisteredAgents => false;

    public bool IsIssueBeingProcessedOrQueued(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
        => false;

    public Task<bool> TryDispatchAsync(
        IssueIdentifier issueIdentifier, ProviderConfigId issueProviderId, ProviderConfigId repoProviderId,
        string? brainProviderId, string? pipelineProviderId, string initiatedBy, CancellationToken ct,
        string? issueTitle = null, PipelineProject? project = null)
        => Task.FromResult(false);

    public Task<bool> TryDispatchReviewAsync(ReviewDispatchRequest request, CancellationToken ct, PipelineProject? project = null)
        => Task.FromResult(false);

    public Task<bool> TryDispatchDecompositionAsync(
        IssueIdentifier epicIdentifier, string epicTitle, PipelineRunType phaseType,
        ProviderConfigId issueProviderId, ProviderConfigId repoProviderId,
        string? brainProviderId, string initiatedBy, CancellationToken ct,
        string? decompositionSource = null, PipelineProject? project = null)
        => Task.FromResult(false);

    public Task<bool> DispatchToAgentDirectAsync(
        AgentEntry agent, PendingJob job, IReadOnlyList<string> requiredLabels, CancellationToken ct)
        => Task.FromResult(false);
}

/// <summary>
/// No-op IConsolidationDispatchService stub.
/// TODO(Spec 043/044, same branch): remove when hub moves out of the monolith.
/// </summary>
internal sealed class NullConsolidationDispatchService : IConsolidationDispatchService
{
    public Task<ConsolidationDispatchResult> TryDispatchAsync(
        ConsolidationRun run, ConsolidationRunType type, TemplateId? templateId,
        string? feedbackDataJson, string workspacePath, CancellationToken ct)
        => Task.FromResult(ConsolidationDispatchResult.Failed);

    public Task<bool> TryDispatchToAgentAsync(
        RunId runId, ConsolidationRunType type, TemplateId? templateId,
        string workspacePath, AgentId agentId, CancellationToken ct)
        => Task.FromResult(false);

    public Task NotifyRunCancelledAsync(RunId runId, CancellationToken ct)
        => Task.CompletedTask;
}
