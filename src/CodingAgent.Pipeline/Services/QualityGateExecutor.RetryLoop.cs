using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services.Prompts;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services;

public partial class QualityGateExecutor
{
    /// <summary>Maximum consecutive transient provider errors before the retry loop is aborted.</summary>
    private const int MaxConsecutiveTransientRetries = 10;

    /// <summary>Prefix used for the post-PR CI gate result Details and UI messages.</summary>
    private const string PostPrCiPrefix = "Post-PR CI";

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
            if (run.CurrentStep == PipelineStep.Failed) return;

            LogAndRecordReport(context, report, "quality gates");

            report = await RunRetryLoopAsync(context, report, "Quality gate retry agent", linkedCt);
            if (run.CurrentStep is PipelineStep.Failed or PipelineStep.ConflictRestart) return;

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
                run.MarkCompleted();
                await callbacks.SwapAgentLabel(run.IssueIdentifier, AgentLabels.Cancelled, CancellationToken.None);
                callbacks.EmitOutputLine("🚫 Pipeline cancelled");
                callbacks.TransitionTo(PipelineStep.Cancelled);
                await callbacks.AddRunToHistoryAsync(run);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Pipeline {RunId} quality gate validation failed", run.RunId);
            run.FailureReason = $"Quality gate validation error: {ex.Message}";
            _logger.Information(
                "Pipeline {RunId} QualityGateExecutor swapping label to agent:error for issue {IssueIdentifier} (reason=quality gate validation error)",
                run.RunId, run.IssueIdentifier);
            await context.IssueOps.SwapLabelAsync(run.IssueIdentifier, AgentLabels.Error, CancellationToken.None);
            callbacks.EmitOutputLine($"❌ Pipeline failed: {run.FailureReason}");
            callbacks.TransitionTo(PipelineStep.Failed);
            await callbacks.AddRunToHistoryAsync(run);
        }
        finally
        {
            _qualityGateDuration.Record(
                qgStopwatch.Elapsed.TotalSeconds,
                PipelineTelemetry.BuildTags(run.RunType, run.ProjectId, run.ProjectName));
        }
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
        if (run.CurrentStep == PipelineStep.Failed) return;

        LogAndRecordReport(context, report, "final quality gates");
        report = await RunRetryLoopAsync(context, report, "Final QG retry agent", linkedCt);
        if (run.CurrentStep is PipelineStep.Failed or PipelineStep.ConflictRestart) return;

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
        if (run.CurrentStep is PipelineStep.Failed or PipelineStep.ConflictRestart) return;

        if (!report.AllPassed)
        {
            report = await RunRetryLoopAsync(context, report, "Post-PR CI retry agent", linkedCt);
            if (run.CurrentStep is PipelineStep.Failed or PipelineStep.ConflictRestart) return;

            if (!report.AllPassed)
                await FinalizeDraftPrAsync(context, run, report, "post-PR CI failed after retries", linkedCt);
        }
    }

    /// <summary>
    /// Polls external CI after the PR has been promoted to ready-for-review. This validates
    /// CI workflows that only trigger on <c>pull_request</c> events (not on branch pushes),
    /// which would not have been caught by the pre-PR <see cref="AppendExternalCiIfNeededAsync"/>
    /// call if that call exited early via the <c>skipCiIfNoChanges</c> path.
    /// </summary>
    /// <remarks>
    /// Returns the original <paramref name="report"/> with <see cref="QualityGateReport.ExternalCi"/>
    /// replaced by the post-PR CI result. Returns the unchanged report (with no <c>ExternalCi</c>
    /// mutation) when <see cref="QualityGateContext.PipelineProvider"/> is null or
    /// <see cref="PipelineRun.BranchName"/> is empty — both are treated as "CI not configured".
    /// </remarks>
    private async Task<QualityGateReport> WaitForPostPrCiAsync(
        QualityGateContext context,
        QualityGateReport report,
        CancellationToken ct)
    {
        var run = context.Run;
        var config = context.Config;
        var callbacks = context.Callbacks;

        if (context.PipelineProvider is null || string.IsNullOrEmpty(run.BranchName))
            return report;

        _logger.Information("Pipeline {RunId} waiting for post-PR CI on branch {BranchName}", run.RunId, run.BranchName);
        callbacks.EmitOutputLine("⏳ Waiting for post-PR CI...");

        var commitSha = await TryReadHeadShaAsync(context, "could not read HEAD SHA for post-PR CI wait", ct);

        // Snapshot and reset InfrastructureRetryCount so post-PR CI gets its own fresh budget.
        // The pre-PR CI poll (AppendExternalCiIfNeededAsync) may have consumed some or all of
        // MaxInfrastructureRetries. Without a reset, a single infra failure here would exhaust
        // the remaining budget and skip retries, degrading to draft PR unnecessarily.
        var priorInfraRetryCount = run.InfrastructureRetryCount;
        run.InfrastructureRetryCount = 0;

        // Tracks the entire post-PR CI wait including polling and infra retries, so that
        // pipeline_step_duration_seconds{step_name="WaitForPostPrCi"} accounts for time that
        // would otherwise be invisible in the "Avg Step Duration" Grafana panel.
        var waitSw = System.Diagnostics.Stopwatch.StartNew();
        GateResult ciGate;
        try
        {
            try
            {
                var ciPollStopwatch = System.Diagnostics.Stopwatch.StartNew();
                var (ciPassed, ciStatus, ciLogPaths) = await PollAndHandleInfraRetryAsync(context, commitSha, config, callbacks, ct);

                // PostPrCiDuration is a dedicated histogram for the post-PR CI wait, separate from
                // ExternalCiDuration (which is recorded in AppendExternalCiIfNeededAsync for the
                // pre-PR CI pass). Using a distinct metric avoids inflating pre-PR p50/p99 with
                // post-PR observations and makes the per-phase time budget observable in Grafana.
                _postPrCiDuration.Record(
                    ciPollStopwatch.Elapsed.TotalSeconds,
                    PipelineTelemetry.BuildTags(run.RunType, run.ProjectId, run.ProjectName));

                ciGate = BuildCiGateResult(ciPassed, ciStatus, ciLogPaths, PostPrCiPrefix, PostPrCiPrefix, callbacks);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                ciGate = BuildCiTimeoutGateResult(config.ExternalCiTimeout, PostPrCiPrefix);
                callbacks.EmitOutputLine($"❌ {PostPrCiPrefix} timed out after {config.ExternalCiTimeout}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Pipeline {RunId} post-PR CI check failed, treating as gate failure", run.RunId);
                ciGate = BuildCiErrorGateResult(PostPrCiPrefix, ex.Message);
            }
            finally
            {
                // Restore the accumulated count so the run summary reflects the total infra retries
                // across both pre-PR and post-PR CI polls.
                run.InfrastructureRetryCount += priorInfraRetryCount;
            }
        }
        finally
        {
            waitSw.Stop();
            // Guard against genuine pipeline cancellation (ct.IsCancellationRequested).
            // When the outer ct is cancelled the inner catch(OperationCanceledException){ throw; }
            // re-throws and this finally still runs.  Recording a partial elapsed time as a
            // complete WaitForPostPrCi observation would distort p50/p99 histogram aggregations
            // for long CI waits (potentially hours).  Only emit the sample when the wait
            // completed — successfully or with a CI-level error — not when the pipeline itself
            // was cancelled mid-poll.
            if (!ct.IsCancellationRequested)
            {
                var stepTags = PipelineTelemetry.BuildStepTags("WaitForPostPrCi", run.RunType, run.ProjectId, run.ProjectName);
                _stepDuration.Record(waitSw.Elapsed.TotalSeconds, stepTags);
                _stepCount.Add(1, stepTags);
            }
        }

        return new QualityGateReport
        {
            Compilation = report.Compilation,
            Tests = report.Tests,
            ExternalCi = ciGate
        };
    }

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
        try
        {
            context.Callbacks.EmitOutputLine("📋 Collecting failure feedback...");

            // Load distinct categories from recent run summaries
            var (harnessCategories, issueCategories) = await _feedbackService.LoadPreviousCategoriesAsync(_historyService, ct).ConfigureAwait(false);

            // Build the issue detail for the prompt (use context issue or create a minimal one from run data)
            var issue = context.Issue ?? new IssueDetail
            {
                Identifier = run.IssueIdentifier,
                Title = run.IssueTitle,
                Description = "(Issue description not available)",
                Labels = []
            };

            // Build the failure feedback prompt
            var feedbackPrompt = FeedbackPromptBuilder.BuildFailureFeedbackPrompt(
                run, issue, latestReport, harnessCategories, issueCategories);

            // Execute agent with UseResume = true and operator-configured timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(context.Config.FeedbackTimeoutSeconds));

            var agentResult = await context.AgentProvider.ExecuteAsync(
                new AgentRequest
                {
                    Prompt = feedbackPrompt,
                    WorkspacePath = run.WorkspacePath!,
                    Timeout = TimeSpan.FromSeconds(context.Config.FeedbackTimeoutSeconds),
                    UseResume = true
                },
                timeoutCts.Token,
                line => context.Callbacks.EmitOutputLine(line));

            // Parse the response
            var responseText = string.Join("\n", agentResult.OutputLines);
            var feedback = _feedbackService.ParseFeedbackFromResponse(responseText, FeedbackOutcome.Failure, DateTime.UtcNow);
            run.Feedback = feedback;

            _logger.Information("Pipeline {RunId} failure feedback collected successfully. Category: {Category}",
                run.RunId, feedback.Harness.Category ?? "(none)");
        }
        catch (OperationCanceledException ex) when (!ct.IsCancellationRequested)
        {
            // Timeout on the feedback call itself (not pipeline cancellation)
            _logger.Warning(ex, "Pipeline {RunId} failure feedback collection timed out after {Timeout}s",
                run.RunId, context.Config.FeedbackTimeoutSeconds);
            run.Feedback = _feedbackService.CreateFallbackFeedback(
                FeedbackOutcome.Failure, "Feedback collection timed out", DateTime.UtcNow);
        }
        catch (OperationCanceledException)
        {
            // Pipeline-level cancellation — re-throw to let the outer handler deal with it
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Pipeline {RunId} failure feedback collection failed", run.RunId);
            run.Feedback = _feedbackService.CreateFallbackFeedback(
                FeedbackOutcome.Failure, $"Feedback collection failed: {ex.Message}", DateTime.UtcNow);
        }
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
            var (shouldBreak, shouldContinue, updatedTransientCount) = await RunFixAgentIterationAsync(
                context, fixPrompt, retryAgentDescription, pendingAttemptNum, consecutiveTransientRetries, ct);
            consecutiveTransientRetries = updatedTransientCount;

            if (shouldContinue) continue;
            if (shouldBreak) break;

            callbacks.TransitionTo(PipelineStep.RunningQualityGates);
            report = await RunQualityGateValidationAsync(context, run.WorkspacePath!, config, ct);

            report = await AppendExternalCiIfNeededAsync(context, report, allowEmptyCommit: true, ct);
            if (run.CurrentStep is PipelineStep.Failed or PipelineStep.ConflictRestart) return report;

            LogAndRecordReport(context, report, "retry quality gates");
        }

        return report;
    }

    /// <summary>
    /// Executes one fix-agent invocation inside the retry loop and classifies the result.
    /// Returns <c>(shouldBreak: true, shouldContinue: false)</c> to exit the loop,
    /// <c>(false, shouldContinue: true)</c> to continue to the next iteration without running
    /// quality gates, or <c>(false, false)</c> to proceed with quality gate validation.
    /// Extracted from <see cref="RunRetryLoopAsync"/> to reduce cognitive complexity.
    /// </summary>
    private async Task<(bool ShouldBreak, bool ShouldContinue, int ConsecutiveTransientRetries)> RunFixAgentIterationAsync(
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
            // accurate.  run.RetryCount is incremented here — on the Retry (default) outcome
            // only — so that it reflects the number of real fix-agent attempts that completed,
            // not the number of loop iterations entered.
            switch (ClassifyRetryOutcome(agentResult))
            {
                case RetryOutcome.TransientWait:
                    _qualityGateRetries.Add(1, BuildRetryTags(run, OutcomeTransient));
                    // No increment: transient iterations don't consume a retry-budget slot.
                    consecutiveTransientRetries++;
                    if (consecutiveTransientRetries >= MaxConsecutiveTransientRetries)
                    {
                        _logger.Warning(
                            "Pipeline {RunId} retry {RetryCount}: reached consecutive transient error cap " +
                            "({Cap} consecutive transient responses), breaking retry loop",
                            run.RunId, run.RetryCount, MaxConsecutiveTransientRetries);
                        return (ShouldBreak: true, ShouldContinue: false, consecutiveTransientRetries);
                    }
                    _logger.Warning(
                        "Pipeline {RunId} retry {RetryCount}: transient agent result, " +
                        "not consuming retry budget, waiting before next attempt " +
                        "({Consecutive}/{Cap} consecutive transient retries)",
                        run.RunId, run.RetryCount,
                        consecutiveTransientRetries, MaxConsecutiveTransientRetries);
                    await Task.Delay(config.TransientRetryDelay, ct);
                    return (ShouldBreak: false, ShouldContinue: true, consecutiveTransientRetries);

                case RetryOutcome.AbortAuth:
                    _qualityGateRetries.Add(1, BuildRetryTags(run, OutcomeAuthAbort));
                    // TODO [WARNING]: Incrementing run.RetryCount here on AbortAuth is semantically
                    // inconsistent with the stated design intent ("only increment after a real fix-agent
                    // attempt runs"). Auth failures are permanent errors that immediately break the loop —
                    // they are not genuine fix attempts that consumed a retry-budget slot. If run.RetryCount
                    // is inspected after the loop (e.g., to cap draft PR descriptions or display attempt
                    // counts), AbortAuth artificially inflates the counter by 1. Consider NOT incrementing
                    // here (matching the TransientWait / RestartSession treatment) or documenting that
                    // AbortAuth is intentionally counted as a consumed slot.
                    // See review finding: Correctness WARNING — QualityGateExecutor.RetryLoop.cs:549
                    run.RetryCount++;
                    _logger.Error(
                        "Pipeline {RunId} retry {RetryCount}: permanent auth failure, aborting retry loop",
                        run.RunId, run.RetryCount);
                    return (ShouldBreak: true, ShouldContinue: false, consecutiveTransientRetries);

                case RetryOutcome.RestartSession:
                    _qualityGateRetries.Add(1, BuildRetryTags(run, OutcomeSessionRestart));
                    // TODO [WARNING]: RestartSession does not increment run.RetryCount and has no
                    // cap analogous to MaxConsecutiveTransientRetries. The outer while loop exits only
                    // when run.RetryCount >= config.MaxRetries; if the fix agent repeatedly returns
                    // zero tokens (triggering RestartSession on every iteration), run.RetryCount never
                    // advances and the loop runs indefinitely (until cancellation). Before this refactor,
                    // the top-of-loop run.RetryCount++ ensured every RestartSession iteration still
                    // consumed a retry-budget slot. Add a consecutive RestartSession cap (similar to
                    // MaxConsecutiveTransientRetries) or increment run.RetryCount here.
                    // See review finding: DotNetSpecialist WARNING — QualityGateExecutor.RetryLoop.cs:556
                    _logger.Warning(
                        "Pipeline {RunId} retry {RetryCount}: agent returned empty response (0 tokens), " +
                        "clearing session affinity for next attempt",
                        run.RunId, run.RetryCount);
                    run.CodegenSessionId = null;
                    return (ShouldBreak: false, ShouldContinue: true, consecutiveTransientRetries);

                default: // RetryOutcome.Retry
                    _qualityGateRetries.Add(1, BuildRetryTags(run, OutcomeRetry));
                    // Increment only here: the fix agent actually ran and produced code changes.
                    run.RetryCount++;
                    consecutiveTransientRetries = 0;
                    if (agentResult != null)
                        await _prOrchestrator.UpdateFileChangeStatsAsync(run, context.RepoProvider);
                    return (ShouldBreak: false, ShouldContinue: false, consecutiveTransientRetries);
            }
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
            // TODO: If ExecuteAgentAndRecordAsync's exception-absorption contract changes, revisit
            // whether ShouldBreak/ShouldContinue semantics here still match TransientWait.
            _logger.Warning(ex, "Pipeline {RunId} retry fix agent call failed", run.RunId);
            run.ChatHistory.Enqueue(new ChatEntry
            {
                Role = ChatRole.System,
                Content = $"Agent error during retry fix: {ex.Message}"
            });
            _qualityGateRetries.Add(1, BuildRetryTags(run, OutcomeTransient));
            return (ShouldBreak: false, ShouldContinue: false, consecutiveTransientRetries);
        }
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
