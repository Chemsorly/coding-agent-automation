using System.Diagnostics;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services.Prompts;
using CodingAgent.Pipeline.Telemetry;
using OpenTelemetry.Trace;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Encapsulates the shared post-PR-creation logic (reflection, brain sync, feedback collection)
/// that was previously duplicated between PipelineOrchestrationService and LocalPipelineExecutor.
/// Stateless service — all dependencies are passed per-call.
/// </summary>
public sealed class PullRequestFinalizationService
{
    private readonly Serilog.ILogger _logger;
    private readonly Func<string?, CancellationToken, Task<IReadOnlyList<string>>> _getChangedFiles;
    private const string PipelineRunIdTag = "pipeline.run_id";

    public PullRequestFinalizationService(
        Serilog.ILogger logger,
        Func<string?, CancellationToken, Task<IReadOnlyList<string>>>? getChangedFiles = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        _getChangedFiles = getChangedFiles ?? GateConfigGuard.GetChangedFilesAsync;
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

            await transitionCallback(PipelineStep.FinalizingPullRequest);

            if (run.LinkedPullRequest is not null)
            {
                run.PullRequestUrl = run.LinkedPullRequest.Url;
                run.PullRequestNumber = run.LinkedPullRequest.Number.ToString();
            }

            var prUrl = await prOrchestrator.CreatePullRequestAsync(
                new PullRequestPublishRequest
                {
                    Run = run,
                    IsDraft = isDraft,
                    RepoProvider = repoProvider,
                    Issue = issue,
                    IssueComments = issueComments,
                    Config = config,
                    OnOutputLine = emitOutputLine
                },
                ct,
                isRework: run.LinkedPullRequest is not null);

            if (prUrl is null)
            {
                run.FailureReason = "Agent did not produce any changes. No commits ahead of base branch.";
                run.MarkCompleted();
                run.CurrentStep = PipelineStep.Failed;
                return;
            }

            if (isDraft)
            {
                // Preserve a more specific failure reason if already set (e.g., "CI never started after N retries").
                // Fall back to the generic draft message for all other exhaustion paths.
                run.FailureReason ??= "Quality gates failed after max retries; draft PR created.";

                // For draft PRs GeneratePrDescriptionAsync is skipped, so inject the not-re-applied
                // section directly here if there are any identifiers to report (issue #3435).
                if (run.NotReappliedIdentifiersByFile.Count > 0 && !string.IsNullOrEmpty(run.PullRequestNumber))
                {
                    try
                    {
                        var updatedBody = AppendDroppedIdentifiersSection(run.PullRequestBody ?? "", run);
                        // TODO: If PullRequestNumber is non-empty but not a valid integer, int.TryParse
                        // silently fails here and UpdatePullRequestAsync is never called — the
                        // dropped-identifier section is silently lost for draft PRs with non-numeric PR
                        // numbers and no diagnostic trace is emitted. Add a warning log branch for the
                        // TryParse failure path (consistent with the warning in GeneratePrDescriptionAsync).
                        if (int.TryParse(run.PullRequestNumber, out var draftPrNumber))
                        {
                            await repoProvider.UpdatePullRequestAsync(draftPrNumber, updatedBody, null, ct);
                            run.PullRequestBody = updatedBody;
                            _logger.Information(
                                "Pipeline {RunId} appended not-re-applied section to draft PR body", run.RunId);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.Warning(ex,
                            "Pipeline {RunId} failed to append not-re-applied section to draft PR body, continuing",
                            run.RunId);
                    }
                }
            }
            // Label swap (agent:done / agent:error) is handled by the orchestrator in ReportJobCompleted.
        }
        // Log-before-rethrow is the intended observability contract here (pinned by ThrowLoggingTests);
        // the original exception type must propagate unchanged to the step runner.
        catch (Exception ex) when (ex is not OperationCanceledException) // NOSONAR S2139 — logged here by design, see above
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            _logger.Error(ex, "Pipeline {RunId} PR creation failed", run.RunId);
            throw;
        }

        var finalStep = isDraft ? PipelineStep.Failed : PipelineStep.Completed;

