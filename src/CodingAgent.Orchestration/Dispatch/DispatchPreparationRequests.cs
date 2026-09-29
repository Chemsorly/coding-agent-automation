using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration.Dispatch;

/// <summary>
/// Parameter object for <see cref="DispatchInfrastructure.PrepareDispatchCoreAsync"/>.
/// Groups the 10 orchestration parameters to satisfy S107.
/// <see cref="AdditionalRepoProviderIds"/> lists the other repositories a project epic's
/// decomposition clones next to its own; null for every other run.
/// <see cref="PullRequest"/> is the pull request a review is about; null for every other run.
/// A review's subject is that pull request in the repository, so the tracker is not read.
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
    ILogger Logger,
    IReadOnlyList<string>? AdditionalRepoProviderIds = null,
    IssueDetail? PullRequest = null);

/// <summary>
/// Parameter object for <see cref="DispatchOrchestrationService.PrepareAsync"/>
/// and <see cref="DispatchOrchestrationService.PrepareCoreAsync"/>.
/// Groups the 10 orchestration parameters to satisfy S107.
/// <see cref="PullRequest"/> is the pull request a review is about; null for every other run.
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
    PipelineRunType RunType = PipelineRunType.Implementation,
    IssueDetail? PullRequest = null);
