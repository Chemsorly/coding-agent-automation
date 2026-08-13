using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Orchestration.Dispatch;

public sealed partial class AgentJobDispatcher
{
    /// <summary>
    /// Bundles all shared context needed to build and send a <see cref="JobAssignmentMessage"/>.
    /// Replaces both the former 18-parameter <c>BuildAndSendAsync</c> signature and the internal
    /// <c>DispatchContext</c> class, reducing indirection to a single context object.
    /// </summary>
    internal sealed class DispatchPipelineContext
    {
        public required AgentEntry Agent { get; init; }
        public required PipelineRun Run { get; init; }
        public required AgentProfile Profile { get; init; }
        public required IssueIdentifier IssueIdentifier { get; init; }
        public required IssueDetail IssueDetail { get; init; }
        public required ParsedIssue ParsedIssue { get; init; }
        public required IReadOnlyList<IssueComment> IssueComments { get; init; }
        public required ProviderConfigId RepoProviderId { get; init; }
        public required string AgentProviderId { get; init; }
        public string? BrainProviderId { get; init; }
        public string? PipelineProviderId { get; init; }
        public ProviderConfigId? IssueProviderId { get; init; }
        public required IReadOnlyList<ProviderConfig> ProviderConfigs { get; init; }
        public required PipelineConfiguration Config { get; init; }
        public required string InitiatedBy { get; init; }
        public required PipelineProject Project { get; init; }

    }

    /// <summary>
    /// Result of a variant-specific dispatch preparation delegate. Replaces the anonymous tuple
    /// <c>(DispatchPipelineContext, Func&lt;JobAssignmentMessage, JobAssignmentMessage&gt;, Action?)</c>
    /// with a named type for improved readability and stack traces.
    /// </summary>
    internal sealed record DispatchPipelineResult(
        DispatchPipelineContext Context,
        Func<JobAssignmentMessage, JobAssignmentMessage> Customize,
        Action? OnSuccess);

    /// <summary>
    /// Builds a <see cref="JobAssignmentMessage"/> with properties shared across all dispatch paths.
    /// Variant-specific properties (RunType, QualityGateConfigs, ReviewerConfigs, etc.) must be set
    /// by the caller on the returned message using <c>with</c> expressions.
    /// </summary>
    private static JobAssignmentMessage BuildBaseJobAssignmentMessage(DispatchPipelineContext ctx)
    {
        return new JobAssignmentMessage
        {
            JobId = ctx.Run.RunId,
            IssueIdentifier = ctx.IssueIdentifier,
            IssueDetail = ctx.IssueDetail,
            ParsedIssue = ctx.ParsedIssue,
            IssueComments = ctx.IssueComments,
            RepoProviderConfigId = ctx.RepoProviderId.Value,
            AgentProviderConfigId = ctx.AgentProviderId,
            BrainProviderConfigId = ctx.BrainProviderId,
            PipelineProviderConfigId = ctx.PipelineProviderId,
            ProviderConfigs = ctx.ProviderConfigs,
            PipelineConfiguration = ctx.Config,
            InitiatedBy = ctx.InitiatedBy,
            ResolvedProfileId = ctx.Profile.Id,
            McpServers = DispatchOrchestrationService.MergeMcpServers(ctx.Profile.McpServers, ctx.Project.McpServers),
            ProjectId = ctx.Project.Id,
            ProjectName = ctx.Project.Name,
            ProjectSecrets = ctx.Project.Secrets,
            TraceContext = CaptureTraceContext(),
            ProjectSteeringContent = ctx.Project.SteeringContent,
            // NOTE: This path was historically dead code (TokenVendingService.CloneWithSettings dropped SteeringContent).
            // Fixed in #1628. Integration test in RepoSteeringContentIntegrationTests guards against regression.
            RepoSteeringContent = ctx.ProviderConfigs.FirstOrDefault(c => c.Id == ctx.RepoProviderId.Value)?.SteeringContent,
            IssueProviderConfigId = ctx.IssueProviderId?.Value,
            // Variant-specific properties default to safe values; callers override via `with`
            QualityGateConfigs = Array.Empty<QualityGateConfiguration>(),
            ReviewerConfigs = Array.Empty<ReviewerConfiguration>()
        };
    }

