using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Handles quality gate validation with retry logic and external CI integration.
/// Extracted from PipelineOrchestrationService.
/// </summary>
internal class QualityGateOrchestrator
{
    private readonly IQualityGateValidator _qualityGateValidator;
    private readonly CiLogWriter _ciLogWriter;
    private readonly PullRequestOrchestrator _prOrchestrator;
    private readonly Serilog.ILogger _logger;

    public QualityGateOrchestrator(
        IQualityGateValidator qualityGateValidator,
        CiLogWriter ciLogWriter,
        PullRequestOrchestrator prOrchestrator,
        Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(qualityGateValidator);
        ArgumentNullException.ThrowIfNull(ciLogWriter);
        ArgumentNullException.ThrowIfNull(prOrchestrator);
        ArgumentNullException.ThrowIfNull(logger);

        _qualityGateValidator = qualityGateValidator;
        _ciLogWriter = ciLogWriter;
        _prOrchestrator = prOrchestrator;
        _logger = logger;
    }

    /// <summary>
    /// Runs quality gate validation with retry logic and PR creation.
    /// </summary>
    // TODO: [ARC-10] Add ArgumentNullException.ThrowIfNull for public method parameters
    public async Task ProceedToQualityGatesAsync(
        PipelineRun run, PipelineConfiguration config,
        IAgentProvider agentProvider,
        IRepositoryProvider repoProvider,
        IPipelineProvider? pipelineProvider,
        CancellationTokenSource? orchestratorCts,
        Action<PipelineStep> transitionTo,
        IAgentIssueOperations issueOps,
        Func<string, CancellationToken, Task> removeAllAgentLabels,
        Action<PipelineRun> addRunToHistory,
        Action<string> onOutputLine, Action onChange,
        Func<PipelineRun, QualityGateReport, bool, CancellationToken, Task> createPullRequest,
        CancellationToken ct)
    {
        transitionTo(PipelineStep.RunningQualityGates);

        try
        {
            using var linkedCts = orchestratorCts != null
                ? CancellationTokenSource.CreateLinkedTokenSource(ct, orchestratorCts.Token)
                : null;
            var linkedCt = linkedCts?.Token ?? ct;

            onOutputLine("🏗️ Running quality gates...");
            var report = await _qualityGateValidator.ValidateAsync(run.WorkspacePath!, config, linkedCt);

            report = await AppendExternalCiIfNeededAsync(run, report, config, repoProvider, pipelineProvider,
                transitionTo, issueOps, addRunToHistory, onChange, onOutputLine, allowEmptyCommit: false, linkedCt);
            if (run.CurrentStep == PipelineStep.Failed) return;

            run.LatestQualityReport = report;
            run.QualityGateHistory.Enqueue(report);
            onOutputLine(PipelineFormatting.FormatQualityGateSummary(report));

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
                onOutputLine($"🔄 Quality gates failed, retrying (attempt {run.RetryCount}/{config.MaxRetries})");

                var retryPromptSummary = BuildQualityGateRetryPrompt(report, run.RetryCount, config.MaxRetries);

                run.ChatHistory.Enqueue(new ChatEntry
                {
                    Role = ChatRole.System,
                    Content = retryPromptSummary
                });

                transitionTo(PipelineStep.GeneratingCode);

                var fixPrompt = $"{retryPromptSummary}\n\nDo NOT run git write commands (git add, git commit, git push, etc.). The pipeline handles version control automatically.";
                run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.System, Content = fixPrompt });
                onChange();

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
                        run, config, $"Quality gate retry agent (attempt {run.RetryCount})", onChange, _logger, linkedCt,
                        line =>
                        {
                            run.OutputLines.Enqueue(line);
                            onOutputLine(line);
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

                transitionTo(PipelineStep.RunningQualityGates);
                report = await _qualityGateValidator.ValidateAsync(run.WorkspacePath!, config, linkedCt);

                report = await AppendExternalCiIfNeededAsync(run, report, config, repoProvider, pipelineProvider,
                    transitionTo, issueOps, addRunToHistory, onChange, onOutputLine, allowEmptyCommit: true, linkedCt);
                if (run.CurrentStep == PipelineStep.Failed) return;

                run.LatestQualityReport = report;
                run.QualityGateHistory.Enqueue(report);
                onOutputLine(PipelineFormatting.FormatQualityGateSummary(report));

                _logger.Information("Pipeline {RunId} retry quality gates: AllPassed={AllPassed}, Compilation={CompilationPassed}, Tests={TestsPassed}, Coverage={CoverageResult}, SecurityScan={SecurityResult}, ExternalCi={ExternalCiResult}",
                    run.RunId, report.AllPassed, report.Compilation.Passed, report.Tests.Passed,
                    FormatCoverageLogValue(report.Coverage), FormatGateLogValue(report.SecurityScan), FormatGateLogValue(report.ExternalCi));
            }

