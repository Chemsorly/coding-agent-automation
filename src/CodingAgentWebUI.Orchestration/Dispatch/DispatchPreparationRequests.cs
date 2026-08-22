using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

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
/// Parameter object for <see cref="AnalysisStalenessDetector.EvaluateAsync"/>.
/// Groups the 8 evaluation parameters to satisfy S107.
/// </summary>
public sealed record StalenessEvaluationRequest(
    IssueComment AnalysisComment,
    IReadOnlyList<IssueComment> IssueComments,
    string IssueBody,
    IssueIdentifier IssueIdentifier,
    ProviderConfigId IssueProviderConfigId,
    int CommitThreshold,
    Func<DateTimeOffset, CancellationToken, Task<int>>? GetCommitCount);

/// <summary>
/// Parameter object for <see cref="DispatchLifecycleService.ExecuteDispatchLifecycleAsync"/>.
/// Groups the non-delegate parameters to satisfy S107.
/// </summary>
internal sealed record DispatchLifecycleContext(
    PipelineDbContext Db,
    PendingWorkItemProjection Item,
    JobTemplate Template,
    bool IsKiroAgent,
    List<string> AvailablePvcs,
    Dictionary<string, int> ConcurrencyBySelector,
    string LogPrefix);

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