    /// <summary>
    /// Builds the base <see cref="JobAssignmentMessage"/> from a <see cref="DispatchPipelineContext"/>,
    /// applies variant-specific customization via the <paramref name="customize"/> function, and
    /// sends the job to the agent. This is the shared tail of all dispatch paths.
    /// </summary>
    private async Task BuildAndSendAsync(
        DispatchPipelineContext pipelineCtx,
        Func<JobAssignmentMessage, JobAssignmentMessage> customize,
        CancellationToken ct)
    {
        var message = customize(BuildBaseJobAssignmentMessage(pipelineCtx));

        await AssignAndSendAsync(pipelineCtx.Agent, pipelineCtx.Run.RunId, message, ct);
    }

    /// <summary>
    /// Sets common project and profile metadata on a <see cref="PipelineRun"/>.
    /// Shared extraction point used by all three dispatch paths.
    /// </summary>
    private static void ApplyRunMetadata(PipelineRun run, PipelineProject project, AgentProfile profile)
    {
        run.ProjectId = project.Id;
        run.ProjectName = project.Name;
        run.ResolvedProfileId = profile.Id;
    }

    /// <summary>
    /// Shared prologue for all dispatch paths: ensures a non-null project, resolves the agent profile,
    /// and extracts the agent provider config ID. Returns <c>null</c> if profile resolution fails.
    /// </summary>
    private async Task<(PipelineProject Project, AgentProfile Profile, string AgentProviderId)?>
        ResolveDispatchCoreAsync(AgentEntry agent, string identifier, string identifierType,
                                  PipelineProject? project, CancellationToken ct)
    {
        project = EnsureProject(project, identifier, identifierType);

        var profile = await _infra.Resolution.ResolveProfileAsync(agent, ct);
        if (profile is null)
            return null;

        return (project, profile, profile.AgentProviderConfigId);
    }

    /// <summary>
    /// Shared error-handling wrapper for all dispatch paths. Executes <paramref name="body"/> and,
    /// on exception, reverts agent state and swaps the label back via <see cref="RevertDispatchFailureAsync"/>.
    /// </summary>
    /// <param name="agent">The agent being dispatched to.</param>
    /// <param name="revertProviderConfigId">Provider config ID for the label swap on failure (issueProviderId for impl/decomp, repoProviderId for review).</param>
    /// <param name="identifier">Issue/PR/epic identifier for logging and label revert.</param>
    /// <param name="revertLabel">Label to swap back to on failure.</param>
    /// <param name="failureMessageTemplate">Serilog message template for the error log.</param>
    /// <param name="body">The dispatch logic to execute.</param>
    /// <param name="revertTargetKind">Optional label target kind (e.g., PullRequest for review path).</param>
    private async Task<bool> SafeDispatchAsync(
        AgentEntry agent,
        ProviderConfigId revertProviderConfigId,
        string identifier,
        string revertLabel,
        string failureMessageTemplate,
        Func<Task<bool>> body,
        LabelTargetKind? revertTargetKind = null)
    {
        try
        {
            return await body();
        }
        catch (Exception ex)
        {
            await RevertDispatchFailureAsync(agent, ex, failureMessageTemplate,
                revertProviderConfigId, identifier, revertLabel, revertTargetKind);
            return false;
        }
    }

