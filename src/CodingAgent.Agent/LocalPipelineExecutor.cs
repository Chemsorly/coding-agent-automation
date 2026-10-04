using System.Diagnostics;
using CodingAgent.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Steps;
using CodingAgent.Pipeline.Telemetry;
using KiroCliLib.Core;
using Microsoft.AspNetCore.SignalR.Client;
using Serilog.Context;
namespace CodingAgent.Agent;

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
    private readonly AgentId _agentId;
    private readonly IAgentProviderResolver _providerResolver;
    private readonly PipelineExecutionContextBuilder _contextBuilder;
    private readonly Serilog.ILogger _logger;
    /// <summary>
    /// When non-null, overrides the internal <see cref="AgentProviderFactory"/> so test code
    /// can substitute fake repository and agent providers. Set via
    /// <see cref="LocalPipelineExecutorDependencies.ProviderFactoryOverride"/>.
    /// </summary>
    private readonly IProviderFactory? _providerFactoryOverride;

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
        _providerFactoryOverride = deps.ProviderFactoryOverride;
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

        // Resolve provider configs from the job assignment.
        // When a ProviderFactoryOverride is injected (test seam), the factory ignores ProviderConfig
        // contents entirely, so we skip the mandatory lookup and use a dummy config instead.
        // This allows smoke tests to insert minimal work-item rows without a fully-populated payload.
        ProviderConfig repoConfig;
        ProviderConfig agentConfig;
        if (_providerFactoryOverride is not null)
        {
            // The override factory ignores ProviderConfig contents entirely, so we use
            // placeholder values. The only required members are populated to satisfy the compiler.
            repoConfig = new ProviderConfig { DisplayName = "test-repo", Kind = ProviderKind.Repository, ProviderType = ProviderTypes.GitHub };
            agentConfig = new ProviderConfig { DisplayName = "test-agent", Kind = ProviderKind.Agent, ProviderType = ProviderTypes.KiroCli };
        }
        else
        {
            // TODO: These two mandatory lookups use TryGetProviderConfig + manual null-check-and-throw rather than
            // GetRequiredProviderConfig, because GetRequiredProviderConfig skips the structured _logger.Error call that
            // precedes the throw. If the error-logging requirement is relaxed, migrate to GetRequiredProviderConfig to
            // consolidate the "lookup + throw" pattern as originally intended by the extract.
            repoConfig = job.ProviderConfigs.TryGetProviderConfig(job.RepoProviderConfigId)!;
            if (repoConfig is null)
            {
                _logger.Error("Repository provider config '{RepoProviderConfigId}' not found in job assignment for job {JobId}", job.RepoProviderConfigId, job.JobId);
                throw new InvalidOperationException($"Repository provider config '{job.RepoProviderConfigId}' not found in job assignment");
            }
            agentConfig = job.ProviderConfigs.TryGetProviderConfig(job.AgentProviderConfigId)!;
            if (agentConfig is null)
            {
                _logger.Error("Agent provider config '{AgentProviderConfigId}' not found in job assignment for job {JobId}", job.AgentProviderConfigId, job.JobId);
                throw new InvalidOperationException($"Agent provider config '{job.AgentProviderConfigId}' not found in job assignment");
            }
        }

        // Override blacklist settings from repo provider config (per-repo takes precedence)
        config = PipelineConfigurationResolver.ApplyBlacklistOverride(config, repoConfig);

        // Construct a per-job provider factory with the OrchestratorProxy for token refresh.
        // Construction is intentionally placed after all config overrides (including blacklist)
        // so the factory always receives the final config state.
        // TODO: Add a test that sets a repo-level blacklist override and asserts the factory receives the
        // overridden config, to permanently protect the ordering invariant against future refactors.
        var issueOps = new OrchestratorProxy(connection, job.JobId);
        // TODO [WARNING]: The two-variable pattern here (issueOps + issueOpsDisposable pointing to the
        // same object) is redundant and potentially confusing. Prefer `using var issueOps = new OrchestratorProxy(...)`
        // as done in LocalConsolidationExecutor. Not a defect since Dispose() is idempotent, but
        // the alias introduces maintenance risk around ownership. (.NET Specialist Review)
        using var issueOpsDisposable = issueOps; // ensure _tokenCacheLock is disposed after the job completes
        // When a ProviderFactoryOverride is injected (test seam), use it instead of constructing
        // a real AgentProviderFactory. This allows fake repository / agent providers to be
        // substituted without modifying the rest of the execution path.
        var providerFactory = _providerFactoryOverride
            ?? (IProviderFactory)new AgentProviderFactory(_orchestrator, _httpClientFactory, config, issueOps);

        // The project repositories a project epic clones next to its own use the token vended into
        // their own config: the proxy's token refresh covers only this job's primary repository.
        var projectRepoFactory = _providerFactoryOverride
            ?? (IProviderFactory)new AgentProviderFactory(_orchestrator, _httpClientFactory, config);

        IRepositoryProvider? repoProvider = null;
        IAgentProvider? agentProvider = null;
        IRepositoryProvider? brainProvider = null;
        IPipelineProvider? pipelineProvider = null;
        List<(string TemplateName, IRepositoryProvider Provider)>? additionalRepoProviders = null;
        JobCompletionPayload? result = null;

        try
        {
            var resolved = await _providerResolver.ResolveAsync(job, providerFactory, projectRepoFactory, repoConfig, agentConfig, ct);
            repoProvider = resolved.RepoProvider;
            agentProvider = resolved.AgentProvider;
            brainProvider = resolved.BrainProvider;
            pipelineProvider = resolved.PipelineProvider;
            additionalRepoProviders = resolved.AdditionalRepoProviders;

            // Warn when an implementation run has no brain provider — post-run reflection and brain sync
            // will be silently skipped, and brain_updates_* metrics will never be populated.
            // Review and Decomposition runs reach pre-run brain sync but never reach post-run finalization
            // (they exit at PostingFindings/PostPlan), so the absence of a brain provider is expected there.
            WarnIfNoBrainProvider(job);

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
            else if (result.FinalStep == PipelineStep.Failed)
                instrumentation.Activity?.SetStatus(ActivityStatusCode.Error, result.FailureReason ?? result.FinalStep.ToString());
            return result;
        }
        catch (Exception ex)
        {
            instrumentation.Activity?.RecordError(ex, ct);
            throw;
        }
        finally
        {
            PipelineRunInstrumentation.StopTiming();
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

            // Inject additional repo providers: a project epic's decomposition and a project review clone them
            if (additionalRepoProviders is { Count: > 0 })
                stepContext.AdditionalRepoProviders = additionalRepoProviders;

            // Build step pipeline based on run type
            var steps = run.RunType switch
            {
                PipelineRunType.Review => AgentStepPipelineBuilder.BuildReviewStepPipeline(job, issueOps, repoConfig),
                PipelineRunType.DecompositionAnalysis => AgentStepPipelineBuilder.BuildDecompositionAnalysisStepPipeline(job, issueOps, repoConfig),
                PipelineRunType.Decomposition => AgentStepPipelineBuilder.BuildDecompositionStepPipeline(job, issueOps, repoConfig),
                _ => AgentStepPipelineBuilder.BuildAgentStepPipeline(job, issueOps, repoConfig)
            };

            var outcome = await PipelineRunExecutionHost.ExecuteStepsAsync(steps, stepContext, linkedCt);

            switch (outcome)
            {
                case PipelineExecutionOutcome.CompletedOutcome:
                    // For review/decomposition runs, the step pipeline ends at PostingFindings/PostPlan/PostSummary.
                    // Transition to Completed here (implementation runs do this in CreatePullRequestAsync).
                    // NOTE: Because review/decomposition runs terminate here without going through
                    // CreatePullRequestAsync → RunFullPrCreationAsync → RunPostPrSequenceAsync,
                    // post-run brain sync (and brain_updates_* metrics) is NEVER executed for these run types.
                    // This is by design. brain_syncs_completed_total (pre-run) will still increment for
                    // review/decomposition runs if a brain provider is configured, creating an apparent
                    // asymmetry where pre-run brain metrics exist but post-run metrics are absent.
                    if (run.RunType is PipelineRunType.Review or PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition
                        && run.CurrentStep is not PipelineStep.Failed and not PipelineStep.Cancelled
                               and not PipelineStep.PrMerged and not PipelineStep.PrClosed)
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
                        RunMode = run.RunMode,
                        FinalLabel = AgentLabels.Cancelled
                    };

                case PipelineExecutionOutcome.FailedOutcome { Exception: var ex }:
                    _logger.Error(ex, "Pipeline execution failed with unhandled error");
                    return BuildFailurePayload(run, ex.Message, run.FailureCategory);

                default:
                    throw new InvalidOperationException($"Unexpected pipeline execution outcome: {outcome.GetType().Name}");
            }
        }
        finally
        {
            // TODO: [WARNING] If PipelineCleanup.RunAsync throws, the exception propagates to the outer
            // catch (Exception ex) in ExecuteAsync which calls instrumentation.Activity?.RecordError(ex, ct),
            // setting Error on ExecutePipeline due to a cleanup failure rather than a pipeline-logic failure.
            // This is pre-existing behaviour (not introduced by this diff), but should be noted: a cleanup
            // failure can misrepresent a successful pipeline run as errored in traces.
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
        AnalysisRecommendation = run.AnalysisRecommendation,
        // BranchName is populated in the payload so that K8s mode (HttpPrimaryCompletionReporter)
        // can fire an intermediate Running+BranchName POST before the terminal status. During the
        // run, the hub already stores WorkItems.BranchName from the step-transition metadata
        // (AgentJobLifecycleService.HandleStepTransition).
        BranchName = run.BranchName
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
        RunMode = run.RunMode,
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
        FinalLabel = run.FinalLabel,
        HarnessVersion = Environment.GetEnvironmentVariable("SERVICE_VERSION"),
        PhaseBreakdown = BuildPhaseBreakdownPayload(run.Metrics.PhaseBreakdown)
    };

    /// <summary>
    /// Converts the in-memory <see cref="RunMetrics.PhaseBreakdown"/> to a
    /// <see cref="PhaseUsagePayload"/> dictionary suitable for wire serialization.
    /// Returns null when the breakdown is empty.
    /// </summary>
    private static IReadOnlyDictionary<string, PhaseUsagePayload>? BuildPhaseBreakdownPayload(
        System.Collections.Concurrent.ConcurrentDictionary<string, PhaseUsage> breakdown)
    {
        if (breakdown.IsEmpty)
            return null;

        return breakdown.ToDictionary(
            kvp => kvp.Key,
            kvp => new PhaseUsagePayload
            {
                Tokens = kvp.Value.Tokens,
                Cost = kvp.Value.Cost,
                SessionCount = kvp.Value.SessionCount,
                AgentTimeSeconds = kvp.Value.AgentTimeSeconds,
                Provider = kvp.Value.Provider,
                Model = kvp.Value.Model
            });
    }

    /// <summary>
    /// Emits a warning when an implementation-type run has no brain provider configured.
    /// Extracted so it can be tested without going through the full <see cref="ExecuteAsync"/> pipeline
    /// (which emits pipeline telemetry counters that pollute cross-assembly MeterListener tests).
    /// </summary>
    internal void WarnIfNoBrainProvider(JobAssignmentMessage job)
    {
        if (job.RunType is not (PipelineRunType.Review or PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition)
            && string.IsNullOrEmpty(job.BrainProviderConfigId))
        {
            _logger.Warning(
                "Job {JobId} for {IssueIdentifier} has no BrainProviderConfigId configured. " +
                "Post-run reflection and brain sync will be skipped. " +
                "Set BrainProviderId on the job template to enable brain updates.",
                job.JobId, job.IssueIdentifier);
        }
    }

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
