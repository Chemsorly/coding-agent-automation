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
    private readonly PipelineConfiguration _defaultPipelineConfig;
    private readonly IQualityGateValidator _qualityGateValidator;
    private readonly IBrainUpdateService? _brainUpdateService;
    private readonly IPipelineRunHistoryService? _historyService;
    private readonly IOpenIssueContextWriter _openIssueContextWriter;
    private readonly AgentIdentity _agentIdentity;
    private readonly AgentProviderResolver _providerResolver;
    private readonly IPipelineReporterFactory _reporterFactory;
    private readonly PipelineExecutionContextBuilder _contextBuilder;
    private readonly Serilog.ILogger _logger;

    public LocalPipelineExecutor(
        IKiroCliOrchestrator orchestrator,
        IHttpClientFactory httpClientFactory,
        PipelineConfiguration defaultPipelineConfig,
        IQualityGateValidator qualityGateValidator,
        Serilog.ILogger logger,
        IBrainUpdateService? brainUpdateService = null,
        IPipelineRunHistoryService? historyService = null,
        IOpenIssueContextWriter? openIssueContextWriter = null,
        AgentIdentity? agentIdentity = null,
        IPipelineReporterFactory? reporterFactory = null)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(defaultPipelineConfig);
        ArgumentNullException.ThrowIfNull(qualityGateValidator);
        ArgumentNullException.ThrowIfNull(logger);

        _orchestrator = orchestrator;
        _httpClientFactory = httpClientFactory;
        _defaultPipelineConfig = defaultPipelineConfig;
        _qualityGateValidator = qualityGateValidator;
        _brainUpdateService = brainUpdateService;
        _historyService = historyService;
        _openIssueContextWriter = openIssueContextWriter ?? new OpenIssueContextWriter(logger);
        _agentIdentity = agentIdentity ?? new AgentIdentity(Environment.MachineName);
        _providerResolver = new AgentProviderResolver(logger);
        _reporterFactory = reporterFactory ?? new PipelineReporterFactory(logger);
        var feedbackService = new FeedbackService(logger);
        var finalization = new PullRequestFinalizationService(logger);
        _contextBuilder = new PipelineExecutionContextBuilder(
            qualityGateValidator, _reporterFactory, feedbackService, _agentIdentity, logger,
            brainUpdateService, historyService, finalization);
        _logger = logger;
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
        instrumentation.Activity?.SetTag("pipeline.agent_id", _agentIdentity.Id);

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

            result = await ExecutePipelineStepsAsync(
                job, config, repoProvider, agentProvider, brainProvider, pipelineProvider,
                issueOps, repoConfig, connection, outputBatcher, onStepChanged, ct, additionalRepoProviders);

            if (result.FinalStep == PipelineStep.Completed)
                instrumentation.MarkCompleted();

            instrumentation.Activity?.SetTag("pipeline.final_step", result.FinalStep.ToString());
            // TODO: Add test that verifies cancelled runs set pipeline.cancelled tag with Unset status (no Error)
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

    private async Task<JobCompletionPayload> ExecutePipelineStepsAsync(
        JobAssignmentMessage job,
        PipelineConfiguration config,
        IRepositoryProvider repoProvider,
        IAgentProvider agentProvider,
        IRepositoryProvider? brainProvider,
        IPipelineProvider? pipelineProvider,
        OrchestratorProxy issueOps,
        ProviderConfig repoConfig,
        HubConnection connection,
        OutputBatcher outputBatcher,
        Action<PipelineStep?>? onStepChanged,
        CancellationToken ct,
        List<(string TemplateName, IRepositoryProvider Provider)>? additionalRepoProviders = null)
    {
        var buildResult = await _contextBuilder.Build(
            job, config, repoProvider, agentProvider, brainProvider, pipelineProvider,
            issueOps, connection, outputBatcher, onStepChanged, ct);

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