    /// <summary>
    /// Template method that orchestrates the common dispatch pipeline sequence:
    /// <c>SafeDispatchAsync</c> → <c>ResolveDispatchCoreAsync</c> → variant-specific preparation
    /// → <c>ApplyRunMetadata</c> → <c>BuildAndSendAsync</c> → optional success callback.
    /// <para>
    /// Each dispatch path provides its unique logic via the <paramref name="prepareAndCustomize"/> delegate,
    /// which performs run creation, config resolution, and extra data fetching, then returns the
    /// populated <see cref="DispatchPipelineContext"/>, a customize function for the message, and
    /// an optional success callback for variant-specific logging.
    /// </para>
    /// </summary>
    /// <param name="agent">The agent being dispatched to.</param>
    /// <param name="identifier">Issue/PR/epic identifier.</param>
    /// <param name="identifierType">Type of identifier for logging ("issue", "PR", "epic").</param>
    /// <param name="revertProviderConfigId">Provider config ID for label revert on failure.</param>
    /// <param name="revertLabel">Label to swap back to on failure.</param>
    /// <param name="failureMessageTemplate">Serilog message template for error logging.</param>
    /// <param name="project">Optional project owning the template.</param>
    /// <param name="prepareAndCustomize">Variant-specific delegate that returns the pipeline context, message customizer, and optional success action; or null to abort.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="revertTargetKind">Optional label target kind (e.g., PullRequest for review path).</param>
    /// <returns><c>true</c> if the job was dispatched successfully; <c>false</c> on failure or abort.</returns>

    /// <summary>
    /// Groups the routing and revert parameters for <see cref="ExecuteDispatchPipelineAsync"/>
    /// to reduce its parameter count (S107).
    /// </summary>
    private sealed record DispatchRoutingParams(
        string Identifier,
        string IdentifierType,
        ProviderConfigId RevertProviderConfigId,
        string RevertLabel,
        string FailureMessageTemplate,
        PipelineProject? Project,
        LabelTargetKind? RevertTargetKind = null);

    private async Task<bool> ExecuteDispatchPipelineAsync(
        AgentEntry agent,
        DispatchRoutingParams routing,
        Func<PipelineProject, AgentProfile, string, CancellationToken, Task<DispatchPipelineResult?>> prepareAndCustomize,
        CancellationToken ct)
    {
        return await SafeDispatchAsync(agent, routing.RevertProviderConfigId, routing.Identifier, routing.RevertLabel,
            routing.FailureMessageTemplate,
            async () =>
        {
            var core = await ResolveDispatchCoreAsync(agent, routing.Identifier, routing.IdentifierType, routing.Project, ct);
            if (core is null) return false;
            var (proj, profile, agentProviderId) = core.Value;

            var result = await prepareAndCustomize(proj, profile, agentProviderId, ct);
            if (result is null) return false;
            var (pipelineCtx, customize, onSuccess) = result;

            // TODO: ApplyRunMetadata is called AFTER RegisterDispatchedRun (which fires NotifyChange) in the
            // review and decomposition paths. Subscribers reading the run between registration and this point
            // may observe ProjectId/ProjectName/ResolvedProfileId as null. Consider calling ApplyRunMetadata
            // inside the delegate before RegisterDispatchedRun, or suppressing notification until metadata is set.
            ApplyRunMetadata(pipelineCtx.Run, pipelineCtx.Project, pipelineCtx.Profile);
            await BuildAndSendAsync(pipelineCtx, customize, ct);
            onSuccess?.Invoke();
            return true;
        }, routing.RevertTargetKind);
    }

