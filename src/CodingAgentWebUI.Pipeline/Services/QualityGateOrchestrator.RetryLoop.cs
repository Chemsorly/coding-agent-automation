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
        var agentProvider = context.AgentProvider;
        var repoProvider = context.RepoProvider;
        var pipelineProvider = context.PipelineProvider;
        var orchestratorCts = context.OrchestratorCts;
        var issueOps = context.IssueOps;
        var callbacks = context.Callbacks;
        callbacks.TransitionTo(PipelineStep.RunningQualityGates);

        try
        {
            using var linkedCts = orchestratorCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(ct, orchestratorCts.Token)
                : null;
            var linkedCt = linkedCts?.Token ?? ct;

            callbacks.EmitOutputLine("🏗️ Running quality gates...");
            var report = await RunQualityGateValidationAsync(context, run.WorkspacePath!, config, linkedCt);

            report = await AppendExternalCiIfNeededAsync(context, report, allowEmptyCommit: false, linkedCt);
            if (run.CurrentStep == PipelineStep.Failed) return;

            run.LatestQualityReport = report;
            run.QualityGateHistory.Enqueue(report);
            callbacks.EmitOutputLine(PipelineFormatting.FormatQualityGateSummary(report));

            _logger.Information("Pipeline {RunId} quality gates: AllPassed={AllPassed}, Compilation={CompilationPassed}, Tests={TestsPassed}, Coverage={CoverageResult}, SecurityScan={SecurityResult}, ExternalCi={ExternalCiResult}",
                run.RunId, report.AllPassed, report.Compilation.Passed, report.Tests.Passed,
                FormatCoverageLogValue(report.Coverage), FormatGateLogValue(report.SecurityScan), FormatGateLogValue(report.ExternalCi));

            while (!report.AllPassed && run.RetryCount < config.MaxRetries)
            {
                run.RetryCount++;
                var errorSummary = BuildQualityGateErrorSummary(report);
                run.RetryErrors.Add(errorSummary);

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

                var fixPrompt = $"{retryPromptSummary}\n\nDo NOT run git write commands (git add, git commit, git push, etc.). The pipeline handles version control automatically.";
                run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.System, Content = fixPrompt });
                callbacks.NotifyChange();

                try
                {
                    var agentResult = await AgentStallMonitor.ExecuteWithMonitoringAsync(
                        agentProvider,
                        new AgentRequest
                        {
                            Prompt = fixPrompt,
                            WorkspacePath = run.WorkspacePath!,
                            Timeout = config.AgentTimeout,
                            UseResume = true
                        },
                        run, config, $"Quality gate retry agent (attempt {run.RetryCount})", callbacks.NotifyChange, _logger, linkedCt,
                        line =>
                        {
                            run.OutputLines.Enqueue(line);
                            callbacks.EmitOutputLine(line);
                        });

                    var outputSummary = agentResult.OutputLines.Count > 0
                        ? string.Join(Environment.NewLine, agentResult.OutputLines.TakeLast(10))
                        : "(no output)";
                    run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.Agent, Content = outputSummary });

                    await _prOrchestrator.UpdateFileChangeStatsAsync(run, repoProvider);
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
                report = await RunQualityGateValidationAsync(context, run.WorkspacePath!, config, linkedCt);

                report = await AppendExternalCiIfNeededAsync(context, report, allowEmptyCommit: true, linkedCt);
                if (run.CurrentStep == PipelineStep.Failed) return;

                run.LatestQualityReport = report;
                run.QualityGateHistory.Enqueue(report);
                callbacks.EmitOutputLine(PipelineFormatting.FormatQualityGateSummary(report));

                _logger.Information("Pipeline {RunId} retry quality gates: AllPassed={AllPassed}, Compilation={CompilationPassed}, Tests={TestsPassed}, Coverage={CoverageResult}, SecurityScan={SecurityResult}, ExternalCi={ExternalCiResult}",
                    run.RunId, report.AllPassed, report.Compilation.Passed, report.Tests.Passed,
                    FormatCoverageLogValue(report.Coverage), FormatGateLogValue(report.SecurityScan), FormatGateLogValue(report.ExternalCi));
            }

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
                    var cleanupResult = await AgentStallMonitor.ExecuteWithMonitoringAsync(
                        agentProvider,
                        new AgentRequest
                        {
                            Prompt = cleanupPrompt,
                            WorkspacePath = run.WorkspacePath!,
                            Timeout = config.AgentTimeout,
                            UseResume = true
                        },
                        run, config, "Pre-PR cleanup agent", callbacks.NotifyChange, _logger, linkedCt,
                        line =>
                        {
                            run.OutputLines.Enqueue(line);
                            callbacks.EmitOutputLine(line);
                        });

                    var outputSummary = cleanupResult.OutputLines.Count > 0
                        ? string.Join(Environment.NewLine, cleanupResult.OutputLines.TakeLast(10))
                        : "(no output)";
                    run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.Agent, Content = outputSummary });

                    await _prOrchestrator.UpdateFileChangeStatsAsync(run, repoProvider);
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

                run.LatestQualityReport = report;
                run.QualityGateHistory.Enqueue(report);
                callbacks.EmitOutputLine(PipelineFormatting.FormatQualityGateSummary(report));

                _logger.Information("Pipeline {RunId} final quality gates: AllPassed={AllPassed}, Compilation={CompilationPassed}, Tests={TestsPassed}, Coverage={CoverageResult}, SecurityScan={SecurityResult}, ExternalCi={ExternalCiResult}",
                    run.RunId, report.AllPassed, report.Compilation.Passed, report.Tests.Passed,
                    FormatCoverageLogValue(report.Coverage), FormatGateLogValue(report.SecurityScan), FormatGateLogValue(report.ExternalCi));

                // If final gates fail, re-enter the retry loop using the existing retry budget
                while (!report.AllPassed && run.RetryCount < config.MaxRetries)
                {
                    run.RetryCount++;
                    var errorSummary = BuildQualityGateErrorSummary(report);
                    run.RetryErrors.Add(errorSummary);

                    _logger.Information("Pipeline {RunId} final quality gates failed, auto-retry {RetryCount}/{MaxRetries}",
                        run.RunId, run.RetryCount, config.MaxRetries);
                    callbacks.EmitOutputLine($"🔄 Final quality gates failed, retrying (attempt {run.RetryCount}/{config.MaxRetries})");

                    var retryPromptSummary = BuildQualityGateRetryPrompt(report, run.RetryCount, config.MaxRetries);

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
                        var agentResult = await AgentStallMonitor.ExecuteWithMonitoringAsync(
                            agentProvider,
                            new AgentRequest
                            {
                                Prompt = fixPrompt,
                                WorkspacePath = run.WorkspacePath!,
                                Timeout = config.AgentTimeout,
                                UseResume = true
                            },
                            run, config, $"Final QG retry agent (attempt {run.RetryCount})", callbacks.NotifyChange, _logger, linkedCt,
                            line =>
                            {
                                run.OutputLines.Enqueue(line);
                                callbacks.EmitOutputLine(line);
                            });

                        var outputSummary = agentResult.OutputLines.Count > 0
                            ? string.Join(Environment.NewLine, agentResult.OutputLines.TakeLast(10))
                            : "(no output)";
                        run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.Agent, Content = outputSummary });

                        await _prOrchestrator.UpdateFileChangeStatsAsync(run, repoProvider);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.Warning(ex, "Pipeline {RunId} final QG retry fix agent call failed", run.RunId);
                        run.ChatHistory.Enqueue(new ChatEntry
                        {
                            Role = ChatRole.System,
                            Content = $"Agent error during retry fix: {ex.Message}"
                        });
                    }

                    callbacks.TransitionTo(PipelineStep.RunningQualityGates);
                    report = await RunQualityGateValidationAsync(context, run.WorkspacePath!, config, linkedCt);

                    report = await AppendExternalCiIfNeededAsync(context, report, allowEmptyCommit: true, linkedCt);
                    if (run.CurrentStep == PipelineStep.Failed) return;

                    run.LatestQualityReport = report;
                    run.QualityGateHistory.Enqueue(report);
                    callbacks.EmitOutputLine(PipelineFormatting.FormatQualityGateSummary(report));

                    _logger.Information("Pipeline {RunId} final QG retry quality gates: AllPassed={AllPassed}, Compilation={CompilationPassed}, Tests={TestsPassed}, Coverage={CoverageResult}, SecurityScan={SecurityResult}, ExternalCi={ExternalCiResult}",
                        run.RunId, report.AllPassed, report.Compilation.Passed, report.Tests.Passed,
                        FormatCoverageLogValue(report.Coverage), FormatGateLogValue(report.SecurityScan), FormatGateLogValue(report.ExternalCi));
                }

                if (report.AllPassed)
                {
                    await callbacks.CreatePullRequest(run, report, false, linkedCt);
                }
                else
                {
                    _logger.Warning("Pipeline {RunId} max retries ({MaxRetries}) exhausted after cleanup, creating draft PR",
                        run.RunId, config.MaxRetries);
                    callbacks.EmitOutputLine($"⚠️ Quality gates failed after {config.MaxRetries} retries, creating draft PR");

                    var finalErrorSummary = BuildQualityGateErrorSummary(report);
                    run.RetryErrors.Add(finalErrorSummary);

                    await callbacks.CreatePullRequest(run, report, true, linkedCt);
                }
            }
            else
            {
                _logger.Warning("Pipeline {RunId} max retries ({MaxRetries}) exhausted, creating draft PR",
                    run.RunId, config.MaxRetries);
                callbacks.EmitOutputLine($"⚠️ Quality gates failed after {config.MaxRetries} retries, creating draft PR");

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
            await issueOps.SwapLabelAsync(run.IssueIdentifier, AgentLabels.Error, CancellationToken.None);
            callbacks.EmitOutputLine($"❌ Pipeline failed: {run.FailureReason}");
            callbacks.TransitionTo(PipelineStep.Failed);
            callbacks.AddRunToHistory(run);
        }
    }
}
