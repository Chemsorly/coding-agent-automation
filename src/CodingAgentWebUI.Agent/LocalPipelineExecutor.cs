using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.AspNetCore.SignalR.Client;
namespace CodingAgentWebUI.Agent;

/// <summary>
/// Executes the full pipeline locally on the agent, replicating the flow from
/// <see cref="PipelineOrchestrationService.ExecutePipelineStepsAsync"/>.
/// Reports all progress back to the orchestrator via SignalR hub methods.
/// </summary>
public sealed class LocalPipelineExecutor
{
    private readonly IProviderFactory _providerFactory;
    private readonly IQualityGateValidator _qualityGateValidator;
    private readonly IBrainUpdateService? _brainUpdateService;
    private readonly Serilog.ILogger _logger;

    public LocalPipelineExecutor(
        IProviderFactory providerFactory,
        IQualityGateValidator qualityGateValidator,
        Serilog.ILogger logger,
        IBrainUpdateService? brainUpdateService = null)
    {
        ArgumentNullException.ThrowIfNull(providerFactory);
        ArgumentNullException.ThrowIfNull(qualityGateValidator);
        ArgumentNullException.ThrowIfNull(logger);

        _providerFactory = providerFactory;
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
            repoProvider = _providerFactory.CreateRepositoryProvider(repoConfig);
            agentProvider = _providerFactory.CreateAgentProvider(agentConfig);

            if (!string.IsNullOrEmpty(job.BrainProviderConfigId))
            {
                var brainConfig = job.ProviderConfigs.FirstOrDefault(c => c.Id == job.BrainProviderConfigId);
                if (brainConfig is not null)
                {
                    try
                    {
                        brainProvider = _providerFactory.CreateRepositoryProvider(brainConfig);
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
                    pipelineProvider = _providerFactory.CreatePipelineProvider(pipelineConfig);
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
            ModelName = agentProvider is Infrastructure.Agent.KiroCliAgentProvider kp ? kp.Model : null,
            BrainProviderConfigId = brainProvider is not null ? job.BrainProviderConfigId : null,
            InitiatedBy = job.InitiatedBy,
            LinkedPullRequest = job.LinkedPullRequest,
            AgentId = Environment.MachineName
        };

        run.IssueLabels = job.IssueDetail.Labels;

        // Orchestrators
        var agentExecution = new AgentExecutionOrchestrator(_logger);
        var ciLogWriter = new CiLogWriter(_logger);
        var prOrchestrator = new PullRequestOrchestrator(_logger);
        var qualityGates = new QualityGateOrchestrator(_qualityGateValidator, ciLogWriter, prOrchestrator, _logger);
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

            // ── Clone ──
            var workspacePath = Path.Combine(config.WorkspaceBaseDirectory, run.RunId);
            Directory.CreateDirectory(workspacePath);
            run.WorkspacePath = workspacePath;

            TransitionTo(PipelineStep.CloningRepository);
            EmitOutputLine($"📋 Cloning repository {run.RepositoryName}...");

            try { await repoProvider.CloneAsync(workspacePath, linkedCt); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return BuildFailurePayload(run, $"Repository clone failed: {ex.Message}");
            }

            // ── Brain sync pre-run ──
            if (brainProvider is not null && brainSync is not null)
            {
                TransitionTo(PipelineStep.SyncingBrainRepoPreRun);
                try { await brainSync.SyncPreRunAsync(run, brainProvider, workspacePath, linkedCt, EmitOutputLine); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning(ex, "Brain sync pre-run failed, continuing without brain context");
                    run.BrainContextLoaded = false;
                }

                // Report brain sync result to orchestrator for UI display
                try { await connection.InvokeAsync("ReportBrainSyncResult", job.JobId, run.BrainContextLoaded, run.BrainKnowledgeFileCount, linkedCt); }
                catch (Exception ex) { _logger.Warning(ex, "Failed to report brain sync result"); }
            }

            // ── Rework detection ──
            if (run.LinkedPullRequest is null)
            {
                try
                {
                    var agentPrs = await repoProvider.GetAgentPullRequestsAsync(run.IssueIdentifier, linkedCt);
                    if (agentPrs.Count > 0)
                    {
                        var selectedPr = agentPrs.OrderByDescending(pr => pr.Number).First();
                        run.LinkedPullRequest = selectedPr;
                        EmitOutputLine($"🔄 Rework mode: updating existing PR #{selectedPr.Number}");
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning(ex, "Failed to detect agent PRs, falling back to new-issue flow");
                }
            }

            // ── Branch ──
            TransitionTo(PipelineStep.CreatingBranch);
            if (run.LinkedPullRequest is not null)
            {
                try
                {
                    await repoProvider.CheckoutRemoteBranchAsync(workspacePath, run.LinkedPullRequest.BranchName, linkedCt);
                    run.BranchName = run.LinkedPullRequest.BranchName;
                    EmitOutputLine($"🌿 Checked out existing branch {run.BranchName}");

                    var mergeResult = await repoProvider.MergeFromBaseAsync(workspacePath, linkedCt);
                    run.MergeConflictFiles = mergeResult.ConflictFiles;
                    EmitOutputLine(mergeResult.HasConflicts
                        ? $"⚠️ Merged from {repoProvider.BaseBranch} with {mergeResult.ConflictFiles.Count} conflict(s)"
                        : $"🔀 Merged from {repoProvider.BaseBranch} (no conflicts)");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return BuildFailurePayload(run, $"Branch checkout/merge failed: {ex.Message}");
                }
            }
            else
            {
                EmitOutputLine("🌿 Creating branch...");
                try
                {
                    var branchName = PipelineFormatting.GenerateBranchName(run.IssueIdentifier, job.IssueDetail.Title, run.RunId);
                    run.BranchName = await repoProvider.CreateBranchAsync(workspacePath, branchName, linkedCt);
                    EmitOutputLine($"🌿 Created branch {run.BranchName}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    return BuildFailurePayload(run, $"Branch creation failed: {ex.Message}");
                }
            }

            // ── Analysis ──
            // NOTE: job.ExistingAnalysis and job.ForceRefreshAnalysis are available on the job
            // but the shared AgentExecutionOrchestrator re-derives them from job.IssueComments
            // for consistency with the local execution path.
            EmitOutputLine("🔍 Starting analysis...");
            var analysisShouldContinue = await agentExecution.ExecuteAnalysisPhaseAsync(
                run, config, agentProvider, issueOps,
                job.IssueDetail, job.ParsedIssue, job.IssueComments,
                step => TransitionTo(step),
                _ => { }, // addRunToHistory — agent doesn't persist locally
                EmitOutputLine, () => { }, linkedCt);

            if (!analysisShouldContinue)
                return BuildCompletionPayload(run);

            // ── Code generation ──
            string? reworkPromptOverride = null;
            if (run.LinkedPullRequest is not null)
            {
                reworkPromptOverride = PromptBuilder.BuildReworkPrompt(
                    run.MergeConflictFiles,
                    run.LinkedPullRequest.ReviewComments,
                    isDraft: run.LinkedPullRequest.IsDraft);

                if (reworkPromptOverride is null)
                    EmitOutputLine("⏭️ No conflicts, review comments, or draft status — skipping code generation");
                else
                    EmitOutputLine("⚙️ Starting rework code generation...");
            }
            else
            {
                EmitOutputLine("⚙️ Starting code generation...");
            }

            if (reworkPromptOverride is not null || run.LinkedPullRequest is null)
            {
                var shouldContinue = await agentExecution.ExecuteCodeGenerationAsync(
                    run, config, agentProvider,
                    job.IssueDetail, job.ParsedIssue,
                    localCts,
                    step => TransitionTo(step),
                    EmitOutputLine, () => { },
                    r => prOrchestrator.UpdateFileChangeStatsAsync(r, repoProvider),
                    issueOps,
                    _ => { }, // addRunToHistory
                    linkedCt,
                    promptOverride: reworkPromptOverride);

                if (!shouldContinue)
                    return BuildCompletionPayload(run);
            }

            // ── Brain pull before write ──
            if (brainProvider is not null && brainSync is not null && !config.BrainReadOnly && run.BrainContextLoaded)
            {
                try { await brainSync.PullBeforeWriteAsync(run, brainProvider, linkedCt); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { _logger.Warning(ex, "Brain repo pull-before-write failed, continuing"); }
            }

            // ── Code review ──
            await agentExecution.ExecuteCodeReviewAsync(
                run, config, agentProvider,
                job.IssueDetail, job.ParsedIssue,
                localCts,
                step => TransitionTo(step),
                EmitOutputLine, () => { }, linkedCt);

            // ── Quality gates ──
            await qualityGates.ProceedToQualityGatesAsync(
                run, config, agentProvider, repoProvider, pipelineProvider,
                localCts,
                step => TransitionTo(step),
                issueOps,
                (id, token) => issueOps.SwapLabelAsync(id, string.Empty, token), // removeAllAgentLabels — orchestrator handles
                _ => { }, // addRunToHistory
                EmitOutputLine, () => { },
                async (r, report, isDraft, token) =>
                {
                    ReportQualityGateResult(report);
                    await CreatePullRequestAsync(r, report, isDraft, repoProvider, agentProvider,
                        brainProvider, brainSync, config, issueOps, connection, job, EmitOutputLine, token);
                },
                linkedCt);

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
                if (run.CurrentStep == PipelineStep.Completed && config.CleanupSuccessfulWorkspaces
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
        catch { /* best effort */ }

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
            catch { /* best effort */ }

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
            catch { /* best effort */ }

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
}