    /// <summary>
    /// Dispatches a job to a specific agent. Resolves the agent profile and quality gate
    /// configurations, creates the PipelineRun, prepares configs, and sends the
    /// <see cref="JobAssignmentMessage"/> via SignalR.
    /// </summary>
    internal async Task<bool> DispatchToAgentAsync(
        AgentEntry agent,
        IssueIdentifier issueIdentifier,
        ProviderConfigId issueProviderId,
        ProviderConfigId repoProviderId,
        string? brainProviderId,
        string? pipelineProviderId,
        string initiatedBy,
        IReadOnlyList<string> requiredLabels,
        CancellationToken ct,
        PipelineProject? project = null)
    {
        var preparation = new ImplementationDispatchPreparation(
            new ImplementationDispatchRequest(
                _infra, _orchestration, _logger, agent, issueIdentifier, issueProviderId,
                repoProviderId, brainProviderId, pipelineProviderId, initiatedBy, requiredLabels));

        return await ExecuteDispatchPipelineAsync(
            agent,
            new DispatchRoutingParams(
                issueIdentifier, "issue",
                RevertProviderConfigId: issueProviderId,
                RevertLabel: AgentLabels.Next,
                FailureMessageTemplate: "Failed to dispatch job to agent {AgentId} for issue {IssueIdentifier}",
                Project: project),
            (proj, profile, agentProviderId, token) =>
                preparation.PrepareAsync(proj, profile, agentProviderId, token),
            ct);
    }

    /// <summary>
    /// Dispatches a PR review job to a specific agent. Creates the PipelineRun with review metadata,
    /// pre-fetches linked issues, and sends the <see cref="JobAssignmentMessage"/> via SignalR.
    /// </summary>
    internal async Task<bool> DispatchReviewToAgentAsync(
        AgentEntry agent,
        ReviewDispatchRequest request,
        IReadOnlyList<string> requiredLabels,
        CancellationToken ct,
        PipelineProject? project = null)
    {
        var preparation = new ReviewDispatchPreparation(
            _infra, _orchestration, _logger, agent, request, requiredLabels);

        return await ExecuteDispatchPipelineAsync(
            agent,
            new DispatchRoutingParams(
                request.PrIdentifier, "PR",
                RevertProviderConfigId: request.RepoProviderId,
                RevertLabel: AgentLabels.Next,
                FailureMessageTemplate: "Failed to dispatch review job to agent {AgentId} for PR {PrIdentifier}",
                Project: project,
                RevertTargetKind: LabelTargetKind.PullRequest),
            (proj, profile, agentProviderId, token) =>
                preparation.PrepareAsync(proj, profile, agentProviderId, token),
            ct);
    }

    /// <summary>
    /// Dispatches a decomposition job to a specific agent. Creates the PipelineRun with the
    /// correct RunType (DecompositionAnalysis or Decomposition), sets workspace path to
    /// <c>{base}/decomposition/{runId}/</c>, and sends the <see cref="JobAssignmentMessage"/> via SignalR.
    /// </summary>
    internal async Task<bool> DispatchDecompositionToAgentAsync(
        AgentEntry agent,
        IssueIdentifier epicIdentifier,
        string epicTitle,
        PipelineRunType phaseType,
        ProviderConfigId issueProviderId,
        ProviderConfigId repoProviderId,
        string? brainProviderId,
        string initiatedBy,
        IReadOnlyList<string> requiredLabels,
        CancellationToken ct,
        string? decompositionSource = null,
        PipelineProject? project = null)
    {
        // Revert label on dispatch failure — Phase 1 reverts to agent:epic, Phase 2 reverts to agent:epic-approved
        var revertLabel = phaseType == PipelineRunType.DecompositionAnalysis
            ? AgentLabels.Epic
            : AgentLabels.EpicApproved;

        return await ExecuteDispatchPipelineAsync(
            agent,
            new DispatchRoutingParams(
                epicIdentifier, "epic",
                RevertProviderConfigId: issueProviderId,
                RevertLabel: revertLabel,
                FailureMessageTemplate: "Failed to dispatch decomposition job to agent {AgentId} for epic {EpicIdentifier}",
                Project: project),
            (proj, profile, agentProviderId, token) =>
            {
                // TODO: Strategy instantiation inside the lambda means a new object is created
                // for every dispatch attempt, even when ResolveDispatchCoreAsync returns null
                // (before the delegate is invoked). Since the class is stateless beyond readonly
                // fields, this is harmless but wasteful. Consider lazy-initializing or caching
                // the strategy instance, or restructuring the delegate to accept a pre-built
                // strategy.
                var preparation = new DecompositionDispatchPreparation(
                    new DecompositionDispatchRequest(
                        _infra, _orchestration, _logger, agent, epicIdentifier, epicTitle, phaseType,
                        issueProviderId, repoProviderId, brainProviderId, initiatedBy,
                        decompositionSource));
                return preparation.PrepareAsync(proj, profile, agentProviderId, token);
            },
            ct);
    }

