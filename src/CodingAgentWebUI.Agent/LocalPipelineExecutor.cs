using System.Diagnostics;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using CodingAgentWebUI.Pipeline.Telemetry;
using KiroCliLib.Core;
using Microsoft.AspNetCore.SignalR.Client;
using Serilog.Context;
namespace CodingAgentWebUI.Agent;

/// <summary>
/// Executes the full pipeline locally on the agent via <see cref="PipelineRunExecutionHost"/>.
/// Reports all progress back to the orchestrator via SignalR hub methods.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hub-to-Pipeline Bridge:</b> This class bridges the event-driven SignalR hub layer
/// with the sequential pipeline execution model. When <see cref="AgentWorkerService"/>
/// receives a <c>JobAssignmentMessage</c> via the hub, it delegates to this executor which:
/// </para>
/// <list type="number">
///   <item>Constructs provider instances (repository, agent, issue, pipeline, brain) from
///     the job's provider configurations using <see cref="AgentProviderFactory"/>.</item>
///   <item>Builds a <see cref="Pipeline.Services.Steps.PipelineStepContext"/> with all resolved
///     providers, callbacks, and configuration.</item>
///   <item>Runs the pipeline steps sequentially via <see cref="Pipeline.Services.Steps.PipelineStepRunner"/>.</item>
///   <item>Reports progress back to the orchestrator by invoking hub methods (e.g.,
///     <c>ReportStepTransition</c>, <c>ReportOutput</c>) through an <c>AgentCallbacks</c>
///     implementation of <see cref="Pipeline.Interfaces.IPipelineCallbacks"/>.</item>
/// </list>
/// <para>
/// This design allows the agent to execute the same pipeline logic as the orchestrator's
/// server-side execution path, ensuring behavioral parity between local and remote execution.
/// </para>
/// </remarks>
public sealed class LocalPipelineExecutor : IPipelineExecutor
{
    private readonly IKiroCliOrchestrator _orchestrator;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IOpenIssueContextWriter _openIssueContextWriter;
    private readonly AgentId _agentId;
    private readonly AgentProviderResolver _providerResolver;
    private readonly PipelineExecutionContextBuilder _contextBuilder;
    private readonly Serilog.ILogger _logger;

    public LocalPipelineExecutor(LocalPipelineExecutorDependencies deps)
    {
        ArgumentNullException.ThrowIfNull(deps);
        ArgumentNullException.ThrowIfNull(deps.Orchestrator);
        ArgumentNullException.ThrowIfNull(deps.HttpClientFactory);
        ArgumentNullException.ThrowIfNull(deps.DefaultPipelineConfig);
        ArgumentNullException.ThrowIfNull(deps.QualityGateValidator);
        ArgumentNullException.ThrowIfNull(deps.Logger);

        _orchestrator = deps.Orchestrator;
        _httpClientFactory = deps.HttpClientFactory;
        _openIssueContextWriter = deps.OpenIssueContextWriter ?? new OpenIssueContextWriter(deps.Logger);
        _agentId = deps.AgentIdentity ?? new AgentId(Environment.MachineName);
        _providerResolver = new AgentProviderResolver(deps.Logger);
        var reporterFactory = deps.ReporterFactory ?? new PipelineReporterFactory(deps.Logger);
        var feedbackService = new FeedbackService(deps.Logger);
        var finalization = new PullRequestFinalizationService(deps.Logger);
        _contextBuilder = new PipelineExecutionContextBuilder(
            new PipelineExecutionContextBuilderDependencies(
                deps.QualityGateValidator, reporterFactory, feedbackService, _agentId, deps.Logger,
                deps.BrainUpdateService, deps.HistoryService, finalization));
        _logger = deps.Logger;
    }