        try
        {
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
        }
        finally
        {
            // Always set terminal state regardless of whether RunPostPrSequenceAsync threw
            // (including OperationCanceledException from brain sync or reflection steps).
            // A run that reached PR creation must always exit with CompletedAt set so that
            // DatabaseMaintenanceService retention sweeps can clean it up and the Active Runs
            // panel does not accumulate ghost records with null CompletedAt.
            run.MarkCompleted();
            run.CurrentStep = finalStep;
            // FinalLabel is set unconditionally in the finally block so that it is assigned on all
            // exit paths including OperationCanceledException from RunPostPrSequenceAsync. The OCE
            // case is covered by RunFullPrCreationAsync_DraftOce_SetsFinalLabelError (draft) and
            // RunFullPrCreationAsync_OceFromRunPostPrSequenceAsync_StillSetsCompletedAt (non-draft).
            // The caller (orchestrator) may apply a further label-swap via ReportJobCompleted; that
            // interaction is intentional and documented in AgentJobLifecycleService.
            run.FinalLabel = isDraft ? AgentLabels.Error : AgentLabels.Done;
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
        ct.ThrowIfCancellationRequested();
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
            await GeneratePrDescriptionAsync(run, agentProvider, repoProvider, config, emitOutputLine, ct);

            // Mark ready-for-review last — after description is applied so reviewers see the complete body.
            // Non-fatal: a failure to mark-ready is logged but does not abort the post-PR sequence.
            if (int.TryParse(run.PullRequestNumber, out var prNum))
            {
                await MarkReadyUnlessGateConfigChangedAsync(run, prNum, repoProvider, emitOutputLine, ct);
            }
            else
            {
                _logger.Warning("Pipeline {RunId} mark-ready skipped — PullRequestNumber '{PrNumber}' is not a valid integer",
                    run.RunId, run.PullRequestNumber);
            }
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
            // The brain post-run sync was skipped — log the reason.
            _logger.Information(
                "Pipeline {RunId} skipping brain post-run sync: isDraft={IsDraft}, brainProvider={HasProvider}, brainSync={HasSync}, brainReadOnly={ReadOnly}",
                run.RunId, isDraft, brainProvider is not null, brainSync is not null, config.BrainReadOnly);
        }

