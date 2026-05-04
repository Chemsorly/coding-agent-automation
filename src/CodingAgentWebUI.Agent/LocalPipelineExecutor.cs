using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
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
    private readonly PipelineConfiguration _defaultPipelineConfig;
    private readonly IQualityGateValidator _qualityGateValidator;
    private readonly IBrainUpdateService? _brainUpdateService;
    private readonly Serilog.ILogger _logger;

    public LocalPipelineExecutor(
        IKiroCliOrchestrator orchestrator,
        PipelineConfiguration defaultPipelineConfig,
        IQualityGateValidator qualityGateValidator,
        Serilog.ILogger logger,
        IBrainUpdateService? brainUpdateService = null)
    {
        ArgumentNullException.ThrowIfNull(orchestrator);
        ArgumentNullException.ThrowIfNull(defaultPipelineConfig);
        ArgumentNullException.ThrowIfNull(qualityGateValidator);
        ArgumentNullException.ThrowIfNull(logger);

        _orchestrator = orchestrator;
        _defaultPipelineConfig = defaultPipelineConfig;
        _qualityGateValidator = qualityGateValidator;
        _brainUpdateService = brainUpdateService;
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

        var config = job.PipelineConfiguration;
        var issueOps = new OrchestratorProxy(connection, job.JobId);

        // Construct a per-job provider factory with the OrchestratorProxy for token refresh
        var providerFactory = new AgentProviderFactory(_orchestrator, config, issueOps);

        // Resolve provider configs from the job assignment
        var repoConfig = job.ProviderConfigs.FirstOrDefault(c => c.Id == job.RepoProviderConfigId)
            ?? throw new InvalidOperationException($"Repository provider config '{job.RepoProviderConfigId}' not found in job assignment");
        var agentConfig = job.ProviderConfigs.FirstOrDefault(c => c.Id == job.AgentProviderConfigId)
            ?? throw new InvalidOperationException($"Agent provider config '{job.AgentProviderConfigId}' not found in job assignment");

        IRepositoryProvider? repoProvider = null;
        IAgentProvider? agentProvider = null;
        IRepositoryProvider? brainProvider = null;
        IPipelineProvider? pipelineProvider = null;

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

            if (config.ExternalCiEnabled && !string.IsNullOrEmpty(job.PipelineProviderConfigId))
            {
                var pipelineConfig = job.ProviderConfigs.FirstOrDefault(c => c.Id == job.PipelineProviderConfigId);
                if (pipelineConfig is not null)
                    pipelineProvider = providerFactory.CreatePipelineProvider(pipelineConfig);
            }

            await repoProvider.ValidateAsync(ct);
            await agentProvider.ValidateAsync(ct);
            if (pipelineProvider is not null)
                await pipelineProvider.ValidateAsync(ct);

            return await ExecutePipelineStepsAsync(
                job, config, repoProvider, agentProvider, brainProvider, pipelineProvider,
                issueOps, connection, outputBatcher, onStepChanged, ct);
        }
        finally
        {
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
            AgentId = Environment.MachineName
        };

        run.IssueLabels = job.IssueDetail.Labels;

        // Orchestrators
        var agentExecution = new AgentExecutionOrchestrator(_logger);
        var prOrchestrator = new PullRequestOrchestrator(_logger);
        var qualityGates = new QualityGateOrchestrator(_qualityGateValidator, prOrchestrator, _logger);
        BrainSyncOrchestrator? brainSync = _brainUpdateService is not null
            ? new BrainSyncOrchestrator(_brainUpdateService, _logger)
            : null;

        // Local helpers for reporting
        async void TransitionTo(PipelineStep step)
        {
            try
            {
                run.CurrentStep = step;
                if (step is not (PipelineStep.Failed or PipelineStep.Cancelled)
                    && (int)step > (int)run.HighWaterMark)
                    run.HighWaterMark = step;

                onStepChanged?.Invoke(step);

                await connection.InvokeAsync("ReportStepTransition", job.JobId, step, DateTimeOffset.UtcNow, ct);
            }
            catch (Exception ex) { _logger.Warning(ex, "Failed to report step transition to {Step}", step); }
        }

        async void EmitOutputLine(string line)
        {
            try
            {
                run.OutputLines.Enqueue(line);
                await outputBatcher.AddLineAsync(line, ct);
            }
            catch (Exception ex) { _logger.Warning(ex, "Failed to batch output line"); }
        }

        async void ReportQualityGateResult(QualityGateReport report)
        {
            try { await connection.InvokeAsync("ReportQualityGateResult", job.JobId, report, ct); }
            catch (Exception ex) { _logger.Warning(ex, "Failed to report quality gate result"); }
        }

        CancellationTokenSource? localCts = null;

        try
        {
            localCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var linkedCt = localCts.Token;

            // Build step context
            var callbacks = new AgentCallbacks(
                step => TransitionTo(step),
                EmitOutputLine,
                issueOps,
                prOrchestrator,
                repoProvider,
                report => ReportQualityGateResult(report),
                (r, report, isDraft, token) => CreatePullRequestAsync(r, report, isDraft, repoProvider, agentProvider,
                    brainProvider, brainSync, config, issueOps, connection, job, EmitOutputLine, token));

            var context = new PipelineStepContext
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
                // Pre-populate issue data from job (no IssueProvider on agent side)
                Issue = job.IssueDetail,
                ParsedIssue = job.ParsedIssue,
                IssueComments = job.IssueComments
            };

            // Build step pipeline (agent-specific: includes MCP config, skips FetchIssueStep)
            var steps = BuildAgentStepPipeline(job, connection);

            await PipelineStepRunner.ExecuteAsync(steps, context, linkedCt);

            // Report brain sync result after brain step
            if (brainProvider is not null && brainSync is not null)
            {
                try { await connection.InvokeAsync("ReportBrainSyncResult", job.JobId, run.BrainContextLoaded, run.BrainKnowledgeFileCount, linkedCt); }
                catch (Exception ex) { _logger.Warning(ex, "Failed to report brain sync result"); }
            }

            return BuildCompletionPayload(run);
        }
        catch (OperationCanceledException)
        {
            run.CompletedAt = DateTime.UtcNow;

            // Set agent:cancelled label (matching monolith behavior)
            try { await issueOps.SwapLabelAsync(run.IssueIdentifier, AgentLabels.Cancelled, CancellationToken.None); }
            catch (Exception labelEx) { _logger.Warning(labelEx, "Failed to set cancelled label"); }

            TransitionTo(PipelineStep.Cancelled);
            EmitOutputLine("🚫 Pipeline cancelled");

            return new JobCompletionPayload
            {
                FinalStep = PipelineStep.Cancelled,
                CompletedAt = DateTimeOffset.UtcNow,
                RetryCount = run.RetryCount,
                IsRework = run.LinkedPullRequest is not null
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
    /// Builds the ordered step pipeline for agent-side execution.
    /// Skips FetchIssueStep (issue data comes from job assignment) and adds MCP config step.
    /// </summary>
    private static IReadOnlyList<IPipelineStep> BuildAgentStepPipeline(
        JobAssignmentMessage job, HubConnection connection)
    {
        var steps = new List<IPipelineStep>
        {
            new CloneRepositoryStep(),
            new WriteMcpConfigStep(job),
            new SyncBrainPreRunStep(),
            new DetectReworkStep(),
            new CreateBranchStep(),
            new AnalyzeCodeStep(),
            new GenerateCodeStep(),
            new BrainPullBeforeWriteStep(),
            new ReviewCodeStep(),
            new RunQualityGatesStep()
        };
        return steps;
    }

    private async Task CreatePullRequestAsync(
        PipelineRun run, QualityGateReport report, bool isDraft,
        IRepositoryProvider repoProvider, IAgentProvider agentProvider,
        IRepositoryProvider? brainProvider, BrainSyncOrchestrator? brainSync,
        PipelineConfiguration config, OrchestratorProxy issueOps,
        HubConnection connection, JobAssignmentMessage job,
        Action<string> emitOutputLine, CancellationToken ct)
    {
        var prOrchestrator = new PullRequestOrchestrator(_logger);

        // NOTE: QualityGateOrchestrator already transitions to PreparingForPullRequest
        // during its cleanup phase, so we skip that transition here to avoid duplicates.

        run.CurrentStep = PipelineStep.CreatingPullRequest;
        try { await connection.InvokeAsync("ReportStepTransition", job.JobId, PipelineStep.CreatingPullRequest, DateTimeOffset.UtcNow, ct); }
        catch (Exception ex) { _logger.Warning(ex, "Failed to report step transition to {Step}", PipelineStep.CreatingPullRequest); }

        if (run.LinkedPullRequest is not null)
        {
            run.PullRequestUrl = run.LinkedPullRequest.Url;
            run.PullRequestNumber = run.LinkedPullRequest.Number.ToString();
        }

        var prUrl = await prOrchestrator.CreatePullRequestAsync(
            run, report, isDraft, repoProvider, job.IssueDetail, job.IssueComments, config, ct,
            emitOutputLine, isRework: run.LinkedPullRequest is not null);

        if (prUrl is null && config.BlacklistMode == BlacklistMode.Fail && run.BlacklistedFilesDetected.Count > 0)
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
            await issueOps.SwapLabelAsync(run.IssueIdentifier, AgentLabels.Error, ct);
        }
        else
        {
            await issueOps.SwapLabelAsync(run.IssueIdentifier, AgentLabels.Done, ct);
        }

        // ── Reflection + brain post-run sync ──
        if (!isDraft && brainProvider is not null && brainSync is not null && !config.BrainReadOnly)
        {
            run.CurrentStep = PipelineStep.ReflectingOnRun;
            try { await connection.InvokeAsync("ReportStepTransition", job.JobId, PipelineStep.ReflectingOnRun, DateTimeOffset.UtcNow, ct); }
            catch (Exception ex) { _logger.Warning(ex, "Failed to report step transition to {Step}", PipelineStep.ReflectingOnRun); }

            emitOutputLine("🧠 Reflecting on run and updating brain knowledge...");
            try
            {
                var reflectionPrompt = PromptBuilder.BuildReflectionPrompt(
                    run, run.IssueTitle, run.RepositoryName?.Split('/').LastOrDefault());

                await agentProvider.ExecuteAsync(
                    new AgentRequest
                    {
                        Prompt = reflectionPrompt,
                        WorkspacePath = run.WorkspacePath!,
                        Timeout = config.AgentTimeout,
                        UseResume = true
                    },
                    ct,
                    line =>
                    {
                        run.OutputLines.Enqueue(line);
                        emitOutputLine(line);
                    });
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(ex, "Reflection step failed, continuing with brain sync");
            }

            run.CurrentStep = PipelineStep.SyncingBrainRepoPostRun;
            try { await connection.InvokeAsync("ReportStepTransition", job.JobId, PipelineStep.SyncingBrainRepoPostRun, DateTimeOffset.UtcNow, ct); }
            catch (Exception ex) { _logger.Warning(ex, "Failed to report step transition to {Step}", PipelineStep.SyncingBrainRepoPostRun); }

            try { await brainSync.SyncPostRunAsync(run, brainProvider, ct, emitOutputLine, config.BrainPushMaxRetries); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(ex, "Brain post-run sync failed");
                run.BrainUpdatesPushed = false;
            }
        }

        run.CompletedAt = DateTime.UtcNow;
        run.CurrentStep = finalStep;
    }

    private static JobCompletionPayload BuildCompletionPayload(PipelineRun run) => new()
    {
        FinalStep = run.CurrentStep,
        FailureReason = run.FailureReason,
        PullRequestUrl = run.PullRequestUrl,
        PullRequestNumber = run.PullRequestNumber,
        IsDraftPr = run.IsDraftPr,
        RetryCount = run.RetryCount,
        CompletedAt = run.CompletedAt.HasValue ? new DateTimeOffset(run.CompletedAt.Value, TimeSpan.Zero) : DateTimeOffset.UtcNow,
        FilesChangedCount = run.FilesChangedCount,
        LinesAdded = run.LinesAdded,
        LinesRemoved = run.LinesRemoved,
        BrainUpdatesPushed = run.BrainUpdatesPushed,
        AnalysisRecommendation = run.AnalysisRecommendation,
        IsRework = run.LinkedPullRequest is not null,
        AnalysisConcerns = run.AnalysisConcerns,
        AnalysisBlockingIssues = run.AnalysisBlockingIssues,
        BlacklistedFilesDetected = run.BlacklistedFilesDetected,
        CodeReviewAgentsRun = run.CodeReviewAgentsRun,
        CodeReviewCriticalCount = run.CodeReviewCriticalCount,
        CodeReviewWarningCount = run.CodeReviewWarningCount,
        CodeReviewSuggestionCount = run.CodeReviewSuggestionCount
    };

    private static JobCompletionPayload BuildFailurePayload(PipelineRun run, string reason) => new()
    {
        FinalStep = PipelineStep.Failed,
        FailureReason = reason,
        CompletedAt = DateTimeOffset.UtcNow,
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
        CodeReviewSuggestionCount = run.CodeReviewSuggestionCount
    };

    /// <summary>
    /// Writes the MCP server configuration to the workspace at the path specified by
    /// the agent provider's mcpConfigPath setting (defaults to .kiro/settings/mcp.json for Kiro CLI).
    /// Delegates to <see cref="McpConfigWriter.WriteConfig"/> for the shared implementation.
    /// </summary>
    internal static void WriteMcpConfigToWorkspace(string workspacePath, IReadOnlyList<McpServerConfig> mcpServers, string mcpConfigRelativePath)
    {
        var fullPath = Path.Combine(workspacePath, mcpConfigRelativePath);
        McpConfigWriter.WriteConfig(fullPath, mcpServers);
    }

    /// <summary>
    /// Adapts the agent executor's callback methods to <see cref="IPipelineCallbacks"/>.
    /// </summary>
    private sealed class AgentCallbacks(
        Action<PipelineStep> transitionTo,
        Action<string> emitOutputLine,
        IAgentIssueOperations issueOps,
        PullRequestOrchestrator prOrchestrator,
        IRepositoryProvider repoProvider,
        Action<QualityGateReport> reportQualityGateResult,
        Func<PipelineRun, QualityGateReport, bool, CancellationToken, Task> createPullRequest) : IPipelineCallbacks
    {
        public void TransitionTo(PipelineStep step) => transitionTo(step);
        public void EmitOutputLine(string line) => emitOutputLine(line);
        public void NotifyChange() { }
        public void AddRunToHistory(PipelineRun run) { }
        public Task UpdateFileChangeStats(PipelineRun run)
            => prOrchestrator.UpdateFileChangeStatsAsync(run, repoProvider);
        public Task SwapAgentLabel(string issueIdentifier, string label, CancellationToken ct)
            => issueOps.SwapLabelAsync(issueIdentifier, label, ct);
        public Task RemoveAllAgentLabels(string issueIdentifier, CancellationToken ct)
            => issueOps.SwapLabelAsync(issueIdentifier, string.Empty, ct);  // Hub handles empty label as remove-only
        public Task CreatePullRequest(PipelineRun run, QualityGateReport report, bool isDraft, CancellationToken ct)
        {
            reportQualityGateResult(report);
            return createPullRequest(run, report, isDraft, ct);
        }
    }
}
