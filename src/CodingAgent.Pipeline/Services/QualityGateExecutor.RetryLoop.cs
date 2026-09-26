using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services.Prompts;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services;

public partial class QualityGateExecutor
{
    /// <summary>Maximum consecutive transient provider errors before the retry loop is aborted.</summary>
    private const int MaxConsecutiveTransientRetries = 10;

    // Outcome tag values for the quality_gate.retries counter.
    // These match the per-branch semantics of RunFixAgentIterationAsync.
    private const string OutcomeTransient = "transient";
    private const string OutcomeAuthAbort = "auth_abort";
    private const string OutcomeSessionRestart = "session_restart";
    private const string OutcomeRetry = "retry";

    /// <summary>
    /// Runs quality gate validation with retry logic and PR creation.
    /// </summary>
    public async Task ProceedToQualityGatesAsync(QualityGateContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var run = context.Run;
        var config = context.Config;
        var callbacks = context.Callbacks;
        callbacks.TransitionTo(PipelineStep.RunningQualityGates);

        var qgStopwatch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var linkedCts = context.OrchestratorCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(ct, context.OrchestratorCts.Token)
                : null;
            var linkedCt = linkedCts?.Token ?? ct;

            callbacks.EmitOutputLine("🏗️ Running quality gates...");
            var report = await RunQualityGateValidationAsync(context, run.WorkspacePath!, config, linkedCt);

            report = await AppendExternalCiIfNeededAsync(context, report, allowEmptyCommit: false, linkedCt);
            if (run.CurrentStep is PipelineStep.Failed or PipelineStep.ConflictRestart
                    or PipelineStep.PrMerged or PipelineStep.PrClosed) return;

            LogAndRecordReport(context, report, "quality gates");

            report = await RunRetryLoopAsync(context, report, "Quality gate retry agent", linkedCt);
            if (run.CurrentStep is PipelineStep.Failed or PipelineStep.ConflictRestart
                    or PipelineStep.PrMerged or PipelineStep.PrClosed) return;

            if (report.AllPassed)
                await RunPostRetryCleanupAndFinalizeAsync(context, linkedCt);
            else
                await FinalizeDraftPrAsync(context, run, report, "exhausted", linkedCt);
        }
        catch (OperationCanceledException ex)
        {
            if (run.CurrentStep is not (PipelineStep.Cancelled or PipelineStep.Failed))
            {
                _logger.Information(ex, "Pipeline {RunId} was cancelled during quality gates", run.RunId);
                // TODO: [WARNING] Lambda parameter `ct` shadows the method-level `ct` parameter of
                // ProceedToQualityGatesAsync. This is intentional — the outer token may already be
                // cancelled and CancellationToken.None is passed as the argument — but the shadowing
                // is a latent maintenance hazard. Consider renaming the lambda parameter (e.g., `token`)
                // to make the shadowing explicit and self-documenting.
                // TODO: [WARNING] Partial-finalization risk: if SwapAgentLabel throws (e.g., transient
                // HttpRequestException), run.MarkCompleted() will already have set CompletedAt, but
                // TransitionTo and AddRunToHistoryAsync will not have been called, leaving the run
                // half-finalized with the exception swallowed by the finally block. Consider wrapping
                // swapLabel in a try/catch inside FinalizeRunAsync to ensure TransitionTo and
                // AddRunToHistoryAsync still execute even when the label swap fails.
                await FinalizeRunAsync(context, run,
                    ct => callbacks.SwapAgentLabel(run.IssueIdentifier, AgentLabels.Cancelled, ct),
                    "🚫 Pipeline cancelled",
                    PipelineStep.Cancelled,
                    CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            // TODO: [WARNING] The guard only checks Failed and Cancelled. Other terminal states
            // (e.g. ConflictRestart) are not included. If a future inner call sets CurrentStep to
            // another terminal value and then throws a non-OCE exception, FinalizeRunAsync will
            // still be re-entered. Consider extending the guard to cover all terminal states, or
            // replace the explicit list with a helper method like run.IsTerminal().
            if (run.CurrentStep.IsTerminal()) return;
            _logger.Error(ex, "Pipeline {RunId} quality gate validation failed", run.RunId);
            run.FailureReason = $"Quality gate validation error: {ex.Message}";
            _logger.Information(
                "Pipeline {RunId} QualityGateExecutor swapping label to agent:error for issue {IssueIdentifier} (reason=quality gate validation error)",
                run.RunId, run.IssueIdentifier);
            var failureOutputLine = $"❌ Pipeline failed: {run.FailureReason}";
            // TODO: [WARNING] Lambda parameter `ct` shadows the method-level `ct` parameter of
            // ProceedToQualityGatesAsync. See the same note on the cancellation arm above.
            // TODO: [WARNING] Partial-finalization risk: if SwapLabelAsync throws, MarkCompleted()
            // will already have been called but TransitionTo and AddRunToHistoryAsync will not run.
            // See the note on the cancellation arm above for the suggested mitigation.
            await FinalizeRunAsync(context, run,
                ct => context.IssueOps.SwapLabelAsync(run.IssueIdentifier, AgentLabels.Error, ct),
                failureOutputLine,
                PipelineStep.Failed,
                CancellationToken.None);
        }
        finally
        {
            _qualityGateDuration.Record(
                qgStopwatch.Elapsed.TotalSeconds,
                PipelineTelemetry.BuildTags(run.RunType, run.ProjectId, run.ProjectName));
        }
    }

    /// <summary>
    /// Shared terminal sequence for both <see cref="ProceedToQualityGatesAsync"/> catch arms:
    /// marks the run completed, swaps the issue label, emits a UI output line, transitions the
    /// pipeline step, and records the run in history.
    /// <para>
    /// Always called with <see cref="CancellationToken.None"/> — the incoming cancellation token
    /// may already be cancelled at catch-arm entry, and all finalization calls must complete
    /// regardless. The <paramref name="ct"/> parameter flows only to the <paramref name="swapLabel"/>
    /// delegate; <see cref="IPipelineCallbacks.AddRunToHistoryAsync"/> does not accept a token.
    /// </para>
    /// </summary>
    private async Task FinalizeRunAsync(
        QualityGateContext context,
        PipelineRun run,
        Func<CancellationToken, Task> swapLabel,
        string outputLine,
        PipelineStep step,
        CancellationToken ct)
    {
        // TODO: [WARNING] The `ct` parameter is only forwarded to the swapLabel delegate;
        // AddRunToHistoryAsync is called without any cancellation token. This is intentional
        // (the incoming token may already be cancelled), but if AddRunToHistoryAsync is ever
        // changed to accept a CancellationToken, the partial propagation gap will be silently
        // retained. Revisit when that interface changes.
        run.MarkCompleted();
        await swapLabel(ct);
        context.Callbacks.EmitOutputLine(outputLine);
        context.Callbacks.TransitionTo(step);
        await context.Callbacks.AddRunToHistoryAsync(run);
    }

    /// <summary>
    /// Runs the pre-PR cleanup agent, then the final quality gate pass, and finalizes the PR.
    /// Called after the initial retry loop passes all quality gates.
    /// </summary>
    private async Task RunPostRetryCleanupAndFinalizeAsync(QualityGateContext context, CancellationToken linkedCt)
    {
        var run = context.Run;
        var config = context.Config;
        var callbacks = context.Callbacks;

        callbacks.TransitionTo(PipelineStep.PreparingForPullRequest);
        callbacks.EmitOutputLine("🧹 Preparing for pull request — running cleanup...");

        var cleanupPrompt = PromptBuilder.BuildCleanupPrompt();
        run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.System, Content = cleanupPrompt });
        callbacks.NotifyChange();

