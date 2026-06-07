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
    private readonly AgentIdentity _agentIdentity;
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

        using var activity = PipelineTelemetry.ActivitySource.StartActivity(
            "ExecutePipeline",
            ActivityKind.Consumer,
            PipelineTelemetry.ExtractTraceContext(job.TraceContext));
        activity?.SetTag("pipeline.run_id", job.JobId);
        activity?.SetTag("pipeline.issue", job.IssueIdentifier);
        activity?.SetTag("pipeline.agent_id", _agentIdentity.Id);

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
        List<(string TemplateName, IRepositoryProvider Provider)>? additionalRepoProviders = null;
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

            // Resolve additional repo providers for cross-repo decomposition
            if (job.ProjectContext is not null &&
                job.RunType is PipelineRunType.DecompositionAnalysis or PipelineRunType.Decomposition)
            {
                additionalRepoProviders = [];
                foreach (var repoTarget in job.ProjectContext.Repositories)
                {
                    // Skip the primary repo (already resolved as repoProvider) and repos without a provider ID
                    if (string.IsNullOrEmpty(repoTarget.RepoProviderId) ||
                        repoTarget.RepoProviderId == job.RepoProviderConfigId)
                        continue;

                    var additionalConfig = job.ProviderConfigs.FirstOrDefault(c => c.Id == repoTarget.RepoProviderId);
                    if (additionalConfig is null)
                    {
                        _logger.Warning("Additional repo provider config '{ProviderId}' for template '{Template}' not found in job assignment",
                            repoTarget.RepoProviderId, repoTarget.TemplateName);
                        continue;
                    }

                    try
                    {
                        var additionalProvider = providerFactory.CreateRepositoryProvider(additionalConfig);
                        additionalRepoProviders.Add((repoTarget.TemplateName, additionalProvider));
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.Warning(ex, "Failed to create repo provider for template '{Template}', skipping",
                            repoTarget.TemplateName);
                    }
                }
            }

            await repoProvider.ValidateAsync(ct);
            await agentProvider.ValidateAsync(ct);
            if (pipelineProvider is not null)
                await pipelineProvider.ValidateAsync(ct);

            // Merge provider-specific steering blacklist paths into config
            config = PipelineConfiguration.ApplyProviderBlacklist(config, agentProvider.SteeringBlacklistPaths);

            result = await ExecutePipelineStepsAsync(
                job, config, repoProvider, agentProvider, brainProvider, pipelineProvider,
                issueOps, connection, outputBatcher, onStepChanged, ct, additionalRepoProviders);

            activity?.SetTag("pipeline.final_step", result.FinalStep.ToString());
            // TODO: Distinguish Cancelled from Failed — graceful cancellation should set pipeline.cancelled=true tag
            // and leave status as Unset instead of setting Error status (per amended OTel conventions).
            if (result.FinalStep != PipelineStep.Completed)
                activity?.SetStatus(ActivityStatusCode.Error, result.FinalStep.ToString());
            return result;
        }
        catch (Exception ex)
        {
            activity?.RecordError(ex, ct);
            throw;
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

            // Dispose additional repo providers used for cross-repo decomposition cloning
            if (additionalRepoProviders is not null)
            {
                foreach (var (_, provider) in additionalRepoProviders)
                {
                    if (provider is IAsyncDisposable ard) await ard.DisposeAsync();
                }
            }
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
        CancellationToken ct,
        List<(string TemplateName, IRepositoryProvider Provider)>? additionalRepoProviders = null)
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
            AgentId = _agentIdentity.Id,
            RunType = job.RunType,
            ReviewPrBranchName = job.LinkedPullRequest?.BranchName,
            ReviewPrTargetBranch = job.ReviewPrTargetBranch,
            ReviewPrDescription = job.ReviewPrDescription,
            ReviewPrAuthor = job.ReviewPrAuthor,
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
            new WriteSteeringStep(job),
            new RunEnvironmentSetupStep(job),
            new SyncBrainPreRunStep(),
            new DetectReworkStep(),
            new WritePrConversationContextStep(),
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
            new WriteSteeringStep(job),
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
            new CloneProjectRepositoriesStep(),
            new WriteSteeringStep(job),
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
    /// Sequence: Clone → SyncBrain → WriteProjectContext → WriteOpenIssueContext → Decomposition → CreateSubIssues → PostDecompositionSummary.
    /// WriteProjectContextStep is included so the agent has cross-repo routing context
    /// when generating sub-issue JSON files with targetRepository values.
    /// WriteOpenIssueContextStep provides deduplication context for the agent.
    /// </summary>
    internal static IReadOnlyList<IPipelineStep> BuildDecompositionStepPipeline(
        JobAssignmentMessage job,
        IOpenIssueContextWriter openIssueContextWriter)
    {
        return new IPipelineStep[]
        {
            new CloneRepositoryStep(),
            new CloneProjectRepositoriesStep(),
            new WriteSteeringStep(job),
            new RunEnvironmentSetupStep(job),
            new SyncBrainPreRunStep(),
            new WriteProjectContextStep(),
            new WriteOpenIssueContextStep(openIssueContextWriter),
            new DecompositionStep(),
            new CreateSubIssuesStep(),
            new PostDecompositionSummaryStep()
        };
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
                try { await inputs.Connection.InvokeAsync("ReportBrainSyncResult", inputs.Job.JobId, contextLoaded, fileCount, ct); }
                catch (Exception ex) { _logger.Warning(ex, "Failed to report brain sync result"); }
            });

        return new PipelineStepContext
        {
            Run = inputs.Run,
            Config = inputs.Config,
            RepoProvider = inputs.RepoProvider,
            AgentProvider = inputs.AgentProvider,
            BrainProvider = inputs.BrainProvider,
            PipelineProvider = inputs.PipelineProvider,
            Cts = inputs.LocalCts,
            ConfigStore = new NullConfigurationStore(),
            Callbacks = callbacks,
            IssueOps = inputs.IssueOps,
            AgentExecution = inputs.AgentExecution,
            QualityGates = inputs.QualityGates,
            BrainSync = inputs.BrainSync,
            PrOrchestrator = inputs.PrOrchestrator,
            PreResolvedReviewerConfigs = inputs.Job.ReviewerConfigs,
            PreResolvedQualityGateConfigs = inputs.Job.QualityGateConfigs,
            Logger = _logger,
            QualityGateValidator = _qualityGateValidator,
            ProjectContext = inputs.Job.ProjectContext,
            // Pre-populate issue data from job (no IssueProvider on agent side)
            Issue = inputs.Job.IssueDetail,
            ParsedIssue = inputs.Job.ParsedIssue,
            IssueComments = inputs.Job.IssueComments
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

        try
        {
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
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
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
    /// Bundles the parameters needed by <see cref="CreateStepContext"/> into a single object,
    /// reducing the method's parameter count from 19 to 1 (plus CancellationToken).
    /// </summary>
    internal sealed record PipelineExecutionContext
    {
        public required JobAssignmentMessage Job { get; init; }
        public required PipelineRun Run { get; init; }
        public required PipelineConfiguration Config { get; init; }
        public required IRepositoryProvider RepoProvider { get; init; }
        public required IAgentProvider AgentProvider { get; init; }
        public IRepositoryProvider? BrainProvider { get; init; }
        public BrainSyncService? BrainSync { get; init; }
        public IPipelineProvider? PipelineProvider { get; init; }
        public required OrchestratorProxy IssueOps { get; init; }
        public required HubConnection Connection { get; init; }
        public required PullRequestOrchestrator PrOrchestrator { get; init; }
        public required AgentPhaseExecutor AgentExecution { get; init; }
        public required QualityGateExecutor QualityGates { get; init; }
        public required CancellationTokenSource LocalCts { get; init; }
        public required PullRequestCreationContext PrContext { get; init; }
        public required Action<PipelineStep> TransitionTo { get; init; }
        public required Action<string> EmitOutputLine { get; init; }
        public required Action<QualityGateReport> ReportQualityGateResult { get; init; }
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
