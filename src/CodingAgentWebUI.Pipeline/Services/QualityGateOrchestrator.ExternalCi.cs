using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

internal partial class QualityGateOrchestrator
{
    /// <summary>
    /// Determines which ValidateAsync overload to call based on the QGC context:
    /// - Non-empty QualityGateConfigs → multi-QGC validation
    /// - Empty (none matched or none configured) → skip, return passing report
    /// </summary>
    private async Task<QualityGateReport> RunQualityGateValidationAsync(
        QualityGateContext context, string workspacePath, PipelineConfiguration config, CancellationToken ct)
    {
        if (context.QualityGateConfigs.Count > 0)
        {
            // Multi-QGC mode: validate against matched QGCs
            return await _qualityGateValidator.ValidateAsync(workspacePath, context.QualityGateConfigs, ct);
        }

        // No QGCs matched (or none configured) — skip quality gates
        _logger.Warning("Pipeline {RunId} has no matching QGCs. Skipping quality gates.",
            context.Run.RunId);

        return new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true, Details = "Skipped — no matching QGCs" },
            Tests = new GateResult { GateName = "Tests", Passed = true, Details = "Skipped — no matching QGCs" }
        };
    }

    /// <summary>
    /// Appends an external CI gate result to the quality gate report if external CI is enabled
    /// and all local gates passed. When <paramref name="skipCiIfNoChanges"/> is true and there
    /// are no changes to commit, skips CI entirely (used after cleanup when CI already validated
    /// the same commit). When <paramref name="allowEmptyCommit"/> is true and there are no changes,
    /// creates an empty commit to trigger a CI re-run (used in retry loops).
    /// </summary>
    public async Task<QualityGateReport> AppendExternalCiIfNeededAsync(
        QualityGateContext context,
        QualityGateReport report,
        bool allowEmptyCommit,
        CancellationToken ct,
        bool skipCiIfNoChanges = false)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(report);

        var run = context.Run;
        var config = context.Config;
        var callbacks = context.Callbacks;

        if (!report.Compilation.Passed || !report.Tests.Passed
            || !(report.Coverage?.Passed ?? true) || !(report.SecurityScan?.Passed ?? true)
            || !config.ExternalCiEnabled || context.PipelineProvider == null)
            return report;

        GateResult? ciGate = null;
        try
        {
            try
            {
                var commitMessage = PipelineFormatting.GenerateCommitMessage(run.IssueTitle, run.IssueIdentifier);
                var blacklisted = await context.RepoProvider.CommitAllAsync(
                    run.WorkspacePath!, commitMessage, config.BlacklistedPaths, ct);
                if (await RecordBlacklistedFiles(run, blacklisted, config, callbacks, context.IssueOps, ct))
                    return report;
            }
            catch (InvalidOperationException ex) when (ex.Message.Contains("No changes to commit"))
            {
                if (skipCiIfNoChanges)
                {
                    _logger.Information("Pipeline {RunId} no changes after cleanup, skipping external CI (already validated)", run.RunId);
                    callbacks.EmitOutputLine("✅ External CI skipped — no changes since last CI pass");
                    return report;
                }
                else if (allowEmptyCommit)
                {
                    _logger.Information("Pipeline {RunId} no changes after retry fix, creating empty commit to trigger CI", run.RunId);
                    await context.RepoProvider.CommitAllAsync(
                        run.WorkspacePath!,
                        $"chore: trigger CI re-run for {run.IssueIdentifier} (retry {run.RetryCount})",
                        config.BlacklistedPaths, allowEmpty: true, ct);
                }
                else if (!await context.RepoProvider.HasCommitsAheadAsync(run.WorkspacePath!, ct))
                {
                    _logger.Warning("Pipeline {RunId} no changes to commit and no commits ahead of base", run.RunId);
                    throw;
                }
                else
                {
                    _logger.Information("Pipeline {RunId} no uncommitted changes but branch has commits ahead, proceeding to push", run.RunId);
                }
            }

            await context.RepoProvider.PushBranchAsync(run.WorkspacePath!, run.BranchName!, ct);
            _logger.Information("Pipeline {RunId} pushed branch {BranchName} for CI validation", run.RunId, run.BranchName);
            callbacks.EmitOutputLine($"📦 Committed changes for CI validation");
            callbacks.EmitOutputLine($"🔀 Pushed to origin/{run.BranchName}");

            string? commitSha = null;
            try { commitSha = await context.RepoProvider.GetHeadCommitShaAsync(run.WorkspacePath!, ct); }
            catch (Exception ex) { _logger.Debug(ex, "Pipeline {RunId} could not read HEAD commit SHA", run.RunId); }

            callbacks.EmitOutputLine("⏳ Waiting for external CI...");
            var ciStatus = await context.PipelineProvider.WaitForCompletionAsync(
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

            callbacks.EmitOutputLine(ciPassed
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
        IPipelineCallbacks callbacks,
        IAgentIssueOperations issueOps,
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
            callbacks.TransitionTo(PipelineStep.Failed);
            callbacks.AddRunToHistory(run);
            return true;
        }

        callbacks.NotifyChange();
        return false;
    }
}