        // No step transition for feedback — intentionally matches existing behavior
        if (!isDraft)
        {
            await CollectFeedbackAsync(run, agentProvider, feedbackService, historyService, emitOutputLine, ct, config);
        }
    }

    /// <summary>
    /// Marks the PR ready for review, unless the branch changes quality-gate configuration: then the PR
    /// stays a draft with a warning section so that a person reviews the change; agents have weakened
    /// failing checks this way (#3585). Non-fatal: failures are logged and the post-PR sequence continues.
    /// </summary>
    private async Task MarkReadyUnlessGateConfigChangedAsync(
        PipelineRun run, int prNum, IRepositoryProvider repoProvider, Action<string> emitOutputLine, CancellationToken ct)
    {
        IReadOnlyList<string> gateFiles;
        try
        {
            gateFiles = GateConfigGuard.FindGateConfigFiles(await _getChangedFiles(run.WorkspacePath, ct));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Pipeline {RunId} could not list changed files for the quality-gate configuration check, continuing", run.RunId);
            gateFiles = [];
        }

        if (gateFiles.Count > 0)
        {
            try
            {
                var body = GateConfigGuard.AppendWarningSection(run.PullRequestBody ?? "", gateFiles);
                await repoProvider.UpdatePullRequestAsync(prNum, body, false, ct);
                run.PullRequestBody = body;
                emitOutputLine($"⚠️ PR #{run.PullRequestNumber} changes quality-gate configuration ({string.Join(", ", gateFiles)}) — left as draft for review");
                _logger.Warning("Pipeline {RunId} left PR {PrNumber} as draft: it changes quality-gate configuration {Files}",
                    run.RunId, run.PullRequestNumber, gateFiles);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(ex, "Pipeline {RunId} failed to leave PR as draft for quality-gate configuration change, continuing", run.RunId);
            }
            return;
        }

        try
        {
            // TODO [WARNING]: run.PullRequestBody may be stale here when GeneratePrDescriptionAsync
            // did not update it (file missing, empty output, or invalid PR number inside
            // GeneratePrDescriptionAsync). In those skip paths run.PullRequestBody still holds the
            // pre-description body, causing the mark-ready PATCH to send body content that differs
            // from what was last written to the API by GeneratePrDescriptionAsync on the happy path.
            // Consider always keeping run.PullRequestBody in sync with the latest value sent to the
            // API on exit from GeneratePrDescriptionAsync, or passing null for the body argument here
            // to make this a state-change-only call that avoids overwriting with potentially stale
            // content. The body divergence is functionally harmless (idempotent on the happy path,
            // fallback body on skip) but obscures which body revision was used for the final PR state.
            // TODO [WARNING]: TaskCanceledException is a subclass of OperationCanceledException, so the
            // "when (ex is not OperationCanceledException)" filter correctly excludes both. This is the
            // intended behavior matching all other UpdatePullRequestAsync guards in this file.
            await repoProvider.UpdatePullRequestAsync(prNum, run.PullRequestBody ?? "", true, ct);
            emitOutputLine($"✅ PR #{run.PullRequestNumber} marked ready for review");
            // Record the mark-ready timestamp so post-PR CI polling can use it as the
            // notBefore anchor (filters out push-event CI runs that completed before
            // mark-ready, ensuring only the pull_request-event CI is observed).
            // TODO [WARNING] (DotNetSpecialist): DateTime.UtcNow is captured after the
            // async continuation resumes, not at the exact instant the HTTP call returned.
            // On a busy thread pool the delta between HTTP completion and this assignment
            // can be a few ms. This cannot produce a false-negative (timestamp is always
            // >= actual mark-ready, never before it), so no CI run will be wrongly accepted
            // due to this. The XML doc on PrMarkedReadyAt describes the field as set after
            // UpdatePullRequestAsync(markReady:true) completes successfully — that approximation
            // is acceptable given the usage as a filter anchor, but reviewers should be aware
            // of the imprecision if clock resolution requirements tighten.
            run.PrMarkedReadyAt = DateTime.UtcNow;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Pipeline {RunId} failed to mark PR ready for review, continuing", run.RunId);
        }
    }

    /// <summary>
    /// Generates an agent-written PR description and updates the PR body.
    /// Does not throw on failure — logs a warning and returns.
    /// Emits <c>pipeline.step.duration{step_name="GeneratePrDescription"}</c> unconditionally (including on failure).
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

            var filePath = Path.Combine(run.WorkspacePath!, AgentWorkspacePaths.PrDescriptionFilePath);
            string rawDescription;
            try
            {
                rawDescription = await File.ReadAllTextAsync(filePath, ct);
            }
            // A missing .agent/ directory throws DirectoryNotFoundException, a sibling of FileNotFoundException;
            // both mean the agent wrote no description.
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                // The agent's stdout mixes in tool output and reasoning, so it never becomes the PR body
                // (decisions.md: the PR narrative never comes from the agent's stdout).
                _logger.Warning("Pipeline {RunId} PR description file not found at {Path}, keeping the generated PR body", // NOSONAR S6667 — expected missing file; the message says so
                    run.RunId, filePath);
                await TryAppendDroppedIdentifiersSectionAsync(run, repoProvider, ct);
                return;
            }

            var description = StripBlockquotePrefix(rawDescription);
            if (string.IsNullOrWhiteSpace(description))
            {
                _logger.Warning("Pipeline {RunId} PR description generation returned empty output", run.RunId);
                await TryAppendDroppedIdentifiersSectionAsync(run, repoProvider, ct);
                return;
            }

            // Prepend agent summary above existing PR body
            if (!int.TryParse(run.PullRequestNumber, out var prNumber))
            {
                _logger.Warning("Pipeline {RunId} PR description skipped — PullRequestNumber '{PrNumber}' is not a valid integer", run.RunId, run.PullRequestNumber);
                await TryAppendDroppedIdentifiersSectionAsync(run, repoProvider, ct);
                return;
            }
            var currentBody = run.PullRequestBody;
            var newBody = string.IsNullOrWhiteSpace(currentBody)
                ? description
                : $"{description}\n\n---\n\n{currentBody}";
            newBody = AppendDroppedIdentifiersSection(newBody, run);
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
    /// Delegates to <see cref="FeedbackService.CollectFeedbackCoreAsync"/> for the shared
    /// collect-parse-fallback sequence. Pipeline-level cancellation propagates; timeouts and
    /// non-OCE exceptions produce a fallback record via <see cref="FeedbackService.CreateFallbackFeedback"/>.
    /// Emits <c>pipeline.step.duration{step_name="FeedbackCollection"}</c> unconditionally (including on failure).
    /// </summary>
    public async Task CollectFeedbackAsync(
        PipelineRun run, IAgentProvider agentProvider, FeedbackService feedbackService,
        IPipelineRunHistoryService? historyService, Action<string> emitOutputLine, CancellationToken ct,
        PipelineConfiguration config)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("FeedbackCollection");
        activity?.SetTag(PipelineRunIdTag, run.RunId);

        emitOutputLine("📋 Collecting run feedback...");
        try
        {
            var elapsed = DateTimeOffset.UtcNow - run.StartedAtOffset;
            await feedbackService.CollectFeedbackCoreAsync(
                new FeedbackCollectionRequest
                {
                    Run = run,
                    AgentProvider = agentProvider,
                    HistoryService = historyService,
                    PromptFactory = cats => FeedbackPromptBuilder.BuildStandaloneFeedbackPrompt(
                        run, elapsed, cats.HarnessCategories, cats.IssueCategories),
                    Outcome = FeedbackOutcome.Success,
                    FeedbackTimeoutSeconds = config.FeedbackTimeoutSeconds,
                    EmitOutputLine = emitOutputLine
                },
                ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // CollectFeedbackCoreAsync absorbs non-OCE exceptions internally (producing a fallback),
            // so this outer catch is a safety guard for exceptions raised outside the agent call
            // (e.g., from the elapsed computation). Pipeline-level OCE propagates.
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

    /// <summary>
    /// Appends the "Dropped changes not re-applied" section to <paramref name="body"/> when
    /// <see cref="PipelineRun.NotReappliedIdentifiersByFile"/> is non-empty.
    /// Returns <paramref name="body"/> unchanged when there are no not-re-applied identifiers.
    /// The section is appended after the agent description and before any <c>---</c> divider.
    /// </summary>
    private static string AppendDroppedIdentifiersSection(string body, PipelineRun run)
    {
        if (run.NotReappliedIdentifiersByFile.Count == 0)
            return body;

        var section = new System.Text.StringBuilder();
        section.AppendLine();
        section.AppendLine("## ⚠️ Dropped changes not re-applied");
        section.AppendLine();
        section.AppendLine("The force-resolved rebase dropped the following identifiers that this branch had added. " +
            "They were not re-applied during code generation:");
        section.AppendLine();
        foreach (var (path, ids) in run.NotReappliedIdentifiersByFile)
        {
            var idList = string.Join(", ", ids.Select(id => $"`{id}`"));
            section.AppendLine($"- `{path}`: {idList}");
        }
        section.AppendLine();
        section.Append("These may have been intentionally dropped (if outside the issue scope) or accidentally omitted.");

        return body + section.ToString();
    }

    /// <summary>
    /// Appends the "Dropped changes not re-applied" section to the PR body and calls
    /// <see cref="IRepositoryProvider.UpdatePullRequestAsync"/> when
    /// <see cref="PipelineRun.NotReappliedIdentifiersByFile"/> is non-empty and
    /// <see cref="PipelineRun.PullRequestNumber"/> parses to a valid integer.
    /// No-op when there are no dropped identifiers or the PR number is non-numeric.
    /// Uses <see cref="PipelineRun.PullRequestBody"/> as the base body (correct for early-return
    /// paths where no agent description has been prepended yet).
    /// Non-OCE exceptions are caught and logged as warnings — pipeline execution continues.
    /// </summary>
    private async Task TryAppendDroppedIdentifiersSectionAsync(
        PipelineRun run, IRepositoryProvider repoProvider, CancellationToken ct)
    {
        if (run.NotReappliedIdentifiersByFile.Count == 0)
            return;
        if (!int.TryParse(run.PullRequestNumber, out var prNumber))
            return;
        try
        {
            var updatedBody = AppendDroppedIdentifiersSection(run.PullRequestBody ?? "", run);
            await repoProvider.UpdatePullRequestAsync(prNumber, updatedBody, null, ct);
            run.PullRequestBody = updatedBody;
            _logger.Information("Pipeline {RunId} appended not-re-applied section to PR body", run.RunId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // TODO: This catch suppresses all non-OCE exceptions from UpdatePullRequestAsync, including
            // transient network errors. A failed update (e.g. on the file-not-found early-return path)
            // is only logged as a warning; the orchestrator is not notified and the dropped-identifier
            // section will be permanently absent from that run's PR body. Consistent with the pre-existing
            // draft-path behavior but means no retry is possible. Consider surfacing the failure if
            // retry logic is added to the outer pipeline in future.
            _logger.Warning(ex,
                "Pipeline {RunId} failed to append not-re-applied section to PR body, continuing", run.RunId);
        }
    }
}
