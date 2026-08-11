using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Parameter object for <see cref="DecompositionDispatchPreparation"/> constructor.
/// Groups the 12 dispatch-specific parameters to satisfy S107.
/// </summary>
internal sealed record DecompositionDispatchRequest(
    DispatchInfrastructure Infra,
    IDispatchRunCreator Orchestration,
    ILogger Logger,
    AgentEntry Agent,
    string EpicIdentifier,
    string EpicTitle,
    PipelineRunType PhaseType,
    ProviderConfigId IssueProviderId,
    ProviderConfigId RepoProviderId,
    string? BrainProviderId,
    string InitiatedBy,
    string? DecompositionSource);

/// <summary>
/// Parameter object for <see cref="ImplementationDispatchPreparation"/> constructor.
/// Groups the 11 dispatch-specific parameters to satisfy S107.
/// </summary>
/// <remarks>
/// IssueIdentifier is still declared as <c>string</c> here while DispatchCoreRequest (below) and
/// the public interface callers now use the typed IssueIdentifier. The caller in AgentJobDispatcher.Execution.cs
/// passes an IssueIdentifier which silently downcasts via implicit conversion. A future refactor removing the
/// implicit conversion would break this path without a compile error. Migrate to IssueIdentifier IssueIdentifier.
/// </remarks>
internal sealed record ImplementationDispatchRequest(
    DispatchInfrastructure Infra,
    IDispatchRunCreator Orchestration,
    ILogger Logger,
    AgentEntry Agent,
    string IssueIdentifier,
    ProviderConfigId IssueProviderId,
    ProviderConfigId RepoProviderId,
    string? BrainProviderId,
    string? PipelineProviderId,
    string InitiatedBy,
    IReadOnlyList<string> RequiredLabels);

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
/// Groups the mandatory constructor dependencies of <see cref="PendingWorkItemDrainService"/>
/// to reduce constructor parameter count (S107).
/// <see cref="IOrchestratorRunService"/> and <see cref="ILabelService"/> are no longer included
/// here — they moved to <see cref="DispatchRevertHandler"/> and <see cref="LabelSwapService"/>
/// respectively (#1871).
/// </summary>
public sealed record DrainServiceDependencies(
    Microsoft.EntityFrameworkCore.IDbContextFactory<PipelineDbContext> DbFactory,
    ISignalRWorkDistributorAgentResolver AgentResolver,
    IAgentCommunication AgentComm,
    WorkItemTransitionService TransitionService,
    IPendingWorkQuery PendingWorkQuery,
    Microsoft.Extensions.Logging.ILogger<PendingWorkItemDrainService> Logger);

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
/// <remarks>
/// IssueIdentifier is still declared as <c>string</c> here while the adjacent DispatchCoreRequest
/// uses the typed IssueIdentifier. Callers in DispatchOrchestrationService.cs pass string values that silently
/// bypass the type-safety the migration aims to enforce. Migrate to IssueIdentifier IssueIdentifier for
/// consistency. This is a public record — changing the type is a breaking change for callers outside this assembly.
/// </remarks>
public sealed record OrchestratorPreparationRequest(
    string IssueIdentifier,
    ProviderConfigId IssueProviderId,
    ProviderConfigId RepoProviderId,
    string? BrainProviderId,
    string? PipelineProviderId,
    string InitiatedBy,
    IReadOnlyList<string> RequiredLabels,
    PipelineProject Project,
    PipelineRunType RunType = PipelineRunType.Implementation);