        try
        {
            var cleanupResult = await AgentPhaseExecutor.ExecuteAgentAndRecordAsync(
                new AgentExecutionRequest
                {
                    AgentProvider = context.AgentProvider,
                    Prompt = cleanupPrompt,
                    Run = run,
                    Config = config,
                    Description = "Pre-PR cleanup agent",
                    Logger = _logger,
                    EnvironmentVariables = context.InjectedSecrets
                },
                callbacks, linkedCt);

            if (cleanupResult != null)
                await _prOrchestrator.UpdateFileChangeStatsAsync(run, context.RepoProvider);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Pipeline {RunId} cleanup agent call failed, continuing to final quality gates", run.RunId);
            run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.System, Content = $"Agent error during cleanup: {ex.Message}" });
        }

        callbacks.EmitOutputLine("🏗️ Running final quality gates after cleanup...");
        callbacks.TransitionTo(PipelineStep.RunningQualityGates);
        var report = await RunQualityGateValidationAsync(context, run.WorkspacePath!, config, linkedCt);
        report = await AppendExternalCiIfNeededAsync(context, report, allowEmptyCommit: true, linkedCt, skipCiIfNoChanges: true);
        // TODO: [WARNING] ConflictRestart is missing from this guard — same bug class as #3045 (fixed in
        // ProceedToQualityGatesAsync) but on the RunPostRetryCleanupAndFinalizeAsync post-cleanup path.
        // If AppendExternalCiIfNeededAsync sets ConflictRestart here (conflicted PR detected on the final
        // quality gate pass after cleanup), execution falls through to RunRetryLoopAsync, the fix agent
        // is invoked, and run.RetryCount is incremented — wasting a retry slot on a branch GitHub cannot
        // build. Fix: add PipelineStep.ConflictRestart to this guard, matching the pattern used on lines
        // above (ProceedToQualityGatesAsync pre-retry guard) and below (post-RunRetryLoopAsync guard).
        if (run.CurrentStep is PipelineStep.Failed or PipelineStep.PrMerged or PipelineStep.PrClosed) return;

        LogAndRecordReport(context, report, "final quality gates");
        report = await RunRetryLoopAsync(context, report, "Final QG retry agent", linkedCt);
        if (run.CurrentStep is PipelineStep.Failed or PipelineStep.ConflictRestart
                or PipelineStep.PrMerged or PipelineStep.PrClosed) return;

        if (report.AllPassed)
        {
            await callbacks.FinalizePullRequest(run, false, linkedCt);

            // Wait for post-PR CI and handle retry/draft if it fails.
            // Extracted to keep RunPostRetryCleanupAndFinalizeAsync within complexity threshold.
            await HandlePostPrCiAsync(context, report, linkedCt);
        }
        else
            await FinalizeDraftPrAsync(context, run, report, "exhausted after cleanup", linkedCt);
    }

    /// <summary>
    /// Waits for post-PR CI after FinalizePullRequest and routes failures through the retry loop.
    /// Extracted from <see cref="RunPostRetryCleanupAndFinalizeAsync"/> to reduce cognitive complexity.
    /// </summary>
    private async Task HandlePostPrCiAsync(QualityGateContext context, QualityGateReport report, CancellationToken linkedCt)
    {
        var run = context.Run;
        report = await WaitForPostPrCiAsync(context, report, linkedCt);
        if (run.CurrentStep is PipelineStep.Failed or PipelineStep.ConflictRestart
                or PipelineStep.PrMerged or PipelineStep.PrClosed) return;

        if (!report.AllPassed)
        {
            report = await RunRetryLoopAsync(context, report, "Post-PR CI retry agent", linkedCt);
            if (run.CurrentStep is PipelineStep.Failed or PipelineStep.ConflictRestart
                    or PipelineStep.PrMerged or PipelineStep.PrClosed) return;

            if (!report.AllPassed)
                await FinalizeDraftPrAsync(context, run, report, "post-PR CI failed after retries", linkedCt);
        }
    }

    /// <summary>
    /// Polls external CI after the PR has been promoted to ready-for-review. This validates
    /// CI workflows that only trigger on <c>pull_request</c> events (not on branch pushes),
    /// which would not have been caught by the pre-PR <see cref="AppendExternalCiIfNeededAsync"/>
    /// call if that call exited early via the <c>skipCiIfNoChanges</c> path.
    /// Delegates to <see cref="CiPollingCoordinator.WaitForPostPrCiAsync"/> which owns the
    /// polling logic, telemetry recording, and InfrastructureRetryCount isolation.
    /// </summary>
    private Task<QualityGateReport> WaitForPostPrCiAsync(
        QualityGateContext context,
        QualityGateReport report,
        CancellationToken ct)
        => _ciPollingCoordinator.WaitForPostPrCiAsync(context, report, ct);

    /// <summary>
    /// Encapsulates the draft-PR finalization pattern: log a warning, emit a UI line,
    /// build and enqueue an error summary, collect failure feedback, then finalize as draft PR.
    /// </summary>
    private async Task FinalizeDraftPrAsync(
        QualityGateContext context,
        PipelineRun run,
        QualityGateReport report,
        string logContext,
        CancellationToken ct)
    {
        var config = context.Config;
        var callbacks = context.Callbacks;

        _logger.Warning("Pipeline {RunId} max retries ({MaxRetries}) {LogContext}, finalizing as draft PR",
            run.RunId, config.MaxRetries, logContext);
        callbacks.EmitOutputLine($"⚠️ Quality gates failed after {config.MaxRetries} retries, leaving PR as draft");

        var errorSummary = BuildQualityGateErrorSummary(report);
        run.RetryErrors.Enqueue(errorSummary);

        await CollectFailureFeedbackAsync(context, run, report, ct);

        // Set FailureCategory before FinalizePullRequest so that:
        // (a) FinalizePullRequest sees the correct FailureCategory if it reads run.FailureCategory,
        // (b) if FinalizePullRequest throws, the category is still recorded on the run object
        // and the metric will emit "quality_gate_exhausted" instead of "unknown".
        run.FailureCategory = FailureReason.QualityGateExhausted;
        await callbacks.FinalizePullRequest(run, true, ct);
    }

    /// <summary>
    /// Collects failure feedback from the agent after max retries are exhausted.
    /// This is a dedicated agent call that does NOT count against MaxRetries.
    /// Non-fatal: any exception or timeout produces a fallback feedback record.
    /// </summary>
    private async Task CollectFailureFeedbackAsync(
        QualityGateContext context,
        PipelineRun run,
        QualityGateReport latestReport,
        CancellationToken ct)
    {
        // TODO: The preamble output line is emitted unconditionally before CollectFeedbackCoreAsync is
        // awaited. If CollectFeedbackCoreAsync re-throws OperationCanceledException (pipeline cancellation),
        // the "📋 Collecting failure feedback..." line will already have been emitted. Previously this line
        // was inside the try block, so pre-condition failures would not have emitted it. This is a minor
        // cosmetic regression: the output line appears even when the operation is cancelled before it starts.
        context.Callbacks.EmitOutputLine("📋 Collecting failure feedback...");

        var issue = context.Issue ?? new IssueDetail
        {
            Identifier = run.IssueIdentifier,
            Title = run.IssueTitle,
            Description = "(Issue description not available)",
            Labels = []
        };

        await _feedbackService.CollectFeedbackCoreAsync(
            run,
            context.AgentProvider,
            _historyService,
            cats => FeedbackPromptBuilder.BuildFailureFeedbackPrompt(
                run, issue, latestReport, cats.HarnessCategories, cats.IssueCategories),
            FeedbackOutcome.Failure,
            context.Config.FeedbackTimeoutSeconds,
            ct,
            line => context.Callbacks.EmitOutputLine(line));

        // Log success after the call; run.Feedback is set by CollectFeedbackCoreAsync on the happy path.
        if (run.Feedback is not null)
            _logger.Information("Pipeline {RunId} failure feedback collected successfully. Category: {Category}",
                run.RunId, run.Feedback.Harness.Category ?? "(none)");
    }

    /// <summary>
    /// Encapsulates the shared retry pattern: execute agent → run QG validation → append external CI → check results.
    /// Returns the final <see cref="QualityGateReport"/> after all retries are exhausted or the report passes.
    /// </summary>
    /// <param name="context">The quality gate context containing run, config, callbacks, and providers.</param>
    /// <param name="initialReport">The report from the preceding QG validation run.</param>
    /// <param name="retryAgentDescription">Description prefix for the retry agent (used in logging and chat history).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The final quality gate report after retries.</returns>
    private async Task<QualityGateReport> RunRetryLoopAsync(
        QualityGateContext context,
        QualityGateReport initialReport,
        string retryAgentDescription,
        CancellationToken ct)
    {
        var run = context.Run;
        var config = context.Config;
        var callbacks = context.Callbacks;
        var report = initialReport;

        var consecutiveTransientRetries = 0;

        // NOTE: run.RetryErrors accumulates one entry per loop iteration (including transient
        // provider-error iterations where no fix was attempted). On repeated 429/503 responses, this
        // produces stale entries in the failure-feedback prompt and draft PR summary that were never
        // associated with actual fix attempts. Consider gating the enqueue on a "real work was done"
        // condition, or filtering stale entries before building the failure-feedback prompt.
        while (!report.AllPassed && run.RetryCount < config.MaxRetries)
        {
            // Compute the pending attempt number for logging/prompts without modifying run.RetryCount
            // yet.  run.RetryCount is only incremented inside RunFixAgentIterationAsync, on the
            // RetryOutcome.Retry (default) branch, so that it reflects the number of real fix-agent
            // attempts that actually ran rather than the number of loop iterations entered (which
            // includes transient and session-restart iterations that do not consume a retry budget slot).
            var pendingAttemptNum = run.RetryCount + 1;
            var errorSummary = BuildQualityGateErrorSummary(report);
            run.RetryErrors.Enqueue(errorSummary);

            _logger.Information("Pipeline {RunId} quality gates failed, auto-retry {RetryCount}/{MaxRetries}", run.RunId, pendingAttemptNum, config.MaxRetries);
            callbacks.EmitOutputLine($"🔄 Quality gates failed, retrying (attempt {pendingAttemptNum}/{config.MaxRetries})");

            // Short-circuit: CI-never-started exhaustion is an infrastructure failure, not a code problem.
            // The LLM cannot fix a missing CI trigger — break immediately so FinalizeDraftPrAsync is called
            // instead of wasting a retry budget slot on a pointless agent invocation.
            // TODO [WARNING] (Correctness): This PrMerged/PrClosed guard is defensive-redundant dead code.
            // AppendExternalCiIfNeededAsync already sets run.CurrentStep to PrMerged/PrClosed and every
            // call site immediately checks run.CurrentStep and returns before entering RunRetryLoopAsync.
            // The guard therefore never fires in practice — RunRetryLoopAsync is never entered with
            // CurrentStep already set to PrMerged or PrClosed. The active guard is the IsInfrastructureFailure
            // check below. Consider removing this guard or adding a comment that explains the defensive intent.
            if (run.CurrentStep is PipelineStep.PrMerged or PipelineStep.PrClosed)
                break;
            if (report.ExternalCi is { Passed: false, IsInfrastructureFailure: true })
            {
                _logger.Warning("Pipeline {RunId} CI-never-started infrastructure failure — not invoking LLM fix", run.RunId);
                callbacks.EmitOutputLine("❌ CI infrastructure failure (never started) — not retrying with LLM");
                break;
            }

            // NOTE [WARNING]: Two independent guards are combined here via OR to handle both the
            // multi-QGC case (where BuildAggregateReport only propagates the first failing QGC's
            // Tests flag) and the single-QGC path. The logic is correct but could become fragile
            // if BuildAggregateReport is changed. See review finding: Correctness WARNING.
            // NOTE [WARNING]: hasQualityGateOutput is derived solely from the infra-kill flag. However,
            // WriteGateOutput also skips writing files when stdout AND stderr are both empty regardless
            // of infra-kill classification. A non-infra failure with empty output would set
            // hasQualityGateOutput=true and direct the agent to an empty quality-gates directory.
            // Fix: propagate a "files were written" flag from WriteGateOutput into GateResult.
            // See review finding: Correctness WARNING — QualityGateExecutor.RetryLoop.cs
            // NOTE [WARNING]: This call site re-derives hasQualityGateOutput independently instead of using
            // the priorRetryErrors overload of BuildQualityGateRetryPrompt. As a result, the prior-attempt
            // history section is never emitted in the main retry loop. If this omission is intentional
            // (history section was noisy), document it; if accidental, switch to the priorRetryErrors
            // overload and pass run.RetryErrors.ToArray().
            // See review finding: DotNetSpecialist WARNING — QualityGateExecutor.RetryLoop.cs:457
            var retryPromptSummary = BuildQualityGateRetryPrompt(report, pendingAttemptNum, config.MaxRetries,
                hasQualityGateOutput: !(report.QgcResults.Any(r => r.Tests?.IsInfrastructureFailure == true)
                    || report.Tests?.IsInfrastructureFailure == true));

            run.ChatHistory.Enqueue(new ChatEntry
            {
                Role = ChatRole.System,
                Content = retryPromptSummary
            });

            callbacks.TransitionTo(PipelineStep.GeneratingCode);

            var fixPrompt = $"{retryPromptSummary}\n\n{PipelineConstants.GitRestrictionShort}";
            run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.System, Content = fixPrompt });
            callbacks.NotifyChange();

            // Run the fix agent and classify the result; extracted to reduce cognitive complexity.
            // RetryDecision carries the control-flow intent and updated counters.
            var decision = await RunFixAgentIterationAsync(
                context, fixPrompt, retryAgentDescription, pendingAttemptNum, consecutiveTransientRetries, ct);
            consecutiveTransientRetries = decision.ConsecutiveTransientRetries;

            if (decision.ShouldContinue) continue;
            if (decision.ShouldBreak) break;

            callbacks.TransitionTo(PipelineStep.RunningQualityGates);
            report = await RunQualityGateValidationAsync(context, run.WorkspacePath!, config, ct);

            report = await AppendExternalCiIfNeededAsync(context, report, allowEmptyCommit: true, ct);
            if (run.CurrentStep is PipelineStep.Failed or PipelineStep.ConflictRestart
                    or PipelineStep.PrMerged or PipelineStep.PrClosed) return report;

            LogAndRecordReport(context, report, "retry quality gates");
        }

        return report;
    }

    /// <summary>
    /// Executes one fix-agent invocation inside the retry loop, classifies the result, and
    /// dispatches to the appropriate named handler. Returns a <see cref="RetryDecision"/>
    /// carrying the control-flow intent (<see cref="RetryDecision.ShouldBreak"/> /
    /// <see cref="RetryDecision.ShouldContinue"/>), the updated consecutive-transient counter,
    /// and the retry-count delta to apply. Extracted from <see cref="RunRetryLoopAsync"/> to
    /// reduce cognitive complexity.
    /// </summary>
    private async Task<RetryDecision> RunFixAgentIterationAsync(
        QualityGateContext context,
        string fixPrompt,
        string retryAgentDescription,
        int pendingAttemptNum,
        int consecutiveTransientRetries,
        CancellationToken ct)
    {
        var run = context.Run;
        var config = context.Config;
        var callbacks = context.Callbacks;

        try
        {
            var agentResult = await AgentPhaseExecutor.ExecuteAgentAndRecordAsync(
                new AgentExecutionRequest
                {
                    AgentProvider = context.AgentProvider,
                    Prompt = fixPrompt,
                    Run = run,
                    Config = config,
                    Description = $"{retryAgentDescription} (attempt {pendingAttemptNum})",
                    Logger = _logger,
                    Phase = null,
                    EnvironmentVariables = context.InjectedSecrets
                    // NOTE [WARNING]: StallMetrics was removed from this AgentExecutionRequest.
                    // The StallMetrics field is nullable, so passing null is safe and won't NRE
                    // in the stall monitor. However, stall events during QGC retry agent calls
                    // will no longer be recorded. If stall monitoring is still active elsewhere,
                    // verify this is intentional.
                    // See review finding: Correctness WARNING — QualityGateExecutor.RetryLoop.cs:481
                },
                callbacks, ct,
                resumeSessionId: run.CodegenSessionId);

            // Intentional asymmetry: RetryErrors (incremented above) is NOT rolled back —
            // the RetryErrors entry (from the prior QG failure) is harmless noise. Only
            // RetryCount matters for loop exit logic, so that is the only value that must be
            // accurate.  run.RetryCount is mutated once here — after the handler returns —
            // via decision.RetryCountDelta, so that all four outcome paths share a single
            // mutation site rather than incrementing independently.
            var outcome = ClassifyRetryOutcome(agentResult);
            var decision = outcome switch
            {
                RetryOutcome.TransientWait => await HandleTransientAsync(run, config, consecutiveTransientRetries, ct),
                RetryOutcome.AbortAuth => await HandleAuthAbortAsync(run),
                RetryOutcome.RestartSession => await HandleSessionRestartAsync(run),
                _ => await HandleDefaultRetryAsync(run, agentResult, context.RepoProvider)
            };
            run.RetryCount += decision.RetryCountDelta;
            return decision;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // NOTE: This catch block is a defensive guard. In the current implementation,
            // ExecuteAgentAndRecordAsync absorbs all non-cancellation exceptions and returns null,
            // so exceptions cannot propagate to here from the agent call. If that contract is ever
            // broken, or if code is added between the agent call and the switch in the future,
            // this catch provides a safety net.
            //
            // The outcome is classified as OutcomeTransient because: (a) ExecuteAgentAndRecordAsync
            // returning null (the absorbed-exception path) maps to ClassifyRetryOutcome(null) →
            // RetryOutcome.TransientWait → OutcomeTransient; this catch must be consistent with
            // that contract so dashboards see the same dimension regardless of whether the exception
            // is absorbed upstream or propagates here.
            //
            // IMPORTANT: This catch block must NOT delegate to HandleTransientAsync. HandleTransientAsync
            // returns ShouldContinue: true (skip QG validation), but the catch path must return
            // ShouldContinue: false (proceed to QG validation). The telemetry call and asymmetric
            // ConsecutiveTransientRetries treatment (no increment in the catch path) are intentional.
            //
            // NOTE: If ExecuteAgentAndRecordAsync's exception-absorption contract changes, revisit
            // whether ShouldBreak/ShouldContinue semantics here still match TransientWait.
            _logger.Warning(ex, "Pipeline {RunId} retry fix agent call failed", run.RunId);
            run.ChatHistory.Enqueue(new ChatEntry
            {
                Role = ChatRole.System,
                Content = $"Agent error during retry fix: {ex.Message}"
            });
            _qualityGateRetries.Add(1, BuildRetryTags(run, OutcomeTransient));
            return new RetryDecision(
                ShouldBreak: false,
                ShouldContinue: false,
                ConsecutiveTransientRetries: consecutiveTransientRetries,
                RetryCountDelta: 0);
        }
    }

    /// <summary>
    /// Handles a <see cref="RetryOutcome.TransientWait"/> iteration. Increments the consecutive
    /// transient counter and either breaks the loop (cap reached) or delays and continues.
    /// Does not consume a retry-budget slot (<c>RetryCountDelta: 0</c>).
    /// </summary>
    private async Task<RetryDecision> HandleTransientAsync(
        PipelineRun run,
        PipelineConfiguration config,
        int consecutiveTransientRetries,
        CancellationToken ct)
    {
        _qualityGateRetries.Add(1, BuildRetryTags(run, OutcomeTransient));
        consecutiveTransientRetries++;

        if (consecutiveTransientRetries >= MaxConsecutiveTransientRetries)
        {
            _logger.Warning(
                "Pipeline {RunId} retry {RetryCount}: reached consecutive transient error cap " +
                "({Cap} consecutive transient responses), breaking retry loop",
                run.RunId, run.RetryCount, MaxConsecutiveTransientRetries);
            return new RetryDecision(
                ShouldBreak: true,
                ShouldContinue: false,
                ConsecutiveTransientRetries: consecutiveTransientRetries,
                RetryCountDelta: 0);
        }

        _logger.Warning(
            "Pipeline {RunId} retry {RetryCount}: transient agent result, " +
            "not consuming retry budget, waiting before next attempt " +
            "({Consecutive}/{Cap} consecutive transient retries)",
            run.RunId, run.RetryCount,
            consecutiveTransientRetries, MaxConsecutiveTransientRetries);
        await Task.Delay(config.TransientRetryDelay, ct);
        return new RetryDecision(
            ShouldBreak: false,
            ShouldContinue: true,
            ConsecutiveTransientRetries: consecutiveTransientRetries,
            RetryCountDelta: 0);
    }

    /// <summary>
    /// Handles a <see cref="RetryOutcome.AbortAuth"/> iteration. Breaks the loop immediately.
    /// Carries <c>RetryCountDelta: 1</c> to preserve the current behavior of incrementing
    /// <c>run.RetryCount</c> on auth failures.
    /// </summary>
    /// <remarks>
    /// NOTE: Incrementing run.RetryCount here on AbortAuth is semantically inconsistent
    /// with the stated design intent ("only increment after a real fix-agent attempt runs"). Auth
    /// failures are permanent errors that immediately break the loop — they are not genuine fix
    /// attempts that consumed a retry-budget slot. If run.RetryCount is inspected after the loop
    /// (e.g., to cap draft PR descriptions or display attempt counts), AbortAuth artificially
    /// inflates the counter by 1. Consider changing RetryCountDelta to 0 (matching the
    /// TransientWait / RestartSession treatment) in a separate issue.
    /// See review finding: Correctness WARNING — QualityGateExecutor.RetryLoop.cs:549
    /// </remarks>
    private Task<RetryDecision> HandleAuthAbortAsync(PipelineRun run)
    {
        _qualityGateRetries.Add(1, BuildRetryTags(run, OutcomeAuthAbort));
        _logger.Error(
            "Pipeline {RunId} retry {RetryCount}: permanent auth failure, aborting retry loop",
            run.RunId, run.RetryCount);
        return Task.FromResult(new RetryDecision(
            ShouldBreak: true,
            ShouldContinue: false,
            // NOTE: ConsecutiveTransientRetries is reset to 0 here rather than passed
            // through unchanged. Since ShouldBreak: true causes the loop to exit immediately,
            // the value is never read again in the current code (the caller assigns it to its
            // loop variable, then immediately breaks). However, the behavior-neutral choice
            // matching the original code is to return the accumulated value unchanged. If a
            // future caller inspects the counter after a break (e.g., for telemetry or error
            // reporting), it will silently see 0 instead of the real count.
            // See review findings: Correctness WARNING and DotNetSpecialist WARNING.
            ConsecutiveTransientRetries: 0,
            RetryCountDelta: 1));
    }

    /// <summary>
    /// Handles a <see cref="RetryOutcome.RestartSession"/> iteration. Clears
    /// <c>run.CodegenSessionId</c> and continues the loop without running QG validation.
    /// Does not consume a retry-budget slot (<c>RetryCountDelta: 0</c>).
    /// </summary>
    /// <remarks>
    /// NOTE: RestartSession does not increment run.RetryCount and has no cap analogous
    /// to MaxConsecutiveTransientRetries. If the fix agent repeatedly returns zero tokens, the
    /// outer while loop never advances run.RetryCount and runs indefinitely (until cancellation).
    /// Add a consecutive RestartSession cap or increment run.RetryCount here to bound the loop.
    /// See review finding: DotNetSpecialist WARNING — QualityGateExecutor.RetryLoop.cs:556
    /// </remarks>
    private Task<RetryDecision> HandleSessionRestartAsync(PipelineRun run)
    {
        _qualityGateRetries.Add(1, BuildRetryTags(run, OutcomeSessionRestart));
        _logger.Warning(
            "Pipeline {RunId} retry {RetryCount}: agent returned empty response (0 tokens), " +
            "clearing session affinity for next attempt",
            run.RunId, run.RetryCount);
        run.CodegenSessionId = null;
        return Task.FromResult(new RetryDecision(
            ShouldBreak: false,
            ShouldContinue: true,
            // NOTE: ConsecutiveTransientRetries is reset to 0 here, but the original
            // code returned the counter unchanged on the RestartSession path. This means that
            // interleaved TransientWait / RestartSession sequences can never accumulate enough
            // consecutive transient responses to fire MaxConsecutiveTransientRetries: after each
            // TransientWait increments the counter, the next RestartSession silently resets it to
            // 0, preventing the cap from ever being reached. Concrete scenario: alternating
            // RestartSession / TransientWait indefinitely keeps the counter at 0 or 1 and the
            // loop runs until CancellationToken fires. Consider passing the accumulated value
            // through unchanged, or document the reset as intentional.
            // See review findings: Correctness WARNING and DotNetSpecialist WARNING.
            ConsecutiveTransientRetries: 0,
            RetryCountDelta: 0));
    }

    /// <summary>
    /// Handles the default <see cref="RetryOutcome.Retry"/> iteration. The fix agent produced
    /// real code changes — proceed to QG validation and consume a retry-budget slot
    /// (<c>RetryCountDelta: 1</c>). Resets the consecutive-transient counter to 0.
    /// </summary>
    private async Task<RetryDecision> HandleDefaultRetryAsync(
        PipelineRun run,
        AgentResult? agentResult,
        IRepositoryProvider repoProvider)
    {
        _qualityGateRetries.Add(1, BuildRetryTags(run, OutcomeRetry));
        if (agentResult != null)
            await _prOrchestrator.UpdateFileChangeStatsAsync(run, repoProvider);
        return new RetryDecision(
            ShouldBreak: false,
            ShouldContinue: false,
            ConsecutiveTransientRetries: 0,
            RetryCountDelta: 1);
    }

    /// <summary>
    /// Classifies a single retry-loop agent result into a discrete <see cref="RetryOutcome"/>.
    /// Pure function — no I/O, no side effects.
    /// </summary>
    /// <param name="agentResult">
    /// The result returned by <see cref="AgentPhaseExecutor.ExecuteAgentAndRecordAsync"/>, or
    /// <see langword="null"/> when that method absorbed a non-cancellation exception. A null result
    /// is treated as <see cref="RetryOutcome.TransientWait"/> so the consecutive-transient cap can
    /// fire for exception-surfaced provider failures, not just <see cref="AgentErrorCategory"/>-based ones.
    /// </param>
    internal static RetryOutcome ClassifyRetryOutcome(AgentResult? agentResult)
    {
        // null means ExecuteAgentAndRecordAsync absorbed a non-cancellation exception.
        // Treat as transient so the consecutive counter increments and the cap can fire.
        // NOTE: All absorbed exceptions are classified as transient — this is slightly broad
        // (also catches programming errors like NullReferenceException), but the 10-iteration
        // cap bounds the blast radius and the simplicity outweighs the risk.
        if (agentResult is null)
            return RetryOutcome.TransientWait;

        if (agentResult.ErrorCategory is AgentErrorCategory.ProviderRateLimit
            or AgentErrorCategory.ProviderOverload)
            return RetryOutcome.TransientWait;

        if (agentResult.ErrorCategory == AgentErrorCategory.PermanentAuthFailure)
            return RetryOutcome.AbortAuth;

        // Dead/exhausted session: agent returned successfully but produced nothing.
        if (agentResult is { ExitCode: 0 } &&
            agentResult.Usage?.TotalTokens == 0 &&
            agentResult.OutputLines.Count == 0)
            return RetryOutcome.RestartSession;

        return RetryOutcome.Retry;
    }

    /// <summary>
    /// Logs quality gate results and records the report in the run's history.
    /// </summary>
    private void LogAndRecordReport(QualityGateContext context, QualityGateReport report, string phase)
    {
        var run = context.Run;
        var callbacks = context.Callbacks;

        run.LatestQualityReport = report;
        run.QualityGateHistory.Enqueue(report);
        callbacks.EmitOutputLine(PipelineFormatting.FormatQualityGateSummary(report));

        _logger.Information("Pipeline {RunId} {Phase}: AllPassed={AllPassed}, Compilation={CompilationPassed}, Tests={TestsPassed}, ExternalCi={ExternalCiResult}",
            run.RunId, phase, report.AllPassed, report.Compilation.Passed, FormatGateLogValue(report.Tests),
            FormatGateLogValue(report.ExternalCi));

        EmitGateEvaluation(PipelineTelemetry.QualityGateNames.Compilation, report.Compilation.Passed);
        if (report.Tests is not null)
            EmitGateEvaluation(PipelineTelemetry.QualityGateNames.Tests, report.Tests.Passed);
        if (report.ExternalCi is not null)
            EmitGateEvaluation(PipelineTelemetry.QualityGateNames.ExternalCi, report.ExternalCi.Passed);

        void EmitGateEvaluation(string gateName, bool passed)
        {
            _qualityGateEvaluations.Add(1,
                new("gate_name", gateName), new("result", passed ? "pass" : "fail"));
        }
    }
}
