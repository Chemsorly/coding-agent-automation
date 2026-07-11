using System.Diagnostics;
using CodingAgentWebUI.Agent.KiroCli;
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
/// Executes the full pipeline locally on the agent, replicating the flow from
/// <see cref="PipelineOrchestrationService.ExecutePipelineStepsAsync"/>.
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
    private readonly FeedbackService _feedbackService;
    private readonly PullRequestFinalizationService _finalization;
    private readonly AgentIdentity _agentIdentity;
    private readonly AgentProviderResolver _providerResolver;
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
        AgentIdentity? agentIdentity = null)
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
        _feedbackService = new FeedbackService(logger);
        _finalization = new PullRequestFinalizationService(logger);
        _agentIdentity = agentIdentity ?? new AgentIdentity(Environment.MachineName);
        _providerResolver = new AgentProviderResolver(logger);
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

        // TODO: Duration metric inflation — using-declaration causes Dispose() to run AFTER the finally block's
        // provider disposal, so JobDuration now includes provider cleanup time. Consider stopping the stopwatch
        // explicitly before provider disposal or moving instrumentation inside a narrower scope.
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
        config = PipelineConfiguration.ApplyBlacklistOverride(config, repoConfig);

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
            config = PipelineConfiguration.ApplyProviderBlacklist(config, agentProvider.PipelineInjectedPaths);
            config = config with { PipelineInjectedPaths = agentProvider.PipelineInjectedPaths };

            result = await ExecutePipelineStepsAsync(
                job, config, repoProvider, agentProvider, brainProvider, pipelineProvider,
                issueOps, connection, outputBatcher, onStepChanged, ct, additionalRepoProviders);

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
            await ProviderDisposer.DisposeAllAsync(repoProvider, agentProvider, brainProvider, pipelineProvider);
            if (additionalRepoProviders is not null)
                await ProviderDisposer.DisposeAllAsync(additionalRepoProviders.Select(p => p.Provider as IAsyncDisposable));
        }
    }

    /// <summary>
    /// Delegates to <see cref="AgentProviderResolver.ResolveAsync"/>. Retained as internal
    /// for backward compatibility with existing unit tests that verify disposal behavior.
    /// </summary>
    internal async Task<(IRepositoryProvider RepoProvider, IAgentProvider AgentProvider, IRepositoryProvider? BrainProvider, IPipelineProvider? PipelineProvider, List<(string TemplateName, IRepositoryProvider Provider)>? AdditionalRepoProviders)> ResolveProvidersAsync(
        JobAssignmentMessage job,
        IProviderFactory providerFactory,
        ProviderConfig repoConfig,
        ProviderConfig agentConfig,
        CancellationToken ct)
    {
        var resolved = await _providerResolver.ResolveAsync(job, providerFactory, repoConfig, agentConfig, ct);
        return (resolved.RepoProvider, resolved.AgentProvider, resolved.BrainProvider, resolved.PipelineProvider, resolved.AdditionalRepoProviders);
    }

    private async Task<JobCompletionPayload> ExecutePipelineStepsAsync(
        JobAssignmentMessage job,
        PipelineConfiguration config,
        IRepositoryProvider repoProvider,
        IAgentProvider agentProvider,
        IRepositoryProvider? brainProvider,
        IPipelineProvider? pipelineProvider,
        OrchestratorProxy issueOps,
        HubConnection connection,
        OutputBatcher outputBatcher,
        Action<PipelineStep?>? onStepChanged,
        CancellationToken ct,
        List<(string TemplateName, IRepositoryProvider Provider)>? additionalRepoProviders = null)
    {
        var run = PipelineRun.Create(
            runId: job.JobId,
            issueIdentifier: job.IssueIdentifier,
            issueTitle: job.IssueDetail.Title,
            issueProviderConfigId: string.Empty, // Agent doesn't have issue provider
            repoProviderConfigId: job.RepoProviderConfigId,
            runType: job.RunType,
            initiatedBy: job.InitiatedBy,
            agentId: _agentIdentity.Id,
            brainProviderConfigId: brainProvider is not null ? job.BrainProviderConfigId : null,
            reviewPrBranchName: job.LinkedPullRequest?.BranchName,
            reviewPrTargetBranch: job.ReviewPrTargetBranch,
            reviewPrDescription: job.ReviewPrDescription,
            reviewPrAuthor: job.ReviewPrAuthor,
            linkedIssueContexts: job.LinkedIssueContexts);
        run.RepositoryName = repoProvider.RepositoryFullName;
        run.ModelName = agentProvider is KiroCliAgentProvider kp ? kp.Model : null;
        run.PipelineProviderConfigId = job.PipelineProviderConfigId;
        run.LinkedPullRequest = job.LinkedPullRequest;
        run.ProjectId = job.ProjectId;
        run.ProjectName = job.ProjectName;

        run.IssueLabels = job.IssueDetail.Labels;

        // Orchestrators
        var agentExecution = new AgentPhaseExecutor(_logger);
        var prOrchestrator = new PullRequestOrchestrator(_logger);
        var qualityGates = new QualityGateExecutor(_qualityGateValidator, prOrchestrator, new CiLogWriter(_logger), _feedbackService, _logger, _historyService);
        BrainSyncService? brainSync = _brainUpdateService is not null
            ? new BrainSyncService(_brainUpdateService, _logger)
            : null;

        // Fire-and-forget wrappers — delegate to class-level async methods for testability.
        // Serialized via signalrLock to guarantee ordering at the orchestrator.
        // Not using 'using' — disposed manually after draining in-flight sends.
        var signalrLock = new SemaphoreSlim(1, 1);
        void TransitionTo(PipelineStep step) => _ = SerializedSendAsync(signalrLock, () => TransitionToInternalAsync(run, connection, job.JobId, onStepChanged, step, ct), ct);
        void ReportQualityGateResult(QualityGateReport report) => _ = SerializedSendAsync(signalrLock, () => ReportQualityGateResultInternalAsync(connection, job.JobId, report, ct), ct);

        CancellationTokenSource? localCts = null;
        PipelineStepContext? context = null;

        // Wrap EmitOutputLine so ALL output is masked once secrets are populated.
        // context.InjectedSecrets is null until RunEnvironmentSetupStep populates it,
        // so output before that step passes through unmasked (no secrets exist yet).
        void EmitOutputLine(string line)
        {
            var masked = MaskSecretsInOutput(line, context);
            _ = EmitOutputLineInternalAsync(run, outputBatcher, masked, ct);
        }

        using var _runIdCtx = LogContext.PushProperty("PipelineRunId", run.RunId);
        using var _issueCtx = LogContext.PushProperty("IssueIdentifier", run.IssueIdentifier);

        try
        {
            localCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var linkedCt = localCts.Token;

            var prContext = new PullRequestCreationContext
            {
                RepoProvider = repoProvider,
                AgentProvider = agentProvider,
                BrainProvider = brainProvider,
                BrainSync = brainSync,
                Config = config,
                IssueOps = issueOps,
                Connection = connection,
                Job = job,
                PrOrchestrator = prOrchestrator,
                EmitOutputLine = EmitOutputLine
            };

            // Build step context
            var executionContext = new PipelineExecutionContext
            {
                Job = job,
                Run = run,
                Config = config,
                RepoProvider = repoProvider,
                AgentProvider = agentProvider,
                BrainProvider = brainProvider,
                BrainSync = brainSync,
                PipelineProvider = pipelineProvider,
                IssueOps = issueOps,
                Connection = connection,
                PrOrchestrator = prOrchestrator,
                AgentExecution = agentExecution,
                QualityGates = qualityGates,
                LocalCts = localCts,
                PrContext = prContext,
                TransitionTo = TransitionTo,
                EmitOutputLine = EmitOutputLine,
                ReportQualityGateResult = ReportQualityGateResult
            };

            context = CreateStepContext(executionContext, ct);

            // Inject additional repo providers for cross-repo decomposition cloning
            if (additionalRepoProviders is { Count: > 0 })
                context.AdditionalRepoProviders = additionalRepoProviders;

            // Build step pipeline based on run type
            var steps = run.RunType switch
            {
                PipelineRunType.Review => BuildReviewStepPipeline(job),
                PipelineRunType.DecompositionAnalysis => BuildDecompositionAnalysisStepPipeline(job, _openIssueContextWriter),
                PipelineRunType.Decomposition => BuildDecompositionStepPipeline(job, _openIssueContextWriter),
                _ => BuildAgentStepPipeline(job, connection)
            };

            await PipelineStepRunner.ExecuteAsync(steps, context, linkedCt);

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
        }
        catch (OperationCanceledException)
        {
            run.MarkCompleted();

            // Await directly with CancellationToken.None — ct is already cancelled so the
            // fire-and-forget wrapper would fail to acquire the semaphore.
            await SerializedSendAsync(signalrLock, () => TransitionToInternalAsync(run, connection, job.JobId, onStepChanged, PipelineStep.Cancelled, CancellationToken.None), CancellationToken.None);
            EmitOutputLine("🚫 Pipeline cancelled");

            run.FinalLabel = AgentLabels.Cancelled;
            return new JobCompletionPayload
            {
                FinalStep = PipelineStep.Cancelled,
                CompletedAt = DateTimeOffset.UtcNow,
                RetryCount = run.RetryCount,
                IsRework = run.LinkedPullRequest is not null,
                FinalLabel = AgentLabels.Cancelled
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Pipeline execution failed with unhandled error");

            return BuildFailurePayload(run, ex.Message);
        }
        finally
        {
            localCts?.Dispose();

            // Clean up injected environment secrets
            if (context?.InjectedSecretKeys is { Count: > 0 })
            {
                foreach (var key in context.InjectedSecretKeys)
                    Environment.SetEnvironmentVariable(key, null);
                _logger.Debug("Cleaned up {Count} injected secret keys", context.InjectedSecretKeys.Count);
            }

            // Workspace cleanup
            try
            {
                if (run.CurrentStep is PipelineStep.Completed or PipelineStep.Failed or PipelineStep.Cancelled
                    && !string.IsNullOrEmpty(run.WorkspacePath) && Directory.Exists(run.WorkspacePath))
                {
                    Directory.Delete(run.WorkspacePath, recursive: true);
                    _logger.Information("Cleaned up workspace {WorkspacePath} (step={Step})", run.WorkspacePath, run.CurrentStep);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to clean up workspace {WorkspacePath}", run.WorkspacePath);
            }

            // Drain in-flight serialized sends before disposing the semaphore.
            // SerializedSendAsync catches ObjectDisposedException, so tasks arriving after
            // disposal are handled gracefully without unobserved exceptions.
            try { await signalrLock.WaitAsync(CancellationToken.None); signalrLock.Release(); }
            catch { /* best-effort drain */ }
            signalrLock.Dispose();
        }
    }

    /// <summary>
    /// Masks known secret values in pipeline output. If no secrets are populated on the context
    /// (i.e., before <see cref="RunEnvironmentSetupStep"/> runs), the output passes through unchanged.
    /// Values shorter than 4 characters are not masked to avoid excessive false-positive redaction.
    /// </summary>
    private static string MaskSecretsInOutput(string output, PipelineStepContext? context)
    {
        if (context?.InjectedSecrets is not { Count: > 0 })
            return output;

        foreach (var (_, value) in context.InjectedSecrets)
        {
            if (value.Length >= 4)
                output = output.Replace(value, "***");
        }
        return output;
    }

    /// <summary>
    /// Reports a pipeline step transition with metadata. Updates run state, notifies the callback,
    /// and sends the transition to the orchestrator via SignalR. Failures are logged as warnings.
    /// </summary>
    internal async Task TransitionToInternalAsync(
        PipelineRun run, HubConnection connection, string jobId,
        Action<PipelineStep?>? onStepChanged, PipelineStep step, CancellationToken ct)
    {
        try
        {
            run.CurrentStep = step;
            if (step is not (PipelineStep.Failed or PipelineStep.Cancelled)
                && (int)step > (int)run.HighWaterMark)
                run.HighWaterMark = step;

            onStepChanged?.Invoke(step);

            var metadata = BuildStepMetadata(run, step);
            await connection.SendAsync(HubMethodNames.ReportStepTransition, jobId, step, DateTimeOffset.UtcNow, metadata, ct);
        }
        catch (Exception ex)
        {
            PipelineTelemetry.AgentSignalRFailures.Add(1);
            _logger.Warning(ex, "Failed to report step transition to {Step}", step);
        }
    }

    /// <summary>
    /// Enqueues an output line to the run and batches it for delivery.
    /// Failures are logged as warnings.
    /// </summary>
    internal async Task EmitOutputLineInternalAsync(
        PipelineRun run, OutputBatcher outputBatcher, string line, CancellationToken ct)
    {
        try
        {
            run.OutputLines.Enqueue(line);
            await outputBatcher.AddLineAsync(line, ct);
        }
        catch (Exception ex) { _logger.Warning(ex, "Failed to batch output line"); }
    }

    /// <summary>
    /// Reports a quality gate result to the orchestrator via SignalR.
    /// Failures are logged as warnings.
    /// </summary>
    internal async Task ReportQualityGateResultInternalAsync(
        HubConnection connection, string jobId, QualityGateReport report, CancellationToken ct)
    {
        try { await connection.SendAsync(HubMethodNames.ReportQualityGateResult, jobId, report, ct); }
        catch (Exception ex)
        {
            PipelineTelemetry.AgentSignalRFailures.Add(1);
            _logger.Warning(ex, "Failed to report quality gate result");
        }
    }

    /// <summary>
    /// Serializes a fire-and-forget SignalR send behind a semaphore to guarantee ordering.
    /// Catches <see cref="OperationCanceledException"/> and <see cref="ObjectDisposedException"/>
    /// from the semaphore wait since callers discard the task — these are expected during shutdown.
    /// </summary>
    internal static async Task SerializedSendAsync(SemaphoreSlim signalrLock, Func<Task> send, CancellationToken ct)
    {
        try
        {
            await signalrLock.WaitAsync(ct);
        }
        catch (ObjectDisposedException) { return; }
        catch (OperationCanceledException) { return; }

        try { await send(); }
        finally
        {
            try { signalrLock.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>
    /// Builds the common step prefix shared by all pipelines:
    /// Clone → EnsureGitignore → [CloneProjectRepositories] → WriteMcpConfig → WriteSteering.
    /// </summary>
    private static List<IPipelineStep> BuildCommonPrefix(JobAssignmentMessage job, bool includeProjectClone = false)
    {
        var steps = new List<IPipelineStep>
        {
            new CloneRepositoryStep(),
            new EnsureAgentGitignoreStep(),
        };
        if (includeProjectClone)
            steps.Add(new CloneProjectRepositoriesStep());
        steps.Add(new WriteMcpConfigStep(job));
        steps.Add(new WriteSteeringStep(job));
        return steps;
    }

    /// <summary>
    /// Builds the full step prefix (common prefix + RunEnvironmentSetup + SyncBrainPreRun).
    /// Used by agent and decomposition pipelines.
    /// </summary>
    private static List<IPipelineStep> BuildFullPrefix(JobAssignmentMessage job, bool includeProjectClone = false)
    {
        var steps = BuildCommonPrefix(job, includeProjectClone);
        steps.Add(new RunEnvironmentSetupStep(job));
        steps.Add(new SyncBrainPreRunStep());
        return steps;
    }

    /// <summary>
    /// Builds the ordered step pipeline for agent-side execution.
    /// Skips FetchIssueStep (issue data comes from job assignment) and adds MCP config step.
    /// </summary>
    internal static IReadOnlyList<IPipelineStep> BuildAgentStepPipeline(
        JobAssignmentMessage job, HubConnection connection)
    {
        var steps = BuildFullPrefix(job);
        steps.AddRange(PipelineStepFactory.CreateCoreImplementationSteps());
        return steps;
    }

    /// <summary>
    /// Builds the ordered step pipeline for PR review runs.
    /// Shorter sequence: Clone → WriteMcpConfig → WriteSteering → CreateBranch → SyncBrain → ExtractLinkedIssues → ReviewCode → PostFindings.
    /// Skips analysis, code generation, quality gates, and rework detection.
    /// </summary>
    internal static IReadOnlyList<IPipelineStep> BuildReviewStepPipeline(JobAssignmentMessage job)
    {
        var steps = BuildCommonPrefix(job);
        steps.AddRange([
            new CreateBranchStep(),
            new SyncBrainPreRunStep(),
            new ExtractLinkedIssuesStep(new IssueDescriptionParser()),
            new ReviewCodeStep(),
            new PostReviewFindingsStep()
        ]);
        return steps;
    }

    /// <summary>
    /// Builds the step pipeline for DecompositionAnalysis (Phase 1).
    /// Sequence: Clone → CloneProjectRepos → WriteMcpConfig → WriteSteering → RunEnvironmentSetup → SyncBrain → WriteProjectContext → WriteOpenIssueContext → DecompositionAnalysis → PostDecompositionPlan.
    /// IOpenIssueContextWriter is injected into the WriteOpenIssueContextStep via constructor.
    /// </summary>
    internal static IReadOnlyList<IPipelineStep> BuildDecompositionAnalysisStepPipeline(
        JobAssignmentMessage job,
        IOpenIssueContextWriter openIssueContextWriter)
    {
        var steps = BuildFullPrefix(job, includeProjectClone: true);
        steps.AddRange([
            new WriteProjectContextStep(),
            new WriteOpenIssueContextStep(openIssueContextWriter),
            new DecompositionAnalysisStep(),
            new PostDecompositionPlanStep()
        ]);
        return steps;
    }

    /// <summary>
    /// Builds the step pipeline for Decomposition (Phase 2).
    /// Sequence: Clone → CloneProjectRepos → WriteMcpConfig → WriteSteering → RunEnvironmentSetup → SyncBrain → WriteProjectContext → WriteOpenIssueContext → Decomposition → CreateSubIssues → PostDecompositionSummary.
    /// WriteProjectContextStep is included so the agent has cross-repo routing context
    /// when generating sub-issue JSON files with targetRepository values.
    /// WriteOpenIssueContextStep provides deduplication context for the agent.
    /// </summary>
    internal static IReadOnlyList<IPipelineStep> BuildDecompositionStepPipeline(
        JobAssignmentMessage job,
        IOpenIssueContextWriter openIssueContextWriter)
    {
        var steps = BuildFullPrefix(job, includeProjectClone: true);
        steps.AddRange([
            new WriteProjectContextStep(),
            new WriteOpenIssueContextStep(openIssueContextWriter),
            new DecompositionStep(),
            new CreateSubIssuesStep(),
            new PostDecompositionSummaryStep()
        ]);
        return steps;
    }

    private PipelineStepContext CreateStepContext(
        PipelineExecutionContext inputs,
        CancellationToken ct)
    {
        var callbacks = new AgentCallbacks(
            inputs.TransitionTo,
            inputs.EmitOutputLine,
            inputs.IssueOps,
            inputs.Run,
            inputs.PrOrchestrator,
            inputs.RepoProvider,
            inputs.ReportQualityGateResult,
            (r, report, isDraft, token) => CreatePullRequestAsync(r, report, isDraft, inputs.PrContext, token),
            async (contextLoaded, fileCount) =>
            {
                try { await inputs.Connection.InvokeAsync(HubMethodNames.ReportBrainSyncResult, inputs.Job.JobId, contextLoaded, fileCount, ct); }
                catch (Exception ex) { _logger.Warning(ex, "Failed to report brain sync result"); }
            });

        return PipelineStepContext.ForAgent(
            run: inputs.Run,
            config: inputs.Config,
            repoProvider: inputs.RepoProvider,
            agentProvider: inputs.AgentProvider,
            brainProvider: inputs.BrainProvider,
            pipelineProvider: inputs.PipelineProvider,
            cts: inputs.LocalCts,
            configStore: new NullConfigurationStore(),
            callbacks: callbacks,
            issueOps: inputs.IssueOps,
            agentExecution: inputs.AgentExecution,
            qualityGates: inputs.QualityGates,
            brainSync: inputs.BrainSync,
            prOrchestrator: inputs.PrOrchestrator,
            logger: _logger,
            qualityGateValidator: _qualityGateValidator,
            issue: inputs.Job.IssueDetail,
            parsedIssue: inputs.Job.ParsedIssue,
            issueComments: inputs.Job.IssueComments,
            preResolvedReviewerConfigs: inputs.Job.ReviewerConfigs,
            preResolvedQualityGateConfigs: inputs.Job.QualityGateConfigs,
            projectContext: inputs.Job.ProjectContext);
    }

    private async Task CreatePullRequestAsync(
        PipelineRun run, QualityGateReport report, bool isDraft,
        PullRequestCreationContext context, CancellationToken ct)
    {
        await _finalization.RunFullPrCreationAsync(
            run, report, isDraft,
            context.PrOrchestrator, context.RepoProvider, context.AgentProvider,
            context.BrainProvider, context.BrainSync, context.Config,
            context.Job.IssueDetail, context.Job.IssueComments,
            _feedbackService, _historyService,
            context.EmitOutputLine,
            step => ReportStepTransitionAsync(context.Connection, context.Job.JobId, run, step, ct),
            ct);
    }

    /// <summary>
    /// Reports a step transition to the orchestrator via SignalR, updating the run's current step.
    /// Failures are logged as warnings and do not propagate — step transitions are best-effort.
    /// </summary>
    private async Task ReportStepTransitionAsync(
        HubConnection connection, string jobId, PipelineRun run, PipelineStep step, CancellationToken ct)
    {
        run.CurrentStep = step;
        try
        {
            await connection.InvokeAsync(HubMethodNames.ReportStepTransition, jobId, step, DateTimeOffset.UtcNow, (Dictionary<string, string>?)null, ct);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to report step transition to {Step}", step);
        }
    }

    /// <summary>
    /// Builds metadata dictionary from the current run state to send with step transitions.
    /// Includes data from the just-completed step so the UI can display it in real-time.
    /// </summary>
    internal static Dictionary<string, string>? BuildStepMetadata(PipelineRun run, PipelineStep newStep)
    {
        // When transitioning TO a new step, the previous step just completed.
        // Include data that the previous step produced.
        Dictionary<string, string>? metadata = null;

        void Add(string key, string? value)
        {
            if (value is null) return;
            metadata ??= new Dictionary<string, string>();
            metadata[key] = value;
        }

        // CreatingBranch completed → include branch name
        if (newStep > PipelineStep.CreatingBranch && !string.IsNullOrEmpty(run.BranchName))
            Add("BranchName", run.BranchName);

        // VerifyingBaseline completed → include result
        if (newStep > PipelineStep.VerifyingBaseline && run.BaselineHealthPassed.HasValue)
            Add("BaselineHealthPassed", run.BaselineHealthPassed.Value.ToString());

        // AnalyzingCode completed → include skip status
        if (newStep > PipelineStep.AnalyzingCode && run.AnalysisSkipped)
            Add("AnalysisSkipped", "true");

        // GeneratingCode completed → include file change stats
        if (newStep > PipelineStep.GeneratingCode && run.FilesChangedCount > 0)
        {
            Add("FilesChangedCount", run.FilesChangedCount.ToString());
            Add("LinesAdded", run.LinesAdded.ToString());
            Add("LinesRemoved", run.LinesRemoved.ToString());
        }

        // ReviewingCode progress/completion
        if (newStep >= PipelineStep.ReviewingCode)
        {
            if (run.CodeReviewIterationsTotal > 0)
                Add("CodeReviewIterationsTotal", run.CodeReviewIterationsTotal.ToString());
            if (run.CodeReviewIterationsCompleted > 0)
                Add("CodeReviewIterationsCompleted", run.CodeReviewIterationsCompleted.ToString());
            if (run.CodeReviewIterationInProgress > 0)
                Add("CodeReviewIterationInProgress", run.CodeReviewIterationInProgress.ToString());
        }

        // Decomposition: open issues downloaded
        if (newStep > PipelineStep.DownloadingOpenIssues && run.OpenIssuesDownloaded > 0)
            Add("OpenIssuesDownloaded", run.OpenIssuesDownloaded.ToString());

        // Decomposition: sub-issue creation results
        if (newStep > PipelineStep.CreatingIssues && run.DecompositionSubIssuesAttempted > 0)
        {
            Add("DecompositionSubIssuesCreated", run.DecompositionSubIssuesCreated.ToString());
            Add("DecompositionSubIssuesAttempted", run.DecompositionSubIssuesAttempted.ToString());
        }

        // Quality gate retry count — report whenever retries have occurred
        if (run.RetryCount > 0)
            Add("RetryCount", run.RetryCount.ToString());
        if (run.InfrastructureRetryCount > 0)
            Add("InfrastructureRetryCount", run.InfrastructureRetryCount.ToString());

        // Token/cost accumulation — allows UI to show running totals
        if (run.TotalTokens > 0)
            Add("TotalTokens", run.TotalTokens.ToString());
        if (run.TotalCost is > 0m)
            Add("TotalCost", run.TotalCost.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        // Code review findings — populated during ReviewingCode step
        if (run.CodeReviewCriticalCount > 0)
            Add("CodeReviewCriticalCount", run.CodeReviewCriticalCount.ToString());
        if (run.CodeReviewWarningCount > 0)
            Add("CodeReviewWarningCount", run.CodeReviewWarningCount.ToString());
        if (run.CodeReviewSuggestionCount > 0)
            Add("CodeReviewSuggestionCount", run.CodeReviewSuggestionCount.ToString());
        if (run.CodeReviewAgentsRun.Count > 0)
            Add("CodeReviewAgentsRun", string.Join("\x1F", run.CodeReviewAgentsRun));

        return metadata;
    }

    internal static JobCompletionPayload BuildCompletionPayload(PipelineRun run) => BuildPayloadBase(run) with
    {
        FinalStep = run.CurrentStep,
        FailureReason = run.FailureReason,
        PullRequestUrl = run.PullRequestUrl,
        PullRequestNumber = run.PullRequestNumber,
        IsDraftPr = run.IsDraftPr,
        CompletedAt = run.CompletedAtOffset ?? DateTimeOffset.UtcNow,
        BrainUpdatesPushed = run.BrainUpdatesPushed,
        AnalysisRecommendation = run.AnalysisRecommendation
    };

    internal static JobCompletionPayload BuildFailurePayload(PipelineRun run, string reason) => BuildPayloadBase(run) with
    {
        FinalStep = PipelineStep.Failed,
        FailureReason = reason,
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

    /// <summary>
    /// Adapts the agent executor's callback methods to <see cref="IPipelineCallbacks"/>.
    /// Routes label swaps based on <see cref="PipelineRun.RunType"/>:
    /// Implementation runs swap labels on issues, Review runs swap labels on PRs.
    /// </summary>
    private sealed class AgentCallbacks(
        Action<PipelineStep> transitionTo,
        Action<string> emitOutputLine,
        OrchestratorProxy orchestratorProxy,
        PipelineRun run,
        PullRequestOrchestrator prOrchestrator,
        IRepositoryProvider repoProvider,
        Action<QualityGateReport> reportQualityGateResult,
        Func<PipelineRun, QualityGateReport, bool, CancellationToken, Task> createPullRequest,
        Func<bool, int, Task> reportBrainSyncResult) : IPipelineCallbacks
    {
        public void TransitionTo(PipelineStep step) => transitionTo(step);
        public void EmitOutputLine(string line) => emitOutputLine(line);
        public void NotifyChange() { }
        public void AddRunToHistory(PipelineRun run) { }
        public Task UpdateFileChangeStats(PipelineRun run)
            => prOrchestrator.UpdateFileChangeStatsAsync(run, repoProvider);
        public Task SwapAgentLabel(string issueIdentifier, string label, CancellationToken ct)
        {
            var targetKind = run.RunType == PipelineRunType.Review
                ? LabelTargetKind.PullRequest
                : LabelTargetKind.Issue;
            return orchestratorProxy.SwapLabelAsync(issueIdentifier, label, targetKind, ct);
        }
        public Task RemoveAllAgentLabels(string issueIdentifier, CancellationToken ct)
        {
            var targetKind = run.RunType == PipelineRunType.Review
                ? LabelTargetKind.PullRequest
                : LabelTargetKind.Issue;
            return orchestratorProxy.SwapLabelAsync(issueIdentifier, string.Empty, targetKind, ct);
        }
        public Task CreatePullRequest(PipelineRun run, QualityGateReport report, bool isDraft, CancellationToken ct)
        {
            reportQualityGateResult(report);
            return createPullRequest(run, report, isDraft, ct);
        }
        public async Task CreateDraftPrIfNotExists(PipelineRun run, CancellationToken ct)
        {
            try
            {
                await prOrchestrator.CreateDraftPrIfNotExistsAsync(run, repoProvider, ct);
                if (!string.IsNullOrEmpty(run.PullRequestNumber))
                    emitOutputLine($"📋 Draft PR #{run.PullRequestNumber} created");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Non-fatal on agent side as well
                Serilog.Log.Warning(ex, "Agent {RunId} failed to create draft PR, continuing", run.RunId);
            }
        }
        public Task FinalizePullRequest(PipelineRun run, QualityGateReport report, bool isDraft, CancellationToken ct)
        {
            reportQualityGateResult(report);
            return createPullRequest(run, report, isDraft, ct);
        }
        public Task ReportBrainSyncResult(bool contextLoaded, int knowledgeFileCount)
            => reportBrainSyncResult(contextLoaded, knowledgeFileCount);
    }
}
