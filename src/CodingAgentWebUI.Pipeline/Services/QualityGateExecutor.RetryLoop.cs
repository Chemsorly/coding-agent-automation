using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Prompts;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services;

public partial class QualityGateExecutor
{
    /// <summary>Maximum consecutive transient provider errors before the retry loop is aborted.</summary>
    private const int MaxConsecutiveTransientRetries = 10;

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
            if (run.CurrentStep == PipelineStep.Failed) return;

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
        if (run.CurrentStep == PipelineStep.Failed) return;

        if (report.AllPassed)
        {
            await callbacks.FinalizePullRequest(run, report, false, linkedCt);

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
        if (run.CurrentStep == PipelineStep.Failed) return;

        if (!report.AllPassed)
        {
            report = await RunRetryLoopAsync(context, report, "Post-PR CI retry agent", linkedCt);
            if (run.CurrentStep == PipelineStep.Failed) return;

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

        string? commitSha = null;
        try { commitSha = await context.RepoProvider.GetHeadCommitShaAsync(run.WorkspacePath!, ct); }
        catch (Exception ex) { _logger.Debug(ex, "Pipeline {RunId} could not read HEAD SHA for post-PR CI wait", run.RunId); }

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

                ciGate = new GateResult
                {
                    GateName = "External CI",
                    Passed = ciPassed,
                    Details = ciPassed
                        ? $"Post-PR CI passed. {ciStatus.Jobs.Count} job(s) completed."
                        : QualityGateValidator.BuildCiFailureDetails(ciStatus, ciLogPaths)
                };

                callbacks.EmitOutputLine(ciPassed
                    ? $"✅ Post-PR CI passed ({ciStatus.Jobs.Count} jobs)"
                    : $"❌ Post-PR CI failed: {ciGate.Details}");
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                ciGate = new GateResult
                {
                    GateName = "External CI",
                    Passed = false,
                    Details = $"Post-PR CI timed out after {config.ExternalCiTimeout}"
                };
                callbacks.EmitOutputLine($"❌ Post-PR CI timed out after {config.ExternalCiTimeout}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Pipeline {RunId} post-PR CI check failed, treating as gate failure", run.RunId);
                ciGate = new GateResult
                {
                    GateName = "External CI",
                    Passed = false,
                    Details = $"Post-PR CI error: {ex.Message}"
                };
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
            var stepTags = PipelineTelemetry.BuildStepTags("WaitForPostPrCi", run.RunType, run.ProjectId, run.ProjectName);
            // TODO: This finally block fires on genuine OperationCanceledException (ct.IsCancellationRequested).
            // The inner catch (OperationCanceledException) { throw; } re-throws and the outer finally still
            // executes, recording a partial elapsed time as a complete WaitForPostPrCi observation and
            // incrementing the step count. For long CI waits (potentially hours) a cancellation mid-poll
            // produces an unrealistically short sample that will distort p50/p99 histogram aggregations.
            // Consider guarding with: if (!ct.IsCancellationRequested) { ... Record/Add ... }
            _stepDuration.Record(waitSw.Elapsed.TotalSeconds, stepTags);
            _stepCount.Add(1, stepTags);
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
        await callbacks.FinalizePullRequest(run, report, true, ct);
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
            // NOTE [WARNING]: FeedbackConstraints.FailureFeedbackTimeoutSeconds is used here but the actual
            // CancellationTokenSource was created with context.Config.FeedbackTimeoutSeconds (above).
            // If the operator configures a non-default FeedbackTimeoutSeconds the logged value will
            // be wrong, making log-based diagnosis misleading. Change to context.Config.FeedbackTimeoutSeconds.
            _logger.Warning(ex, "Pipeline {RunId} failure feedback collection timed out after {Timeout}s",
                run.RunId, FeedbackConstraints.FailureFeedbackTimeoutSeconds);
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
            run.RetryCount++;
            // NOTE: Consider using BuildTags (run_type + project_id + project_name) for dimensional consistency with duration metrics.
            // NOTE [WARNING]: The per-outcome dimension tag (transient/auth_abort/session_restart/retry) was
            // removed when this counter was moved to the top of the loop. Dashboards or alerts keyed on
            // outcome=transient or outcome=auth_abort will silently receive zero counts. The replacement
            // uses only RunTypeTag. Restore BuildRetryTags with an outcome dimension or add a separate
            // counter per outcome branch to preserve metric dimensionality.
            // See review finding: Correctness WARNING — QualityGateExecutor.RetryLoop.cs:368-375
            // NOTE [WARNING]: This counter is now incremented before the agent runs. If the loop exits
            // immediately after (e.g. shouldBreak set by the switch then the break fires),
            // the counter is incremented for an attempt that was not executed. Minor double-count risk.
            // See review finding: Correctness WARNING — QualityGateExecutor.RetryLoop.cs:368
            _qualityGateRetries.Add(1, PipelineTelemetry.RunTypeTag(run.RunType));
            var errorSummary = BuildQualityGateErrorSummary(report);
            run.RetryErrors.Enqueue(errorSummary);

            _logger.Information("Pipeline {RunId} quality gates failed, auto-retry {RetryCount}/{MaxRetries}", run.RunId, run.RetryCount, config.MaxRetries);
            callbacks.EmitOutputLine($"🔄 Quality gates failed, retrying (attempt {run.RetryCount}/{config.MaxRetries})");

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
            var retryPromptSummary = BuildQualityGateRetryPrompt(report, run.RetryCount, config.MaxRetries,
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
                context, fixPrompt, retryAgentDescription, consecutiveTransientRetries, ct);
            consecutiveTransientRetries = updatedTransientCount;

            if (shouldContinue) continue;
            if (shouldBreak) break;

            callbacks.TransitionTo(PipelineStep.RunningQualityGates);
            report = await RunQualityGateValidationAsync(context, run.WorkspacePath!, config, ct);

            report = await AppendExternalCiIfNeededAsync(context, report, allowEmptyCommit: true, ct);
            if (run.CurrentStep == PipelineStep.Failed) return report;
            // NOTE [WARNING]: The ConflictRestart early-return guard that previously appeared here
            // was removed. The original comment described an active bug: a ConflictRestart detected
            // inside AppendExternalCiIfNeededAsync during a retry iteration could cause
            // AddRunToHistoryAsync to be called twice. Removing the guard changes runtime behavior:
            // ConflictRestart now falls through to LogAndRecordReport and continues the retry loop.
            // Verify this is intentional (e.g., ConflictRestart is now handled at a higher level).
            // See review finding: Correctness WARNING — QualityGateExecutor.RetryLoop.cs:582

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
                    Description = $"{retryAgentDescription} (attempt {run.RetryCount})",
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

            // Intentional asymmetry: PipelineTelemetry and RetryErrors (incremented above) are NOT
            // rolled back — rolling back a monotonic counter is non-idiomatic in OpenTelemetry, and the
            // RetryErrors entry (from the prior QG failure) is harmless noise. Only RetryCount matters
            // for loop exit logic, so that is the only value corrected.
            switch (ClassifyRetryOutcome(agentResult))
            {
                case RetryOutcome.TransientWait:
                    run.RetryCount = Math.Max(0, run.RetryCount - 1);
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
                    _logger.Error(
                        "Pipeline {RunId} retry {RetryCount}: permanent auth failure, aborting retry loop",
                        run.RunId, run.RetryCount);
                    return (ShouldBreak: true, ShouldContinue: false, consecutiveTransientRetries);

                case RetryOutcome.RestartSession:
                    _logger.Warning(
                        "Pipeline {RunId} retry {RetryCount}: agent returned empty response (0 tokens), " +
                        "clearing session affinity for next attempt",
                        run.RunId, run.RetryCount);
                    run.CodegenSessionId = null;
                    return (ShouldBreak: false, ShouldContinue: true, consecutiveTransientRetries);

                default: // RetryOutcome.Retry
                    consecutiveTransientRetries = 0;
                    if (agentResult != null)
                        await _prOrchestrator.UpdateFileChangeStatsAsync(run, context.RepoProvider);
                    return (ShouldBreak: false, ShouldContinue: false, consecutiveTransientRetries);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // NOTE: [WARNING] This catch depends on the contract that ExecuteAgentAndRecordAsync
            // absorbs all non-cancellation agent exceptions and returns null. If that contract
            // is ever broken, an exception could be silently consumed here and the transient
            // counter would not increment.
            _logger.Warning(ex, "Pipeline {RunId} retry fix agent call failed", run.RunId);
            run.ChatHistory.Enqueue(new ChatEntry
            {
                Role = ChatRole.System,
                Content = $"Agent error during retry fix: {ex.Message}"
            });
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

        // TODO [WARNING]: report.Tests is dereferenced without a null-conditional in the log call and
        // EmitGateEvaluation below. A QGC configured with only a BuildCommand (no TestCommand) produces
        // a report where Tests is null, which will throw NullReferenceException here. Apply the same
        // null-conditional guard used for ExternalCi (null check before EmitGateEvaluation).
        // See review finding: DotNetSpecialist WARNING — QualityGateExecutor.RetryLoop.cs LogAndRecordReport
        _logger.Information("Pipeline {RunId} {Phase}: AllPassed={AllPassed}, Compilation={CompilationPassed}, Tests={TestsPassed}, ExternalCi={ExternalCiResult}",
            run.RunId, phase, report.AllPassed, report.Compilation.Passed, report.Tests.Passed,
            FormatGateLogValue(report.ExternalCi));

        EmitGateEvaluation(PipelineTelemetry.QualityGateNames.Compilation, report.Compilation.Passed);
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