    /// <summary>
    /// Executes the full pipeline for the given job assignment.
    /// Reports all progress to the orchestrator via the hub connection.
    /// </summary>
    public async Task<JobCompletionPayload> ExecuteAsync(
        JobAssignmentMessage job,
        HubConnection connection,
        OutputBatcher outputBatcher,
        Action<PipelineStep?>? onStepChanged,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(outputBatcher);

        using var instrumentation = PipelineRunInstrumentation.Start(
            job.JobId, job.IssueIdentifier, job.RunType, job.ProjectId, job.ProjectName,
            ActivityKind.Consumer,
            PipelineTelemetry.ExtractTraceContext(job.TraceContext));
        instrumentation.Activity?.SetTag("pipeline.agent_id", _agentId.Value);

        var config = job.PipelineConfiguration;
        var issueOps = new OrchestratorProxy(connection, job.JobId);

        // Construct a per-job provider factory with the OrchestratorProxy for token refresh
        // TODO: Factory captures config before blacklist override below. Move construction after
        // the override block if AgentProviderFactory ever needs blacklist settings.
        var providerFactory = new AgentProviderFactory(_orchestrator, _httpClientFactory, config, issueOps);

        // Resolve provider configs from the job assignment
        var repoConfig = job.ProviderConfigs.FirstOrDefault(c => c.Id == job.RepoProviderConfigId);
        if (repoConfig is null)
        {
            _logger.Error("Repository provider config '{RepoProviderConfigId}' not found in job assignment for job {JobId}", job.RepoProviderConfigId, job.JobId);
            throw new InvalidOperationException($"Repository provider config '{job.RepoProviderConfigId}' not found in job assignment");
        }
        var agentConfig = job.ProviderConfigs.FirstOrDefault(c => c.Id == job.AgentProviderConfigId);
        if (agentConfig is null)
        {
            _logger.Error("Agent provider config '{AgentProviderConfigId}' not found in job assignment for job {JobId}", job.AgentProviderConfigId, job.JobId);
            throw new InvalidOperationException($"Agent provider config '{job.AgentProviderConfigId}' not found in job assignment");
        }

        // Override blacklist settings from repo provider config (per-repo takes precedence)
        config = PipelineConfigurationResolver.ApplyBlacklistOverride(config, repoConfig);

        IRepositoryProvider? repoProvider = null;
        IAgentProvider? agentProvider = null;
        IRepositoryProvider? brainProvider = null;
        IPipelineProvider? pipelineProvider = null;
        List<(string TemplateName, IRepositoryProvider Provider)>? additionalRepoProviders = null;
        JobCompletionPayload? result = null;

        try
        {
            var resolved = await _providerResolver.ResolveAsync(job, providerFactory, repoConfig, agentConfig, ct);
            repoProvider = resolved.RepoProvider;
            agentProvider = resolved.AgentProvider;
            brainProvider = resolved.BrainProvider;
            pipelineProvider = resolved.PipelineProvider;
            additionalRepoProviders = resolved.AdditionalRepoProviders;

            // Merge provider-specific paths into configurable blacklist AND store for hardcoded enforcement
            config = PipelineConfigurationResolver.ApplyProviderBlacklist(config, agentProvider.PipelineInjectedPaths);
            config = config with { PipelineInjectedPaths = agentProvider.PipelineInjectedPaths };

            result = await ExecutePipelineStepsAsync(new ExecutePipelineStepsRequest(
                job, config, repoProvider, agentProvider, brainProvider, pipelineProvider,
                issueOps, repoConfig, connection, outputBatcher, onStepChanged, ct, additionalRepoProviders));

            if (result.FinalStep == PipelineStep.Completed)
                instrumentation.MarkCompleted();
            else if (result.FinalStep != PipelineStep.Cancelled)
                instrumentation.MarkFailed(result.FailureCategory);

            instrumentation.Activity?.SetTag("pipeline.final_step", result.FinalStep.ToString());
            if (result.FinalStep == PipelineStep.Cancelled)
                instrumentation.Activity?.SetTag("pipeline.cancelled", true);
            else if (result.FinalStep != PipelineStep.Completed)
                instrumentation.Activity?.SetStatus(ActivityStatusCode.Error, result.FinalStep.ToString());
            return result;
        }
        catch (Exception ex)
        {
            instrumentation.Activity?.RecordError(ex, ct);
            throw;
        }
        finally
        {
            instrumentation.StopTiming();
            await ProviderDisposer.DisposeAllAsync(repoProvider, agentProvider, brainProvider, pipelineProvider);
            if (additionalRepoProviders is not null)
                await ProviderDisposer.DisposeAllAsync(additionalRepoProviders.Select(p => p.Provider as IAsyncDisposable));
        }
    }

