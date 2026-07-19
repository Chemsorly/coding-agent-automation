using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Prompts;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services;

public partial class QualityGateExecutor
{
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

        // TODO: Consider checking ct.ThrowIfCancellationRequested() before starting stopwatch to avoid recording near-zero durations on pre-cancelled tokens
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

            // Initial retry loop
            report = await RunRetryLoopAsync(context, report, "Quality gate retry agent", linkedCt);
            if (run.CurrentStep == PipelineStep.Failed) return;

            if (report.AllPassed)
            {
                // Cleanup step: ask the agent to clean up before PR creation
                callbacks.TransitionTo(PipelineStep.PreparingForPullRequest);
                callbacks.EmitOutputLine("🧹 Preparing for pull request — running cleanup...");
                _logger.Information("Pipeline {RunId} quality gates passed, entering PreparingForPullRequest cleanup step", run.RunId);

                var cleanupPrompt = PromptBuilder.BuildCleanupPrompt();
                run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.System, Content = cleanupPrompt });
                callbacks.NotifyChange();

                try
                {
                    var cleanupResult = await AgentPhaseExecutor.ExecuteAgentAndRecordAsync(
                        context.AgentProvider, cleanupPrompt, run, config,
                        "Pre-PR cleanup agent",
                        callbacks, _logger, linkedCt);

                    if (cleanupResult != null)
                        await _prOrchestrator.UpdateFileChangeStatsAsync(run, context.RepoProvider);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.Warning(ex, "Pipeline {RunId} cleanup agent call failed, continuing to final quality gates", run.RunId);
                    run.ChatHistory.Enqueue(new ChatEntry
                    {
                        Role = ChatRole.System,
                        Content = $"Agent error during cleanup: {ex.Message}"
                    });
                }

                // Final quality gate run after cleanup
                callbacks.EmitOutputLine("🏗️ Running final quality gates after cleanup...");
                _logger.Information("Pipeline {RunId} running final quality gates after cleanup", run.RunId);
                callbacks.TransitionTo(PipelineStep.RunningQualityGates);
                report = await RunQualityGateValidationAsync(context, run.WorkspacePath!, config, linkedCt);

                report = await AppendExternalCiIfNeededAsync(context, report, allowEmptyCommit: true, linkedCt,
                    skipCiIfNoChanges: true);
                if (run.CurrentStep == PipelineStep.Failed) return;

                LogAndRecordReport(context, report, "final quality gates");

                // If final gates fail, re-enter the retry loop using the existing retry budget
                report = await RunRetryLoopAsync(context, report, "Final QG retry agent", linkedCt);
                if (run.CurrentStep == PipelineStep.Failed) return;

                if (report.AllPassed)
                {
                    await callbacks.FinalizePullRequest(run, report, false, linkedCt);
                }
                else
                {
                    await FinalizeDraftPrAsync(context, run, report, "exhausted after cleanup", linkedCt);
                }
            }
            else
            {
                await FinalizeDraftPrAsync(context, run, report, "exhausted", linkedCt);
            }
        }
        catch (OperationCanceledException)
        {
            if (run.CurrentStep is not (PipelineStep.Cancelled or PipelineStep.Failed))
            {
                _logger.Information("Pipeline {RunId} was cancelled during quality gates", run.RunId);
                run.MarkCompleted();
                await callbacks.SwapAgentLabel(run.IssueIdentifier.Value, AgentLabels.Cancelled, CancellationToken.None);
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
            await context.IssueOps.SwapLabelAsync(run.IssueIdentifier.Value, AgentLabels.Error, CancellationToken.None);
            callbacks.EmitOutputLine($"❌ Pipeline failed: {run.FailureReason}");
            callbacks.TransitionTo(PipelineStep.Failed);
            await callbacks.AddRunToHistoryAsync(run);
        }
        finally
        {
            PipelineTelemetry.QualityGateDuration.Record(
                qgStopwatch.Elapsed.TotalSeconds,
                PipelineTelemetry.BuildTags(run.RunType, run.ProjectId, run.ProjectName));
        }
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
            _logger.Information("Pipeline {RunId} collecting failure feedback from agent", run.RunId);
            context.Callbacks.EmitOutputLine("📋 Collecting failure feedback...");

            // Load distinct categories from recent run summaries
            var (harnessCategories, issueCategories) = await _feedbackService.LoadPreviousCategoriesAsync(_historyService).ConfigureAwait(false); // TODO: Propagate CancellationToken ct to LoadPreviousCategoriesAsync

            // Build the issue detail for the prompt (use context issue or create a minimal one from run data)
            var issue = context.Issue ?? new IssueDetail
            {
                Identifier = run.IssueIdentifier.Value,
                Title = run.IssueTitle,
                Description = "(Issue description not available)",
                Labels = []
            };

            // Build the failure feedback prompt
            var feedbackPrompt = FeedbackPromptBuilder.BuildFailureFeedbackPrompt(
                run, issue, latestReport, harnessCategories, issueCategories);

            // Execute agent with UseResume = true and 60-second timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(FeedbackConstraints.FailureFeedbackTimeoutSeconds));

            var agentResult = await context.AgentProvider.ExecuteAsync(
                new AgentRequest
                {
                    Prompt = feedbackPrompt,
                    WorkspacePath = run.WorkspacePath!,
                    Timeout = TimeSpan.FromSeconds(FeedbackConstraints.FailureFeedbackTimeoutSeconds),
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
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout on the feedback call itself (not pipeline cancellation)
            _logger.Warning("Pipeline {RunId} failure feedback collection timed out after {Timeout}s",
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

        while (!report.AllPassed && run.RetryCount < config.MaxRetries)
        {
            run.RetryCount++;
            // TODO: Consider using BuildTags (run_type + project_id + project_name) for dimensional consistency with duration metrics
            PipelineTelemetry.QualityGateRetries.Add(1, PipelineTelemetry.RunTypeTag(run.RunType));
            var errorSummary = BuildQualityGateErrorSummary(report);
            run.RetryErrors.Enqueue(errorSummary);

            _logger.Information("Pipeline {RunId} quality gates failed, auto-retry {RetryCount}/{MaxRetries}",
                run.RunId, run.RetryCount, config.MaxRetries);
            callbacks.EmitOutputLine($"🔄 Quality gates failed, retrying (attempt {run.RetryCount}/{config.MaxRetries})");

            var retryPromptSummary = BuildQualityGateRetryPrompt(report, run.RetryCount, config.MaxRetries);

            run.ChatHistory.Enqueue(new ChatEntry
            {
                Role = ChatRole.System,
                Content = retryPromptSummary
            });

            callbacks.TransitionTo(PipelineStep.GeneratingCode);

            var fixPrompt = $"{retryPromptSummary}\n\n{PipelineConstants.GitRestrictionShort}";
            run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.System, Content = fixPrompt });
            callbacks.NotifyChange();

            try
            {
                var agentResult = await AgentPhaseExecutor.ExecuteAgentAndRecordAsync(
                    context.AgentProvider, fixPrompt, run, config,
                    $"{retryAgentDescription} (attempt {run.RetryCount})",
                    callbacks, _logger, ct,
                    resumeSessionId: run.CodegenSessionId);

                // Detect dead/exhausted session: agent returned successfully but produced nothing.
                // This typically means the session's context window overflowed and the provider
                // returned an empty response. Clear session affinity so the next retry uses a fresh session.
                if (agentResult is { ExitCode: 0 } && agentResult.Usage?.TotalTokens == 0 && agentResult.OutputLines.Count == 0)
                {
                    _logger.Warning("Pipeline {RunId} retry {RetryCount}: agent returned empty response (0 tokens), " +
                                    "clearing session affinity for next attempt", run.RunId, run.RetryCount);
                    run.CodegenSessionId = null;
                    continue; // Skip QG validation — workspace unchanged, go straight to next retry
                }

                if (agentResult != null)
                    await _prOrchestrator.UpdateFileChangeStatsAsync(run, context.RepoProvider);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Pipeline {RunId} retry fix agent call failed", run.RunId);
                run.ChatHistory.Enqueue(new ChatEntry
                {
                    Role = ChatRole.System,
                    Content = $"Agent error during retry fix: {ex.Message}"
                });
            }

            callbacks.TransitionTo(PipelineStep.RunningQualityGates);
            report = await RunQualityGateValidationAsync(context, run.WorkspacePath!, config, ct);

            report = await AppendExternalCiIfNeededAsync(context, report, allowEmptyCommit: true, ct);
            if (run.CurrentStep == PipelineStep.Failed) return report;

            LogAndRecordReport(context, report, "retry quality gates");
        }

        return report;
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

        _logger.Information("Pipeline {RunId} {Phase}: AllPassed={AllPassed}, Compilation={CompilationPassed}, Tests={TestsPassed}, Coverage={CoverageResult}, SecurityScan={SecurityResult}, ExternalCi={ExternalCiResult}",
            run.RunId, phase, report.AllPassed, report.Compilation.Passed, report.Tests.Passed,
            FormatCoverageLogValue(report.Coverage), FormatGateLogValue(report.SecurityScan), FormatGateLogValue(report.ExternalCi));

        EmitGateEvaluation(PipelineTelemetry.QualityGateNames.Compilation, report.Compilation.Passed);
        EmitGateEvaluation(PipelineTelemetry.QualityGateNames.Tests, report.Tests.Passed);
        if (report.Coverage is not null)
            EmitGateEvaluation(PipelineTelemetry.QualityGateNames.Coverage, report.Coverage.Passed);
        if (report.SecurityScan is not null)
            EmitGateEvaluation(PipelineTelemetry.QualityGateNames.Security, report.SecurityScan.Passed);
        if (report.ExternalCi is not null)
            EmitGateEvaluation(PipelineTelemetry.QualityGateNames.ExternalCi, report.ExternalCi.Passed);

        static void EmitGateEvaluation(string gateName, bool passed)
        {
            PipelineTelemetry.QualityGateEvaluations.Add(1,
                new("gate_name", gateName), new("result", passed ? "pass" : "fail"));
        }
    }
}