    /// <summary>
    /// Ensures a non-null project, falling back to the Default project with a warning log
    /// if the template has no parent project (data corruption case).
    /// </summary>
    private PipelineProject EnsureProject(PipelineProject? project, string identifier, string identifierType)
    {
        if (project is not null)
            return project;

        _logger.Warning(
            "Template for {IdentifierType} {Identifier} has no parent project (data corruption). Assigning to Default project for settings resolution",
            identifierType, identifier);
        return new PipelineProject
        {
            Id = WellKnownIds.DefaultProjectId,
            Name = "Default"
        };
    }

    /// <summary>
    /// Assigns the job to the agent and sends the assignment via IAgentCommunication.
    /// Routes through IRunLifecycleManager.AgentAcceptedRunAsync to ensure label swap
    /// and agent state are handled uniformly across all modes.
    /// </summary>
    internal async Task AssignAndSendAsync(AgentEntry agent, string runId, JobAssignmentMessage message, CancellationToken ct)
    {
        await _agentComm.AssignJobAsync(agent.ConnectionId, message, ct);

        if (_lifecycleManager is not null)
        {
            await _lifecycleManager.AgentAcceptedRunAsync(
                runId, agent.AgentId,
                message.IssueIdentifier,
                new ProviderConfigId(message.IssueProviderConfigId ?? ""),
                new ProviderConfigId(message.RepoProviderConfigId ?? ""),
                message.RunType, ct);
        }
        else
        {
            // Fallback for tests without lifecycle manager
            // TODO: [WARNING] This branch only executes when _lifecycleManager is null. When
            // _lifecycleManager is non-null, ActiveJobId and Busy state are set inside
            // AgentAcceptedRunAsync. If AgentAcceptedRunAsync does not unconditionally set
            // ActiveJobId (e.g. due to a future refactor), the non-null lifecycle manager path
            // could leave ActiveJobId unset, re-enabling duplicate dispatch. Verify that
            // AgentAcceptedRunAsync always sets agent.ActiveJobId = runId before returning.
            agent.ActiveJobId = runId;
            _registry.TransitionStatus(agent.AgentId, AgentStatus.Busy);
        }
    }

    /// <summary>
    /// Handles dispatch failure by resetting agent status, logging the error, and reverting the label.
    /// </summary>
    private async Task RevertDispatchFailureAsync(
        AgentEntry agent,
        Exception ex,
        string messageTemplate,
        ProviderConfigId providerConfigId,
        string identifier,
        string revertLabel,
        LabelTargetKind? targetKind = null)
    {
        _logger.Error(ex, messageTemplate, agent.AgentId, identifier);

        // Remove the orphaned run from OrchestratorRunService to unblock future dispatch.
        // Without this, IsIssueBeingProcessed returns true forever in legacy mode.
        if (agent.ActiveJobId is not null)
            _runService.RemoveRun(agent.ActiveJobId);

        agent.ActiveJobId = null;
        _registry.TransitionStatus(agent.AgentId, AgentStatus.Idle);

        if (targetKind.HasValue)
            await _infra.LabelService.SwapLabelAsync(providerConfigId, identifier, revertLabel, targetKind.Value, CancellationToken.None);
        else
            await _infra.LabelService.SwapLabelAsync(providerConfigId, identifier, revertLabel, CancellationToken.None);
    }

    internal static Dictionary<string, string>? CaptureTraceContext() =>
        PipelineTelemetry.CaptureTraceContext("DispatchJob");
}
