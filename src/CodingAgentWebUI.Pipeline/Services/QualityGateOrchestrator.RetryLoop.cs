using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

internal partial class QualityGateOrchestrator
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
                    var cleanupResult = await AgentExecutionOrchestrator.ExecuteAgentAndRecordAsync(
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
                    await callbacks.CreatePullRequest(run, report, false, linkedCt);
                }
                else
                {
                    _logger.Warning("Pipeline {RunId} max retries ({MaxRetries}) exhausted after cleanup, creating draft PR",
                        run.RunId, config.Retry.MaxRetries);
                    callbacks.EmitOutputLine($"⚠️ Quality gates failed after {config.Retry.MaxRetries} retries, creating draft PR");

                    var finalErrorSummary = BuildQualityGateErrorSummary(report);
                    run.RetryErrors.Add(finalErrorSummary);

                    await callbacks.CreatePullRequest(run, report, true, linkedCt);
                }
            }
            else
            {
                _logger.Warning("Pipeline {RunId} max retries ({MaxRetries}) exhausted, creating draft PR",
                    run.RunId, config.Retry.MaxRetries);
                callbacks.EmitOutputLine($"⚠️ Quality gates failed after {config.Retry.MaxRetries} retries, creating draft PR");

                var errorSummary = BuildQualityGateErrorSummary(report);
                run.RetryErrors.Add(errorSummary);

                await callbacks.CreatePullRequest(run, report, true, linkedCt);
            }
        }
        catch (OperationCanceledException)
        {
            if (run.CurrentStep is not (PipelineStep.Cancelled or PipelineStep.Failed))
            {
                _logger.Information("Pipeline {RunId} was cancelled during quality gates", run.RunId);
                run.CompletedAt = DateTime.UtcNow;
                await callbacks.RemoveAllAgentLabels(run.IssueIdentifier, CancellationToken.None);
                callbacks.EmitOutputLine("🚫 Pipeline cancelled");
                callbacks.TransitionTo(PipelineStep.Cancelled);
                callbacks.AddRunToHistory(run);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Pipeline {RunId} quality gate validation failed", run.RunId);
            run.FailureReason = $"Quality gate validation error: {ex.Message}";
            await context.IssueOps.SwapLabelAsync(run.IssueIdentifier, AgentLabels.Error, CancellationToken.None);
            callbacks.EmitOutputLine($"❌ Pipeline failed: {run.FailureReason}");
            callbacks.TransitionTo(PipelineStep.Failed);
            callbacks.AddRunToHistory(run);
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

        while (!report.AllPassed && run.RetryCount < config.Retry.MaxRetries)
        {
            run.RetryCount++;
            var errorSummary = BuildQualityGateErrorSummary(report);
            run.RetryErrors.Add(errorSummary);

            _logger.Information("Pipeline {RunId} quality gates failed, auto-retry {RetryCount}/{MaxRetries}",
                run.RunId, run.RetryCount, config.Retry.MaxRetries);
            callbacks.EmitOutputLine($"🔄 Quality gates failed, retrying (attempt {run.RetryCount}/{config.Retry.MaxRetries})");

            var retryPromptSummary = BuildQualityGateRetryPrompt(report, run.RetryCount, config.Retry.MaxRetries);

            run.ChatHistory.Enqueue(new ChatEntry
            {
                Role = ChatRole.System,
                Content = retryPromptSummary
            });

            callbacks.TransitionTo(PipelineStep.GeneratingCode);

            var fixPrompt = $"{retryPromptSummary}\n\nDo NOT run git write commands (git add, git commit, git push, etc.). The pipeline handles version control automatically.";
            run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.System, Content = fixPrompt });
            callbacks.NotifyChange();

            try
            {
                var agentResult = await AgentExecutionOrchestrator.ExecuteAgentAndRecordAsync(
                    context.AgentProvider, fixPrompt, run, config,
                    $"{retryAgentDescription} (attempt {run.RetryCount})",
                    callbacks, _logger, ct);

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
    }
}
