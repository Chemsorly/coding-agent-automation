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
    private const string PipelineRunIdTag = "pipeline.run_id";

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
        PrCreationRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var run = request.Run;
        var report = request.Report;
        var isDraft = request.IsDraft;
        var prOrchestrator = request.PrOrchestrator;
        var repoProvider = request.RepoProvider;
        var agentProvider = request.AgentProvider;
        var brainProvider = request.BrainProvider;
        var brainSync = request.BrainSync;
        var config = request.Config;
        var issue = request.Issue;
        var issueComments = request.IssueComments;
        var feedbackService = request.FeedbackService;
        var historyService = request.HistoryService;
        var emitOutputLine = request.EmitOutputLine;
        var transitionCallback = request.TransitionCallback;
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("CreatePullRequest");
        activity?.SetTag(PipelineRunIdTag, run.RunId);
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
                new PostPrSequenceRequest
                {
                    Run = run,
                    IsDraft = isDraft,
                    AgentProvider = agentProvider,
                    RepoProvider = repoProvider,
                    Config = config,
                    BrainSync = brainSync,
                    BrainProvider = brainProvider,
                    FeedbackService = feedbackService,
                    HistoryService = historyService,
                    EmitOutputLine = emitOutputLine,
                    TransitionCallback = transitionCallback
                },
                ct);

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
        PostPrSequenceRequest request,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var run = request.Run;
        var isDraft = request.IsDraft;
        var agentProvider = request.AgentProvider;
        var repoProvider = request.RepoProvider;
        var config = request.Config;
        var brainSync = request.BrainSync;
        var brainProvider = request.BrainProvider;
        var feedbackService = request.FeedbackService;
        var historyService = request.HistoryService;
        var emitOutputLine = request.EmitOutputLine;
        var transitionCallback = request.TransitionCallback;

        if (!isDraft && !string.IsNullOrEmpty(run.PullRequestNumber))
        {
            await transitionCallback(PipelineStep.GeneratingPrDescription);
            await GeneratePrDescriptionAsync(run, agentProvider, repoProvider, config, emitOutputLine, ct);
        }
        else
        {
            _logger.Information(
                "Pipeline {RunId} skipping PR description: isDraft={IsDraft}, hasPrNumber={HasPrNumber}",
                run.RunId, isDraft, !string.IsNullOrEmpty(run.PullRequestNumber));
        }

        if (!isDraft && brainProvider is not null && brainSync is not null && !config.BrainReadOnly)
        {
            await transitionCallback(PipelineStep.ReflectingOnRun);
            await RunReflectionAsync(run, agentProvider, config, emitOutputLine, ct);

            await transitionCallback(PipelineStep.SyncingBrainRepoPostRun);
            await SyncBrainPostRunAsync(run, brainSync, brainProvider, config, emitOutputLine, ct);
        }
        else
        {
            // Emit a tagged skip counter so brain.sync.skipped always appears in Prometheus
            // for runs that reach finalization, making the skip reason diagnosable without Loki.
            // Priority: isDraft wins → no_provider → no_sync_service → read_only.
            var skipReason = isDraft ? "is_draft"
                : brainProvider is null ? "no_provider"
                : brainSync is null ? "no_sync_service"
                : "read_only";
            PipelineTelemetry.BrainSyncSkipped.Add(1,
                new KeyValuePair<string, object?>("reason", skipReason));
            // TODO [WARNING]: The five log arguments (RunId, isDraft, brainProvider!=null, brainSync!=null, BrainReadOnly)
            // resolve to Information(string, params object[]) because Serilog's ILogger has generic overloads only up to
            // 3 type params. Moq Verify calls targeting this log must match the params-array overload, not individual
            // typed matchers, to avoid silently vacuous assertions.
            _logger.Information(
                "Pipeline {RunId} skipping brain post-run sync: isDraft={IsDraft}, brainProvider={HasProvider}, brainSync={HasSync}, brainReadOnly={ReadOnly}",
                run.RunId, isDraft, brainProvider is not null, brainSync is not null, config.BrainReadOnly);
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
        activity?.SetTag(PipelineRunIdTag, run.RunId);

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

            // TODO: TOCTOU race — File.Exists followed by File.ReadAllTextAsync means the file could be deleted
            // between the two calls, causing FileNotFoundException to be caught by the outer handler with a
            // misleading log message. Prefer attempting File.ReadAllTextAsync directly and catching
            // FileNotFoundException explicitly to make the "file absent" intent distinct from unexpected failures.
            var filePath = Path.Combine(run.WorkspacePath!, AgentWorkspacePaths.PrDescriptionFilePath);
            if (!File.Exists(filePath))
            {
                // TODO: The issue requirement stated a fallback to OutputLines-based extraction when the file is
                // absent. The current implementation skips the update entirely instead. If the agent fails to
                // write the file (e.g., tool execution error), the PR body receives no description. Consider
                // whether a best-effort OutputLines fallback is worth restoring for resilience.
                _logger.Warning("Pipeline {RunId} PR description file not found at {Path}, description skipped",
                    run.RunId, filePath);
                return;
            }

            var rawDescription = await File.ReadAllTextAsync(filePath, ct);
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
            await repoProvider.UpdatePullRequestAsync(prNumber, newBody, null, ct);
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
        activity?.SetTag(PipelineRunIdTag, run.RunId);

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
        activity?.SetTag(PipelineRunIdTag, run.RunId);

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
        activity?.SetTag(PipelineRunIdTag, run.RunId);

        emitOutputLine("📋 Collecting run feedback...");
        try
        {
            var elapsed = DateTimeOffset.UtcNow - run.StartedAtOffset;
            var (harnessCategories, issueCategories) = await feedbackService.LoadPreviousCategoriesAsync(historyService, ct).ConfigureAwait(false);

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
        {
            if (line.StartsWith("> ")) return line[2..];
            if (line == ">") return "";
            return line;
        });
        return string.Join("\n", stripped).Trim();
    }
}