            if (report.AllPassed)
            {
                // Cleanup step: ask the agent to clean up before PR creation
                transitionTo(PipelineStep.PreparingForPullRequest);
                onOutputLine("🧹 Preparing for pull request — running cleanup...");
                _logger.Information("Pipeline {RunId} quality gates passed, entering PreparingForPullRequest cleanup step", run.RunId);

                var cleanupPrompt = PromptBuilder.BuildCleanupPrompt();
                run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.System, Content = cleanupPrompt });
                onChange();

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
                        run, config, "Pre-PR cleanup agent", onChange, _logger, linkedCt,
                        line =>
                        {
                            run.OutputLines.Enqueue(line);
                            onOutputLine(line);
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
                onOutputLine("🏗️ Running final quality gates after cleanup...");
                _logger.Information("Pipeline {RunId} running final quality gates after cleanup", run.RunId);
                transitionTo(PipelineStep.RunningQualityGates);
                report = await _qualityGateValidator.ValidateAsync(run.WorkspacePath!, config, linkedCt);

                report = await AppendExternalCiIfNeededAsync(run, report, config, repoProvider, pipelineProvider,
                    transitionTo, issueOps, addRunToHistory, onChange, onOutputLine, allowEmptyCommit: true, linkedCt,
                    skipCiIfNoChanges: true);
                if (run.CurrentStep == PipelineStep.Failed) return;

                run.LatestQualityReport = report;
                run.QualityGateHistory.Enqueue(report);
                onOutputLine(PipelineFormatting.FormatQualityGateSummary(report));

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
                    onOutputLine($"🔄 Final quality gates failed, retrying (attempt {run.RetryCount}/{config.MaxRetries})");

                    var retryPromptSummary = BuildQualityGateRetryPrompt(report, run.RetryCount, config.MaxRetries);

                    run.ChatHistory.Enqueue(new ChatEntry
                    {
                        Role = ChatRole.System,
                        Content = retryPromptSummary
                    });

                    transitionTo(PipelineStep.GeneratingCode);

                    var fixPrompt = $"{retryPromptSummary}\n\nDo NOT run git write commands (git add, git commit, git push, etc.). The pipeline handles version control automatically.";
                    run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.System, Content = fixPrompt });
                    onChange();

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
                            run, config, $"Final QG retry agent (attempt {run.RetryCount})", onChange, _logger, linkedCt,
                            line =>
                            {
                                run.OutputLines.Enqueue(line);
                                onOutputLine(line);
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

                    transitionTo(PipelineStep.RunningQualityGates);
                    report = await _qualityGateValidator.ValidateAsync(run.WorkspacePath!, config, linkedCt);

                    report = await AppendExternalCiIfNeededAsync(run, report, config, repoProvider, pipelineProvider,
                        transitionTo, issueOps, addRunToHistory, onChange, onOutputLine, allowEmptyCommit: true, linkedCt);
                    if (run.CurrentStep == PipelineStep.Failed) return;

                    run.LatestQualityReport = report;
                    run.QualityGateHistory.Enqueue(report);
                    onOutputLine(PipelineFormatting.FormatQualityGateSummary(report));

                    _logger.Information("Pipeline {RunId} final QG retry quality gates: AllPassed={AllPassed}, Compilation={CompilationPassed}, Tests={TestsPassed}, Coverage={CoverageResult}, SecurityScan={SecurityResult}, ExternalCi={ExternalCiResult}",
                        run.RunId, report.AllPassed, report.Compilation.Passed, report.Tests.Passed,
                        FormatCoverageLogValue(report.Coverage), FormatGateLogValue(report.SecurityScan), FormatGateLogValue(report.ExternalCi));
                }

                if (report.AllPassed)
                {
                    await createPullRequest(run, report, false, linkedCt);
                }
                else
                {
                    _logger.Warning("Pipeline {RunId} max retries ({MaxRetries}) exhausted after cleanup, creating draft PR",
                        run.RunId, config.MaxRetries);
                    onOutputLine($"⚠️ Quality gates failed after {config.MaxRetries} retries, creating draft PR");

                    var finalErrorSummary = BuildQualityGateErrorSummary(report);
                    run.RetryErrors.Add(finalErrorSummary);

                    await createPullRequest(run, report, true, linkedCt);
                }
            }
            else
            {
                _logger.Warning("Pipeline {RunId} max retries ({MaxRetries}) exhausted, creating draft PR",
                    run.RunId, config.MaxRetries);
                onOutputLine($"⚠️ Quality gates failed after {config.MaxRetries} retries, creating draft PR");

                var errorSummary = BuildQualityGateErrorSummary(report);
                run.RetryErrors.Add(errorSummary);

                await createPullRequest(run, report, true, linkedCt);
            }
        }
        catch (OperationCanceledException)
        {
            if (run.CurrentStep is not (PipelineStep.Cancelled or PipelineStep.Failed))
            {
                _logger.Information("Pipeline {RunId} was cancelled during quality gates", run.RunId);
                run.CompletedAt = DateTime.UtcNow;
                await removeAllAgentLabels(run.IssueIdentifier, CancellationToken.None);
                // TODO: [UX-16] Emit onOutputLine("🚫 Pipeline cancelled") for output log consistency
                transitionTo(PipelineStep.Cancelled);
                addRunToHistory(run);
            }
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Pipeline {RunId} quality gate validation failed", run.RunId);
            run.FailureReason = $"Quality gate validation error: {ex.Message}";
            await issueOps.SwapLabelAsync(run.IssueIdentifier, AgentLabels.Error, CancellationToken.None);
            // TODO: [UX-16] Emit onOutputLine($"❌ Pipeline failed: {run.FailureReason}") for output log consistency
            transitionTo(PipelineStep.Failed);
            addRunToHistory(run);
        }
    }

    /// <summary>
    /// If local gates passed and external CI is enabled, commits, pushes, waits for CI,
    /// and returns a new report with the external CI gate appended.
    /// </summary>
    /// <summary>
    /// Appends an external CI gate result to the quality gate report if external CI is enabled
    /// and all local gates passed. When <paramref name="skipCiIfNoChanges"/> is true and there
    /// are no changes to commit, skips CI entirely (used after cleanup when CI already validated
    /// the same commit). When <paramref name="allowEmptyCommit"/> is true and there are no changes,
    /// creates an empty commit to trigger a CI re-run (used in retry loops).
    /// </summary>
    public async Task<QualityGateReport> AppendExternalCiIfNeededAsync(
        PipelineRun run, QualityGateReport report,
        PipelineConfiguration config,
        IRepositoryProvider repoProvider,
        IPipelineProvider? pipelineProvider,
        Action<PipelineStep> transitionTo,
        IAgentIssueOperations issueOps,
        Action<PipelineRun> addRunToHistory,
        Action onChange,
        Action<string> onOutputLine,
        bool allowEmptyCommit, CancellationToken ct,
        bool skipCiIfNoChanges = false)
    {
        if (!report.Compilation.Passed || !report.Tests.Passed
            || !(report.Coverage?.Passed ?? true) || !(report.SecurityScan?.Passed ?? true)
            || !config.ExternalCiEnabled || pipelineProvider == null)
            return report;

        GateResult? ciGate = null;
        try
        {
            try
            {
                var commitMessage = PipelineFormatting.GenerateCommitMessage(run.IssueTitle, run.IssueIdentifier);
                var blacklisted = await repoProvider.CommitAllAsync(
                    run.WorkspacePath!, commitMessage, config.BlacklistedPaths, ct);
                if (await RecordBlacklistedFiles(run, blacklisted, config, transitionTo, issueOps, addRunToHistory, onChange, ct))
                    return report;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("No changes to commit"))
            {
                if (skipCiIfNoChanges)
                {
                    // CI already validated this exact commit during the initial quality gate run.
                    // Cleanup made no changes, so there's nothing new to validate — skip external CI.
                    _logger.Information("Pipeline {RunId} no changes after cleanup, skipping external CI (already validated)", run.RunId);
                    onOutputLine("✅ External CI skipped — no changes since last CI pass");
                    return report;
                }
                else if (allowEmptyCommit)
                {
                    _logger.Information("Pipeline {RunId} no changes after retry fix, creating empty commit to trigger CI", run.RunId);
                    await repoProvider.CommitAllAsync(
                        run.WorkspacePath!,
                        $"chore: trigger CI re-run for {run.IssueIdentifier} (retry {run.RetryCount})",
                        config.BlacklistedPaths, allowEmpty: true, ct);
                }
                else if (!await repoProvider.HasCommitsAheadAsync(run.WorkspacePath!, ct))
                {
                    _logger.Warning("Pipeline {RunId} no changes to commit and no commits ahead of base", run.RunId);
                    throw;
                }
                else
                {
                    _logger.Information("Pipeline {RunId} no uncommitted changes but branch has commits ahead, proceeding to push", run.RunId);
                }
            }

            await repoProvider.PushBranchAsync(run.WorkspacePath!, run.BranchName!, ct);
            _logger.Information("Pipeline {RunId} pushed branch {BranchName} for CI validation", run.RunId, run.BranchName);
            onOutputLine($"📦 Committed changes for CI validation");
            onOutputLine($"🔀 Pushed to origin/{run.BranchName}");

            string? commitSha = null;
            try { commitSha = await repoProvider.GetHeadCommitShaAsync(run.WorkspacePath!, ct); }
            catch (Exception ex) { _logger.Debug(ex, "Pipeline {RunId} could not read HEAD commit SHA", run.RunId); }

            onOutputLine("⏳ Waiting for external CI...");
            var ciStatus = await pipelineProvider.WaitForCompletionAsync(
                run.BranchName!, commitSha, config.ExternalCiTimeout, ct);

            var ciPassed = ciStatus.State == PipelineRunState.Passed;
            IReadOnlyDictionary<long, string>? ciLogPaths = null;
            if (!ciPassed && run.WorkspacePath != null)
                ciLogPaths = _ciLogWriter.WriteJobLogs(ciStatus, run.WorkspacePath, run.RunId);

            ciGate = new GateResult
            {
                GateName = "External CI",
                Passed = ciPassed,
                Details = ciPassed
                    ? $"CI passed. {ciStatus.Jobs.Count} job(s) completed."
                    : QualityGateValidator.BuildCiFailureDetails(ciStatus, ciLogPaths)
            };

            onOutputLine(ciPassed
                ? $"✅ External CI passed ({ciStatus.Jobs.Count} jobs)"
                : $"❌ External CI failed: {ciGate.Details}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ciGate = new GateResult
            {
                GateName = "External CI", Passed = false,
                Details = $"External CI timed out after {config.ExternalCiTimeout}"
            };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Pipeline {RunId} external CI check failed, treating as gate failure", run.RunId);
            ciGate = new GateResult
            {
                GateName = "External CI", Passed = false,
                Details = $"External CI error: {ex.Message}"
            };
        }

        return new QualityGateReport
        {
            Compilation = report.Compilation,
            Tests = report.Tests,
            Coverage = report.Coverage,
            SecurityScan = report.SecurityScan,
            ExternalCi = ciGate
        };
    }

    /// <summary>
    /// Records blacklisted files and returns true if the pipeline should stop (Fail mode).
    /// </summary>
    private async Task<bool> RecordBlacklistedFiles(
        PipelineRun run, IReadOnlyList<string> blacklisted,
        PipelineConfiguration config,
        Action<PipelineStep> transitionTo,
        IAgentIssueOperations issueOps,
        Action<PipelineRun> addRunToHistory,
        Action onChange,
        CancellationToken ct)
    {
        if (blacklisted.Count == 0) return false;

        _prOrchestrator.RecordBlacklistedFiles(run, blacklisted, config);

        if (config.BlacklistMode == BlacklistMode.Fail)
        {
            var fileList = string.Join(", ", blacklisted);
            run.FailureReason = $"Blacklisted files detected: {fileList}. The agent modified protected paths.";
            run.CompletedAt = DateTime.UtcNow;
            await issueOps.SwapLabelAsync(run.IssueIdentifier, AgentLabels.Error, CancellationToken.None);
            transitionTo(PipelineStep.Failed);
            addRunToHistory(run);
            return true;
        }

        onChange();
        return false;
    }

    internal static string FormatGateLogValue(GateResult? gate) =>
        gate is null ? "N/A" : gate.Passed.ToString();

    internal static string FormatCoverageLogValue(GateResult? gate) =>
        gate is null ? "N/A" : gate.CoveragePercent.HasValue
            ? $"{gate.Passed} ({gate.CoveragePercent.Value.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)}%)"
            : gate.Passed.ToString();

    private static string BuildQualityGateErrorSummary(QualityGateReport report)
    {
        var errors = new List<string>();
        if (!report.Compilation.Passed)
            errors.Add($"Compilation: {report.Compilation.Details}");
        if (!report.Tests.Passed)
            errors.Add($"Tests: {report.Tests.Details}");
        if (report.Coverage is { Passed: false })
            errors.Add($"Coverage: {report.Coverage.Details}");
        if (report.SecurityScan is { Passed: false })
            errors.Add($"Security: {report.SecurityScan.Details}");
        if (report.ExternalCi is { Passed: false })
            errors.Add($"External CI: {report.ExternalCi.Details}");
        return string.Join(Environment.NewLine, errors);
    }

    internal static string BuildQualityGateRetryPrompt(QualityGateReport report, int attempt, int maxRetries)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Quality gates failed (attempt {attempt}/{maxRetries}):");
        sb.AppendLine($"- Compilation: {(report.Compilation.Passed ? "PASSED" : "FAILED")} ({report.Compilation.Details})");
        sb.AppendLine($"- Tests: {(report.Tests.Passed ? "PASSED" : "FAILED")} ({report.Tests.Details})");
        if (report.Coverage != null)
            sb.AppendLine($"- Coverage: {(report.Coverage.Passed ? "PASSED" : "FAILED")} ({report.Coverage.Details})");
        if (report.SecurityScan != null)
            sb.AppendLine($"- Security: {(report.SecurityScan.Passed ? "PASSED" : "FAILED")} ({report.SecurityScan.Details})");
        if (report.ExternalCi != null)
            sb.AppendLine($"- External CI: {(report.ExternalCi.Passed ? "PASSED" : "FAILED")} ({report.ExternalCi.Details})");
        sb.AppendLine();
        sb.AppendLine($"Diagnostic output has been written to `{PromptBuilder.QualityGatesOutputDirectory}/`.");
        sb.Append("List the files there and read the relevant ones to diagnose and fix the failures.");
        return sb.ToString();
    }
}
