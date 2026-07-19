using System.Diagnostics;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Prompts;
using CodingAgentWebUI.Pipeline.Telemetry;
using OpenTelemetry.Trace;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Encapsulates the shared post-PR-creation logic (reflection, brain sync, feedback collection)
/// that was previously duplicated between PipelineOrchestrationService and LocalPipelineExecutor.
/// Stateless service — all dependencies are passed per-call.
/// </summary>
public sealed class PullRequestFinalizationService
{
    private readonly Serilog.ILogger _logger;

    public PullRequestFinalizationService(Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Runs the full PR creation and post-PR finalization flow: transition → create PR → post-PR sequence → set final state.
    /// Encapsulates the complete lifecycle from "ready to create PR" through to "run completed/failed".
    /// Sets CompletedAt, CurrentStep, FinalLabel, and (on failure) FailureReason on the run.
    /// </summary>
    // TODO: Validate non-nullable parameters (run, report, prOrchestrator, repoProvider, agentProvider, config, feedbackService, emitOutputLine, transitionCallback) with ArgumentNullException.ThrowIfNull for fail-fast behavior on public API surface.
    public async Task RunFullPrCreationAsync(
        PipelineRun run,
        QualityGateReport report,
        bool isDraft,
        PullRequestOrchestrator prOrchestrator,
        IRepositoryProvider repoProvider,
        IAgentProvider agentProvider,
        IRepositoryProvider? brainProvider,
        IBrainSyncService? brainSync,
        PipelineConfiguration config,
        IssueDetail? issue,
        IReadOnlyList<IssueComment>? issueComments,
        FeedbackService feedbackService,
        IPipelineRunHistoryService? historyService,
        Action<string> emitOutputLine,
        Func<PipelineStep, Task> transitionCallback,
        CancellationToken ct)
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

            await transitionCallback(PipelineStep.CreatingPullRequest);

            if (run.LinkedPullRequest is not null)
            {
                run.PullRequestUrl = run.LinkedPullRequest.Url;
                run.PullRequestNumber = run.LinkedPullRequest.Number.ToString();
            }

            var prUrl = await prOrchestrator.CreatePullRequestAsync(
                run, report, isDraft, repoProvider, issue, issueComments, config, ct,
                emitOutputLine, isRework: run.LinkedPullRequest is not null);

            if (prUrl is null)
            {
                run.FailureReason = "Agent did not produce any changes. No commits ahead of base branch.";
                run.MarkCompleted();
                run.CurrentStep = PipelineStep.Failed;
                return;
            }

            var finalStep = isDraft ? PipelineStep.Failed : PipelineStep.Completed;
            if (isDraft)
            {
                run.FailureReason = "Quality gates failed after max retries; draft PR created.";
            }
            // Label swap (agent:done / agent:error) is handled by the orchestrator in ReportJobCompleted.

            await RunPostPrSequenceAsync(
                run, isDraft, agentProvider, repoProvider, config,
                brainSync, brainProvider, feedbackService, historyService,
                emitOutputLine, transitionCallback, ct);

            run.MarkCompleted();
            run.CurrentStep = finalStep;
            run.FinalLabel = isDraft ? AgentLabels.Error : AgentLabels.Done;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.Error(ex, "Pipeline {RunId} PR creation failed", run.RunId);
            throw;
        }
    }

    /// <summary>
    /// Runs the full post-PR finalization sequence: PR description → reflection → brain sync → feedback.
    /// Conditionally skips steps based on isDraft, brain provider availability, and config.
    /// Does not set CompletedAt or CurrentStep — those remain the caller's responsibility.
    /// </summary>
    // TODO: Validate non-nullable parameters (run, agentProvider, repoProvider, config, feedbackService, emitOutputLine, transitionCallback) with ArgumentNullException.ThrowIfNull for fail-fast consistency.
    public async Task RunPostPrSequenceAsync(
        PipelineRun run, bool isDraft,
        IAgentProvider agentProvider, IRepositoryProvider repoProvider,
        PipelineConfiguration config,
        IBrainSyncService? brainSync, IRepositoryProvider? brainProvider,
        FeedbackService feedbackService, IPipelineRunHistoryService? historyService,
        Action<string> emitOutputLine,
        Func<PipelineStep, Task> transitionCallback,
        CancellationToken ct)
    {
        if (!isDraft && !string.IsNullOrEmpty(run.PullRequestNumber))
        {
            await transitionCallback(PipelineStep.GeneratingPrDescription);
            await GeneratePrDescriptionAsync(run, agentProvider, repoProvider, config, emitOutputLine, ct);
        }

        if (!isDraft && brainProvider is not null && brainSync is not null && !config.BrainReadOnly)
        {
            await transitionCallback(PipelineStep.ReflectingOnRun);
            await RunReflectionAsync(run, agentProvider, config, emitOutputLine, ct);

            await transitionCallback(PipelineStep.SyncingBrainRepoPostRun);
            await SyncBrainPostRunAsync(run, brainSync, brainProvider, config, emitOutputLine, ct);
        }

        // No step transition for feedback — intentionally matches existing behavior
        if (!isDraft)
        {
            await CollectFeedbackAsync(run, agentProvider, feedbackService, historyService, emitOutputLine, ct);
        }
    }

    /// <summary>
    /// Generates an agent-written PR description and updates the PR body.
    /// Does not throw on failure — logs a warning and returns.
    /// </summary>
    public async Task GeneratePrDescriptionAsync(
        PipelineRun run, IAgentProvider agentProvider, IRepositoryProvider repoProvider,
        PipelineConfiguration config, Action<string> emitOutputLine, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("GeneratePrDescription");
        activity?.SetTag("pipeline.run_id", run.RunId);

        emitOutputLine("📝 Generating PR description...");
        try
        {
            var prompt = PromptBuilder.BuildPrDescriptionPrompt(run);

            var result = await agentProvider.ExecuteAsync(
                new AgentRequest
                {
                    Prompt = prompt,
                    WorkspacePath = run.WorkspacePath!,
                    Timeout = config.AgentTimeout,
                    UseResume = true
                },
                ct,
                line => emitOutputLine(line));

            run.AccumulateTokenUsage(result, phase: "pr_description");

            var rawDescription = string.Join("\n", result.OutputLines).Trim();
            var description = StripBlockquotePrefix(rawDescription);
            if (string.IsNullOrWhiteSpace(description))
            {
                _logger.Warning("Pipeline {RunId} PR description generation returned empty output", run.RunId);
                return;
            }

            // Prepend agent summary above existing PR body
            if (!int.TryParse(run.PullRequestNumber, out var prNumber))
            {
                _logger.Warning("Pipeline {RunId} PR description skipped — PullRequestNumber '{PrNumber}' is not a valid integer", run.RunId, run.PullRequestNumber);
                return;
            }
            var currentBody = run.PullRequestBody ?? "";
            var newBody = $"{description}\n\n---\n\n{currentBody}";
            await repoProvider.UpdatePullRequestAsync(prNumber, newBody, false, ct);
            run.PullRequestBody = newBody;

            _logger.Information("Pipeline {RunId} PR description generated and applied", run.RunId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            _logger.Warning(ex, "Pipeline {RunId} PR description generation failed, continuing", run.RunId);
        }
    }

    /// <summary>
    /// Executes the reflection step: builds a reflection prompt and asks the agent to review
    /// the run and enrich .brain/ knowledge. Accumulates token usage on the run.
    /// Does not throw on failure — logs a warning and returns.
    /// </summary>
    public async Task RunReflectionAsync(
        PipelineRun run, IAgentProvider agentProvider, PipelineConfiguration config,
        Action<string> emitOutputLine, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("Reflection");
        activity?.SetTag("pipeline.run_id", run.RunId);

        emitOutputLine("🧠 Reflecting on run and updating brain knowledge...");
        try
        {
            var reflectionPrompt = PromptBuilder.BuildReflectionPrompt(
                run, run.IssueTitle, run.RepositoryName?.Split('/').LastOrDefault());
            _logger.Debug("Pipeline {RunId} reflection prompt:\n{Prompt}", run.RunId, reflectionPrompt);

            var reflectionResult = await agentProvider.ExecuteAsync(
                new AgentRequest
                {
                    Prompt = reflectionPrompt,
                    WorkspacePath = run.WorkspacePath!,
                    Timeout = config.AgentTimeout,
                    UseResume = true
                },
                ct,
                line => emitOutputLine(line));

            run.AccumulateTokenUsage(reflectionResult, phase: "reflection");
            _logger.Information("Pipeline {RunId} reflection step completed", run.RunId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            _logger.Warning(ex, "Pipeline {RunId} reflection step failed, continuing with brain sync", run.RunId);
        }
    }

    /// <summary>
    /// Syncs the brain repository after the run. Delegates to brainSync.SyncPostRunAsync.
    /// Does not throw on failure — logs a warning and sets run.BrainUpdatesPushed = false.
    /// </summary>
    public async Task SyncBrainPostRunAsync(
        PipelineRun run, IBrainSyncService brainSync, IRepositoryProvider brainProvider,
        PipelineConfiguration config, Action<string> emitOutputLine, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("BrainSyncPostRun");
        activity?.SetTag("pipeline.run_id", run.RunId);

        try
        {
            await brainSync.SyncPostRunAsync(run, brainProvider, ct, emitOutputLine, config.BrainPushMaxRetries);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            _logger.Warning(ex, "Pipeline {RunId} brain post-run sync failed", run.RunId);
            run.BrainUpdatesPushed = false;
        }
    }

    /// <summary>
    /// Collects structured feedback from the agent about the run.
    /// On failure, creates a fallback feedback record via feedbackService.
    /// </summary>
    public async Task CollectFeedbackAsync(
        PipelineRun run, IAgentProvider agentProvider, FeedbackService feedbackService,
        IPipelineRunHistoryService? historyService, Action<string> emitOutputLine, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("FeedbackCollection");
        activity?.SetTag("pipeline.run_id", run.RunId);

        emitOutputLine("📋 Collecting run feedback...");
        try
        {
            var elapsed = DateTimeOffset.UtcNow - run.StartedAtOffset;
            var (harnessCategories, issueCategories) = await feedbackService.LoadPreviousCategoriesAsync(historyService).ConfigureAwait(false); // TODO: Propagate CancellationToken ct to LoadPreviousCategoriesAsync

            var feedbackPrompt = FeedbackPromptBuilder.BuildStandaloneFeedbackPrompt(
                run, elapsed, harnessCategories, issueCategories);

            var feedbackResult = await agentProvider.ExecuteAsync(
                new AgentRequest
                {
                    Prompt = feedbackPrompt,
                    WorkspacePath = run.WorkspacePath!,
                    Timeout = TimeSpan.FromSeconds(FeedbackConstraints.FailureFeedbackTimeoutSeconds),
                    UseResume = true
                },
                ct,
                line => emitOutputLine(line));

            var responseText = string.Join("\n", feedbackResult.OutputLines);
            run.Feedback = feedbackService.ParseFeedbackFromResponse(responseText, FeedbackOutcome.Success, DateTime.UtcNow);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            activity?.AddException(ex);
            _logger.Warning(ex, "Pipeline {RunId} feedback collection failed, using fallback", run.RunId);
            run.Feedback = feedbackService.CreateFallbackFeedback(FeedbackOutcome.Success,
                $"Feedback collection failed: {ex.Message}", DateTime.UtcNow);
        }
    }

    /// <summary>
    /// Strips leading blockquote prefix (<c>&gt; </c>) from each line.
    /// Kiro CLI prefixes assistant response lines with <c>&gt;</c> on stdout.
    /// Lines starting with "<c>&gt; </c>" have the prefix removed; bare "<c>&gt;</c>" lines become empty strings.
    /// Mid-line <c>&gt;</c> characters (code, comparisons) are preserved.
    /// </summary>
    private static string StripBlockquotePrefix(string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        var stripped = lines.Select(line =>
            line.StartsWith("> ") ? line[2..] :
            line == ">" ? "" :
            line);
        return string.Join("\n", stripped).Trim();
    }
}