    private async Task<JobCompletionPayload> ExecutePipelineStepsAsync(ExecutePipelineStepsRequest req)
    {
        var job = req.Job;
        var config = req.Config;
        var repoProvider = req.RepoProvider;
        var agentProvider = req.AgentProvider;
        var brainProvider = req.BrainProvider;
        var pipelineProvider = req.PipelineProvider;
        var issueOps = req.IssueOps;
        var repoConfig = req.RepoConfig;
        var connection = req.Connection;
        var outputBatcher = req.OutputBatcher;
        var onStepChanged = req.OnStepChanged;
        var ct = req.Ct;
        var additionalRepoProviders = req.AdditionalRepoProviders;
        var buildResult = await _contextBuilder.Build(new PipelineBuildRequest(
            job, config, repoProvider, agentProvider, brainProvider, pipelineProvider,
            issueOps, connection, outputBatcher, onStepChanged, ct));

        var run = buildResult.Run;
        var reporter = buildResult.Reporter;

        using var _runIdCtx = LogContext.PushProperty("PipelineRunId", run.RunId);
        using var _issueCtx = LogContext.PushProperty("IssueIdentifier", run.IssueIdentifier);

        PipelineStepContext? stepContext = null;

        try
        {
            var linkedCt = buildResult.LocalCts.Token;

            stepContext = _contextBuilder.CreateStepContext(buildResult.ExecutionContext, reporter, ct);
            buildResult.StepContext = stepContext;

            // Inject additional repo providers for cross-repo decomposition cloning
            if (additionalRepoProviders is { Count: > 0 })
                stepContext.AdditionalRepoProviders = additionalRepoProviders;

            // Build step pipeline based on run type
            var steps = run.RunType switch
            {
                PipelineRunType.Review => AgentStepPipelineBuilder.BuildReviewStepPipeline(job, issueOps, repoConfig),
                PipelineRunType.DecompositionAnalysis => AgentStepPipelineBuilder.BuildDecompositionAnalysisStepPipeline(job, _openIssueContextWriter, issueOps, repoConfig),
                PipelineRunType.Decomposition => AgentStepPipelineBuilder.BuildDecompositionStepPipeline(job, _openIssueContextWriter, issueOps, repoConfig),
                _ => AgentStepPipelineBuilder.BuildAgentStepPipeline(job, issueOps, repoConfig)
            };

            var outcome = await PipelineRunExecutionHost.ExecuteStepsAsync(steps, stepContext, linkedCt);

            switch (outcome)
            {
                case PipelineExecutionOutcome.CompletedOutcome:
                    // For review/decomposition runs, the step pipeline ends at PostingFindings/PostPlan/PostSummary.
                    // Transition to Completed here (implementation runs do this in CreatePullRequestAsync).
                    if (run.RunType is PipelineRunType.Review or PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition
                        && run.CurrentStep is not PipelineStep.Failed and not PipelineStep.Cancelled)
                    {
                        run.MarkCompleted();
                        run.CurrentStep = PipelineStep.Completed;
                        run.FinalLabel ??= AgentLabels.Done;
                    }

                    return BuildCompletionPayload(run);

                case PipelineExecutionOutcome.CancelledOutcome:
                    run.MarkCompleted();

                    // Note: reporter.TransitionTo is fire-and-forget (not awaited), so the Cancelled
                    // transition and subsequent EmitOutputLine may race. DisposeAsync in the finally
                    // block drains both, but orchestrator may observe non-deterministic order.
                    reporter.TransitionTo(PipelineStep.Cancelled, CancellationToken.None);
                    buildResult.EmitOutputLine("🚫 Pipeline cancelled");

                    run.FinalLabel = AgentLabels.Cancelled;
                    return new JobCompletionPayload
                    {
                        FinalStep = PipelineStep.Cancelled,
                        CompletedAt = DateTimeOffset.UtcNow,
                        RetryCount = run.RetryCount,
                        IsRework = run.LinkedPullRequest is not null,
                        FinalLabel = AgentLabels.Cancelled
                    };

                case PipelineExecutionOutcome.FailedOutcome { Exception: var ex }:
                    _logger.Error(ex, "Pipeline execution failed with unhandled error");
                    // TODO: Pass run.FailureCategory here so that failures categorized before the exception
                    // propagates (e.g., ReconciliationService sets FailureReason.Timeout, or
                    // DisconnectedAgentSweepPhase sets FailureReason.InfrastructureFailure) are not lost.
                    // Without it, result.FailureCategory will be null and MarkFailed() will emit
                    // failure_reason="unknown" even when a specific reason is already recorded on the run.
                    // Fix: return BuildFailurePayload(run, ex.Message, run.FailureCategory);
                    return BuildFailurePayload(run, ex.Message);

                default:
                    throw new InvalidOperationException($"Unexpected pipeline execution outcome: {outcome.GetType().Name}");
            }
        }
        finally
        {
            await PipelineCleanup.RunAsync(buildResult.LocalCts, stepContext, run, reporter, _logger);
        }
    }

