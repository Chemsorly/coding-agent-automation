using System.Diagnostics;
using System.Diagnostics.Metrics;
using CodingAgentWebUI.Agent.KiroCli;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using CodingAgentWebUI.Pipeline.Telemetry;
using KiroCliLib.Core;
using Microsoft.AspNetCore.SignalR.Client;
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
public sealed class LocalPipelineExecutor
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
    private readonly Serilog.ILogger _logger;

    public LocalPipelineExecutor(
        IKiroCliOrchestrator orchestrator,
        IHttpClientFactory httpClientFactory,
        PipelineConfiguration defaultPipelineConfig,
        IQualityGateValidator qualityGateValidator,
        Serilog.ILogger logger,
        IBrainUpdateService? brainUpdateService = null,
        IPipelineRunHistoryService? historyService = null,
        IOpenIssueContextWriter? openIssueContextWriter = null)
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

        using var activity = PipelineTelemetry.ActivitySource.StartActivity("ExecutePipeline");
        activity?.SetTag("pipeline.run_id", job.JobId);
        activity?.SetTag("pipeline.issue", job.IssueIdentifier);
        activity?.SetTag("pipeline.agent_id", Environment.MachineName);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var tags = PipelineTelemetry.BuildTags(job.RunType, job.ProjectId, job.ProjectName);
        PipelineTelemetry.SetProjectTags(activity, job.ProjectId, job.ProjectName);
        PipelineTelemetry.JobsDispatched.Add(1, tags);

        var config = job.PipelineConfiguration;
        var issueOps = new OrchestratorProxy(connection, job.JobId);

        // Construct a per-job provider factory with the OrchestratorProxy for token refresh
        // TODO: Factory captures config before blacklist override below. Move construction after
        // the override block if AgentProviderFactory ever needs blacklist settings.
        var providerFactory = new AgentProviderFactory(_orchestrator, _httpClientFactory, config, issueOps);

        // Resolve provider configs from the job assignment
        var repoConfig = job.ProviderConfigs.FirstOrDefault(c => c.Id == job.RepoProviderConfigId)
            ?? throw new InvalidOperationException($"Repository provider config '{job.RepoProviderConfigId}' not found in job assignment");
        var agentConfig = job.ProviderConfigs.FirstOrDefault(c => c.Id == job.AgentProviderConfigId)
            ?? throw new InvalidOperationException($"Agent provider config '{job.AgentProviderConfigId}' not found in job assignment");

        // Override blacklist settings from repo provider config (per-repo takes precedence)
        config = PipelineConfiguration.ApplyBlacklistOverride(config, repoConfig);

        IRepositoryProvider? repoProvider = null;
        IAgentProvider? agentProvider = null;
        IRepositoryProvider? brainProvider = null;
        IPipelineProvider? pipelineProvider = null;
        JobCompletionPayload? result = null;

        try
        {
            repoProvider = providerFactory.CreateRepositoryProvider(repoConfig);
            agentProvider = providerFactory.CreateAgentProvider(agentConfig);

            if (!string.IsNullOrEmpty(job.BrainProviderConfigId))
            {
                var brainConfig = job.ProviderConfigs.FirstOrDefault(c => c.Id == job.BrainProviderConfigId);
                if (brainConfig is not null)
                {
                    try
                    {
                        brainProvider = providerFactory.CreateRepositoryProvider(brainConfig);
                        await brainProvider.ValidateAsync(ct);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.Warning(ex, "Brain provider validation failed, disabling brain sync");
                        if (brainProvider is IAsyncDisposable bd) await bd.DisposeAsync();
                        brainProvider = null;
                    }
                }
            }

            if (!string.IsNullOrEmpty(job.PipelineProviderConfigId))
            {
                var pipelineConfig = job.ProviderConfigs.FirstOrDefault(c => c.Id == job.PipelineProviderConfigId);
                if (pipelineConfig is not null)
                    pipelineProvider = providerFactory.CreatePipelineProvider(pipelineConfig);
            }

            await repoProvider.ValidateAsync(ct);
            await agentProvider.ValidateAsync(ct);
            if (pipelineProvider is not null)
                await pipelineProvider.ValidateAsync(ct);

            result = await ExecutePipelineStepsAsync(
                job, config, repoProvider, agentProvider, brainProvider, pipelineProvider,
                issueOps, connection, outputBatcher, onStepChanged, ct);

            activity?.SetTag("pipeline.final_step", result.FinalStep.ToString());
            return result;
        }
        finally
        {
            sw.Stop();
            PipelineTelemetry.JobDuration.Record(sw.Elapsed.TotalSeconds, tags);
            if (job.RunType is PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition)
            {
                var phase = job.RunType == PipelineRunType.DecompositionAnalysis ? "analysis" : "creation";
                PipelineTelemetry.DecompositionDuration.Record(sw.Elapsed.TotalSeconds,
                    PipelineTelemetry.ProjectIdTag(job.ProjectId),
                    PipelineTelemetry.ProjectNameTag(job.ProjectName),
                    new KeyValuePair<string, object?>("phase", phase));
            }
            if (result is null || result.FinalStep != PipelineStep.Completed)
                PipelineTelemetry.JobsFailed.Add(1, tags);
            else
                PipelineTelemetry.JobsCompleted.Add(1, tags);

            if (repoProvider is IAsyncDisposable rd) await rd.DisposeAsync();
            if (agentProvider is IAsyncDisposable ad) await ad.DisposeAsync();
            if (brainProvider is IAsyncDisposable brd) await brd.DisposeAsync();
            if (pipelineProvider is IAsyncDisposable pd) await pd.DisposeAsync();
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
        HubConnection connection,
        OutputBatcher outputBatcher,
        Action<PipelineStep?>? onStepChanged,
        CancellationToken ct)
    {
        var run = new PipelineRun
        {
            RunId = job.JobId,
            IssueIdentifier = job.IssueIdentifier,
            IssueTitle = job.IssueDetail.Title,
            IssueProviderConfigId = string.Empty, // Agent doesn't have issue provider
            RepoProviderConfigId = job.RepoProviderConfigId,
            StartedAt = DateTime.UtcNow,
            CurrentStep = PipelineStep.Created,
            RepositoryName = repoProvider.RepositoryFullName,
            ModelName = agentProvider is KiroCliAgentProvider kp ? kp.Model : null,
            BrainProviderConfigId = brainProvider is not null ? job.BrainProviderConfigId : null,
            PipelineProviderConfigId = job.PipelineProviderConfigId,
            InitiatedBy = job.InitiatedBy,
            LinkedPullRequest = job.LinkedPullRequest,
            AgentId = Environment.MachineName,
            RunType = job.RunType,
            ReviewPrBranchName = job.LinkedPullRequest?.BranchName,
            ReviewPrTargetBranch = job.ReviewPrTargetBranch,
            ReviewPrDescription = job.ReviewPrDescription,
            LinkedIssueContexts = job.LinkedIssueContexts
        };

        run.IssueLabels = job.IssueDetail.Labels;

        // Orchestrators
        var agentExecution = new AgentPhaseExecutor(_logger);
        var prOrchestrator = new PullRequestOrchestrator(_logger);
        var qualityGates = new QualityGateExecutor(_qualityGateValidator, prOrchestrator, _logger, _historyService);
        BrainSyncService? brainSync = _brainUpdateService is not null
            ? new BrainSyncService(_brainUpdateService, _logger)
            : null;

        // Fire-and-forget wrappers — delegate to class-level async methods for testability.
        void TransitionTo(PipelineStep step) => _ = TransitionToInternalAsync(run, connection, job.JobId, onStepChanged, step, ct);
        void ReportQualityGateResult(QualityGateReport report) => _ = ReportQualityGateResultInternalAsync(connection, job.JobId, report, ct);

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
            context = CreateStepContext(
                job, run, config, repoProvider, agentProvider, brainProvider, brainSync,
                pipelineProvider, issueOps, connection, prOrchestrator, agentExecution,
                qualityGates, localCts, prContext, TransitionTo, EmitOutputLine, ReportQualityGateResult, ct);

            // Build step pipeline based on run type
            var steps = run.RunType switch
            {
                PipelineRunType.Review => BuildReviewStepPipeline(job),
                PipelineRunType.DecompositionAnalysis => BuildDecompositionAnalysisStepPipeline(job, _openIssueContextWriter),
                PipelineRunType.Decomposition => BuildDecompositionStepPipeline(job),
                _ => BuildAgentStepPipeline(job, connection)
            };

            await PipelineStepRunner.ExecuteAsync(steps, context, linkedCt);

            // For review/decomposition runs, the step pipeline ends at PostingFindings/PostPlan/PostSummary.
            // Transition to Completed here (implementation runs do this in CreatePullRequestAsync).
            if (run.RunType is PipelineRunType.Review or PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition
                && run.CurrentStep is not PipelineStep.Failed and not PipelineStep.Cancelled)
            {
                run.CompletedAt = DateTime.UtcNow;
                run.CurrentStep = PipelineStep.Completed;
                run.FinalLabel ??= AgentLabels.Done;
            }

            return BuildCompletionPayload(run);
        }
        catch (OperationCanceledException)
        {
            run.CompletedAt = DateTime.UtcNow;

            TransitionTo(PipelineStep.Cancelled);
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
                if (run.CurrentStep == PipelineStep.Completed
                    && !string.IsNullOrEmpty(run.WorkspacePath) && Directory.Exists(run.WorkspacePath))
                {
                    Directory.Delete(run.WorkspacePath, recursive: true);
                    _logger.Information("Cleaned up successful workspace {WorkspacePath}", run.WorkspacePath);
                }
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to clean up workspace {WorkspacePath}", run.WorkspacePath);
            }
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
            await connection.InvokeAsync("ReportStepTransition", jobId, step, DateTimeOffset.UtcNow, metadata, ct);
        }
        catch (Exception ex) { _logger.Warning(ex, "Failed to report step transition to {Step}", step); }
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
        try { await connection.InvokeAsync("ReportQualityGateResult", jobId, report, ct); }
        catch (Exception ex) { _logger.Warning(ex, "Failed to report quality gate result"); }
    }

    /// <summary>
    /// Builds the ordered step pipeline for agent-side execution.
    /// Skips FetchIssueStep (issue data comes from job assignment) and adds MCP config step.
    /// </summary>
    internal static IReadOnlyList<IPipelineStep> BuildAgentStepPipeline(
        JobAssignmentMessage job, HubConnection connection)
    {
        var steps = new List<IPipelineStep>
        {
            new CloneRepositoryStep(),
            new WriteMcpConfigStep(job),
            new RunEnvironmentSetupStep(job),
            new SyncBrainPreRunStep(),
            new DetectReworkStep(),
            new CreateBranchStep(),
            new VerifyBaselineStep(),
            new AnalyzeCodeStep(),
            new GenerateCodeStep(),
            new BrainPullBeforeWriteStep(),
            new ReviewCodeStep(),
            new RunQualityGatesStep()
        };
        return steps;
    }

    /// <summary>
    /// Builds the ordered step pipeline for PR review runs.
    /// Shorter sequence: Clone → EnvironmentSetup → CreateBranch → SyncBrain → ExtractLinkedIssues → ReviewCode → PostFindings.
    /// Skips analysis, code generation, quality gates, and rework detection.
    /// </summary>
    internal static IReadOnlyList<IPipelineStep> BuildReviewStepPipeline(JobAssignmentMessage job)
    {
        return new IPipelineStep[]
        {
            new CloneRepositoryStep(),
            new RunEnvironmentSetupStep(job),
            new CreateBranchStep(),
            new SyncBrainPreRunStep(),
            new ExtractLinkedIssuesStep(new IssueDescriptionParser()),
            new ReviewCodeStep(),
            new PostReviewFindingsStep()
        };
    }

    /// <summary>
    /// Builds the step pipeline for DecompositionAnalysis (Phase 1).
    /// Sequence: Clone → SyncBrain → WriteOpenIssueContext → DecompositionAnalysis → PostDecompositionPlan.
    /// IOpenIssueContextWriter is injected into the WriteOpenIssueContextStep via constructor.
    /// </summary>
    internal static IReadOnlyList<IPipelineStep> BuildDecompositionAnalysisStepPipeline(
        JobAssignmentMessage job,
        IOpenIssueContextWriter openIssueContextWriter)
    {
        return new IPipelineStep[]
        {
            new CloneRepositoryStep(),
            new RunEnvironmentSetupStep(job),
            new SyncBrainPreRunStep(),
            new WriteProjectContextStep(),
            new WriteOpenIssueContextStep(openIssueContextWriter),
            new DecompositionAnalysisStep(),
            new PostDecompositionPlanStep()
        };
    }

    /// <summary>
    /// Builds the step pipeline for Decomposition (Phase 2).
    /// Sequence: Clone → SyncBrain → Decomposition → CreateSubIssues → PostDecompositionSummary.
    /// </summary>
    internal static IReadOnlyList<IPipelineStep> BuildDecompositionStepPipeline(JobAssignmentMessage job)
    {
        return new IPipelineStep[]
        {
            new CloneRepositoryStep(),
            new RunEnvironmentSetupStep(job),
            new SyncBrainPreRunStep(),
            new DecompositionStep(),
            new CreateSubIssuesStep(),
            new PostDecompositionSummaryStep()
        };
    }

    private PipelineStepContext CreateStepContext(
        JobAssignmentMessage job,
        PipelineRun run,
        PipelineConfiguration config,
        IRepositoryProvider repoProvider,
        IAgentProvider agentProvider,
        IRepositoryProvider? brainProvider,
        BrainSyncService? brainSync,
        IPipelineProvider? pipelineProvider,
        OrchestratorProxy issueOps,
        HubConnection connection,
        PullRequestOrchestrator prOrchestrator,
        AgentPhaseExecutor agentExecution,
        QualityGateExecutor qualityGates,
        CancellationTokenSource localCts,
        PullRequestCreationContext prContext,
        Action<PipelineStep> transitionTo,
        Action<string> emitOutputLine,
        Action<QualityGateReport> reportQualityGateResult,
        CancellationToken ct)
    {
        var callbacks = new AgentCallbacks(
            transitionTo,
            emitOutputLine,
            issueOps,
            run,
            prOrchestrator,
            repoProvider,
            reportQualityGateResult,
            (r, report, isDraft, token) => CreatePullRequestAsync(r, report, isDraft, prContext, token),
            async (contextLoaded, fileCount) =>
            {
                try { await connection.InvokeAsync("ReportBrainSyncResult", job.JobId, contextLoaded, fileCount, ct); }
                catch (Exception ex) { _logger.Warning(ex, "Failed to report brain sync result"); }
            });

        return new PipelineStepContext
        {
            Run = run,
            Config = config,
            RepoProvider = repoProvider,
            AgentProvider = agentProvider,
            BrainProvider = brainProvider,
            PipelineProvider = pipelineProvider,
            Cts = localCts,
            ConfigStore = new NullConfigurationStore(),
            Callbacks = callbacks,
            IssueOps = issueOps,
            AgentExecution = agentExecution,
            QualityGates = qualityGates,
            BrainSync = brainSync,
            PrOrchestrator = prOrchestrator,
            PreResolvedReviewerConfigs = job.ReviewerConfigs,
            PreResolvedQualityGateConfigs = job.QualityGateConfigs,
            Logger = _logger,
            QualityGateValidator = _qualityGateValidator,
            ProjectContext = job.ProjectContext,
            // Pre-populate issue data from job (no IssueProvider on agent side)
            Issue = job.IssueDetail,
            ParsedIssue = job.ParsedIssue,
            IssueComments = job.IssueComments
        };
    }

    private async Task CreatePullRequestAsync(
        PipelineRun run, QualityGateReport report, bool isDraft,
        PullRequestCreationContext context, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("CreatePullRequest");
        activity?.SetTag("pipeline.run_id", run.RunId);
        activity?.SetTag("pipeline.issue", run.IssueIdentifier);
        activity?.SetTag("pipeline.pr.is_draft", isDraft);
        PipelineTelemetry.SetProjectTags(activity, run.ProjectId, run.ProjectName);

        // NOTE: QualityGateExecutor already transitions to PreparingForPullRequest
        // during its cleanup phase, so we skip that transition here to avoid duplicates.

        await ReportStepTransitionAsync(context.Connection, context.Job.JobId, run, PipelineStep.CreatingPullRequest, ct);

        if (run.LinkedPullRequest is not null)
        {
            run.PullRequestUrl = run.LinkedPullRequest.Url;
            run.PullRequestNumber = run.LinkedPullRequest.Number.ToString();
        }

        var prUrl = await context.PrOrchestrator.CreatePullRequestAsync(
            run, report, isDraft, context.RepoProvider, context.Job.IssueDetail, context.Job.IssueComments, context.Config, ct,
            context.EmitOutputLine, isRework: run.LinkedPullRequest is not null);

        if (prUrl is null && context.Config.BlacklistMode == BlacklistMode.Fail && run.BlacklistedFilesDetected.Count > 0)
        {
            run.FailureReason = $"Blacklisted files detected: {string.Join(", ", run.BlacklistedFilesDetected)}";
            run.CompletedAt = DateTime.UtcNow;
            run.CurrentStep = PipelineStep.Failed;
            return;
        }

        if (prUrl is null)
        {
            run.FailureReason = "Agent did not produce any changes. No commits ahead of base branch.";
            run.CompletedAt = DateTime.UtcNow;
            run.CurrentStep = PipelineStep.Failed;
            return;
        }

        var finalStep = isDraft ? PipelineStep.Failed : PipelineStep.Completed;
        if (isDraft)
        {
            run.FailureReason = "Quality gates failed after max retries; draft PR created.";
        }
        // Label swap (agent:done / agent:error) is handled by the orchestrator in ReportJobCompleted.

        // ── Reflection + brain post-run sync ──
        if (!isDraft && context.BrainProvider is not null && context.BrainSync is not null && !context.Config.BrainReadOnly)
        {
            await ReportStepTransitionAsync(context.Connection, context.Job.JobId, run, PipelineStep.ReflectingOnRun, ct);

            await _finalization.RunReflectionAsync(run, context.AgentProvider, context.Config, context.EmitOutputLine, ct);

            await ReportStepTransitionAsync(context.Connection, context.Job.JobId, run, PipelineStep.SyncingBrainRepoPostRun, ct);

            await _finalization.SyncBrainPostRunAsync(run, context.BrainSync, context.BrainProvider, context.Config, context.EmitOutputLine, ct);
        }

        // ── Feedback collection: separate agent call, runs regardless of brain provider ──
        if (!isDraft)
        {
            await _finalization.CollectFeedbackAsync(run, context.AgentProvider, _feedbackService, _historyService, context.EmitOutputLine, ct);
        }

        run.CompletedAt = DateTime.UtcNow;
        run.CurrentStep = finalStep;
        run.FinalLabel = isDraft ? AgentLabels.Error : AgentLabels.Done;
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
            await connection.InvokeAsync("ReportStepTransition", jobId, step, DateTimeOffset.UtcNow, (Dictionary<string, string>?)null, ct);
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

        return metadata;
    }

    internal static JobCompletionPayload BuildCompletionPayload(PipelineRun run) => BuildPayloadBase(run) with
    {
        FinalStep = run.CurrentStep,
        FailureReason = run.FailureReason,
        PullRequestUrl = run.PullRequestUrl,
        PullRequestNumber = run.PullRequestNumber,
        IsDraftPr = run.IsDraftPr,
        CompletedAt = run.CompletedAt.HasValue ? new DateTimeOffset(run.CompletedAt.Value, TimeSpan.Zero) : DateTimeOffset.UtcNow,
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
    /// Writes the MCP server configuration to the workspace at the path specified by
    /// the agent provider's mcpConfigPath setting (defaults to .agent/settings/mcp.json for Kiro CLI).
    /// Delegates to <see cref="McpConfigWriter.WriteConfig"/> for the shared implementation.
    /// </summary>
    internal static void WriteMcpConfigToWorkspace(string workspacePath, IReadOnlyList<McpServerConfig> mcpServers, string mcpConfigRelativePath)
    {
        var fullPath = Path.Combine(workspacePath, mcpConfigRelativePath);
        McpConfigWriter.WriteConfig(fullPath, mcpServers);
    }

    /// <summary>
    /// Bundles the parameters needed by <see cref="CreatePullRequestAsync"/> into a single object,
    /// reducing the method's parameter count from 14 to 5.
    /// </summary>
    internal sealed record PullRequestCreationContext
    {
        public required IRepositoryProvider RepoProvider { get; init; }
        public required IAgentProvider AgentProvider { get; init; }
        public IRepositoryProvider? BrainProvider { get; init; }
        public BrainSyncService? BrainSync { get; init; }
        public required PipelineConfiguration Config { get; init; }
        public required OrchestratorProxy IssueOps { get; init; }
        public required HubConnection Connection { get; init; }
        public required JobAssignmentMessage Job { get; init; }
        public required PullRequestOrchestrator PrOrchestrator { get; init; }
        public required Action<string> EmitOutputLine { get; init; }
    }

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
