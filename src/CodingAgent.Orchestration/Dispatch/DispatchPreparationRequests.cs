using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration.Dispatch;

/// <summary>
/// Parameter object for <see cref="DispatchInfrastructure.PrepareDispatchCoreAsync"/>.
/// Groups the 10 orchestration parameters to satisfy S107.
/// </summary>
internal sealed record DispatchCoreRequest(
    IReadOnlyList<string> RequiredLabels,
    IssueIdentifier IssueIdentifier,
    ProviderConfigId IssueProviderId,
    ProviderConfigId RepoProviderId,
    string AgentProviderId,
    string? BrainProviderId,
    string? PipelineProviderId,
    PipelineProject Project,
    ILogger Logger);

/// <summary>
/// Parameter object for <see cref="DispatchOrchestrationService.PrepareAsync"/>
/// and <see cref="DispatchOrchestrationService.PrepareCoreAsync"/>.
/// Groups the 10 orchestration parameters to satisfy S107.
/// </summary>
public sealed record OrchestratorPreparationRequest(
    IssueIdentifier IssueIdentifier,
    ProviderConfigId IssueProviderId,
    ProviderConfigId RepoProviderId,
    string? BrainProviderId,
    string? PipelineProviderId,
    string InitiatedBy,
    IReadOnlyList<string> RequiredLabels,
    PipelineProject Project,
    PipelineRunType RunType = PipelineRunType.Implementation);
