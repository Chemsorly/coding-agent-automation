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
/// <see cref="PullRequest"/> is a pre-built subject that stands in for the tracker issue, so the tracker is not
/// read: the pull request a review is about, or an operator triage's report; null for every other run.
/// <see cref="NewestComments"/> keeps the newest comments instead of the oldest when an issue has more than the
/// cap; a triage needs the latest feedback.
/// <see cref="StackLabels"/> are the repository template's labels, which pick the quality gates and reviewers
/// (spec 053 DD7); null at dispatch time, where none are resolved. <see cref="AgentConfig"/> is the agent provider
/// config built from the routed profile and route (DD5); null at dispatch time, where the agent receives none yet.
/// </summary>
internal sealed record DispatchCoreRequest(
    IReadOnlyList<string>? StackLabels,
    IssueIdentifier IssueIdentifier,
    ProviderConfigId IssueProviderId,
    ProviderConfigId RepoProviderId,
    ProviderConfig? AgentConfig,
    string? BrainProviderId,
    string? PipelineProviderId,
    PipelineProject Project,
    ILogger Logger,
    IReadOnlyList<string>? AdditionalRepoProviderIds = null,
    IssueDetail? PullRequest = null,
    bool NewestComments = false);

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
    PipelineProject Project,
    PipelineRunType RunType = PipelineRunType.Implementation,
    IssueDetail? PullRequest = null);