    internal static JobCompletionPayload BuildCompletionPayload(PipelineRun run) => BuildPayloadBase(run) with
    {
        FinalStep = run.CurrentStep,
        FailureReason = run.FailureReason,
        FailureCategory = run.FailureCategory,
        PullRequestUrl = run.PullRequestUrl,
        PullRequestNumber = run.PullRequestNumber,
        IsDraftPr = run.IsDraftPr,
        CompletedAt = run.CompletedAtOffset ?? DateTimeOffset.UtcNow,
        BrainUpdatesPushed = run.BrainUpdatesPushed,
        AnalysisRecommendation = run.AnalysisRecommendation
    };

    internal static JobCompletionPayload BuildFailurePayload(PipelineRun run, string reason, FailureReason? failureCategory = null) => BuildPayloadBase(run) with
    {
        FinalStep = PipelineStep.Failed,
        FailureReason = reason,
        FailureCategory = failureCategory,
        CompletedAt = DateTimeOffset.UtcNow
    };

    private static JobCompletionPayload BuildPayloadBase(PipelineRun run) => new()
    {
        FinalStep = PipelineStep.Failed, // Placeholder — callers override via 'with'
        CompletedAt = DateTimeOffset.UtcNow, // Placeholder — callers override via 'with'
        RetryCount = run.RetryCount,
        IsRework = run.LinkedPullRequest is not null,
        FilesChangedCount = run.FilesChangedCount,
        LinesAdded = run.LinesAdded,
        LinesRemoved = run.LinesRemoved,
        AnalysisConcerns = run.AnalysisConcerns,
        AnalysisBlockingIssues = run.AnalysisBlockingIssues,
        BlacklistedFilesDetected = run.BlacklistedFilesDetected,
        CodeReviewAgentsRun = run.CodeReviewAgentsRun,
        CodeReviewCriticalCount = run.CodeReviewCriticalCount,
        CodeReviewWarningCount = run.CodeReviewWarningCount,
        CodeReviewSuggestionCount = run.CodeReviewSuggestionCount,
        Feedback = run.Feedback,
        TotalTokens = run.TotalTokens,
        TotalCost = run.TotalCost,
        FinalLabel = run.FinalLabel
    };

}

/// <summary>
/// Groups the parameters for <see cref="LocalPipelineExecutor.ExecutePipelineStepsAsync"/>
/// to reduce method parameter count (S107).
/// </summary>
internal sealed record ExecutePipelineStepsRequest(
    JobAssignmentMessage Job,
    PipelineConfiguration Config,
    IRepositoryProvider RepoProvider,
    IAgentProvider AgentProvider,
    IRepositoryProvider? BrainProvider,
    IPipelineProvider? PipelineProvider,
    OrchestratorProxy IssueOps,
    ProviderConfig RepoConfig,
    HubConnection Connection,
    OutputBatcher OutputBatcher,
    Action<PipelineStep?>? OnStepChanged,
    CancellationToken Ct,
    List<(string TemplateName, IRepositoryProvider Provider)>? AdditionalRepoProviders = null);
