using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services;

internal partial class QualityGateExecutor
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
            return await _qualityGateValidator.ValidateAsync(workspacePath, context.QualityGateConfigs, ct, context.RepoProvider.BaseBranch);
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
            || context.PipelineProvider == null)
            return report;

        GateResult? ciGate = null;
        try
        {
            try
            {
                var issueRef = context.IssueReference ?? $"#{run.IssueIdentifier}";
                var commitMessage = PipelineFormatting.GenerateCommitMessage(run.IssueTitle, issueRef);
                var blacklisted = await context.RepoProvider.CommitAllAsync(
                    run.WorkspacePath!, commitMessage, config.BlacklistedPaths, ct,
                    config.PipelineInjectedPaths);
                RecordBlacklistedFiles(run, blacklisted, config, callbacks);
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
                        config.BlacklistedPaths, allowEmpty: true, ct,
                        config.PipelineInjectedPaths);
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

            await context.RepoProvider.PushBranchAsync(run.WorkspacePath!, run.BranchName!, forcePush: true, ct);
            _logger.Information("Pipeline {RunId} pushed branch {BranchName} for CI validation",
                run.RunId, run.BranchName);
            callbacks.EmitOutputLine($"📦 Committed changes for CI validation");
            callbacks.EmitOutputLine($"🔀 Pushed to origin/{run.BranchName}");

            // Create draft PR if not exists — ensures CI results (coverage comments) land on the PR
            await callbacks.CreateDraftPrIfNotExists(run, ct);

            string? commitSha = null;
            try { commitSha = await context.RepoProvider.GetHeadCommitShaAsync(run.WorkspacePath!, ct); }
            catch (Exception ex) { _logger.Debug(ex, "Pipeline {RunId} could not read HEAD commit SHA", run.RunId); }

            var pollSha = commitSha;

            callbacks.EmitOutputLine("⏳ Waiting for external CI...");
            var ciPollStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var ciStatus = await PollCiWithNotStartedRetryAsync(context, pollSha, config, callbacks, ct);

            var ciPassed = ciStatus.State == PipelineRunState.Passed;
            IReadOnlyDictionary<long, string>? ciLogPaths = null;
            if (!ciPassed && run.WorkspacePath != null)
                ciLogPaths = _ciLogWriter.WriteJobLogs(ciStatus, run.WorkspacePath, run.RunId);

            // Classify CI failure and auto-retry infrastructure failures
            if (!ciPassed)
            {
                var classification = CiFailureClassifier.Classify(ciStatus);
                while (!ciPassed
                       && classification == CiFailureClassifier.CiFailureCategory.Infrastructure
                       && run.InfrastructureRetryCount < config.MaxInfrastructureRetries)
                {
                    ct.ThrowIfCancellationRequested();
                    run.InfrastructureRetryCount++;
                    _logger.Warning("Pipeline {RunId} CI infrastructure failure detected, auto-retrying ({Attempt}/{Max})",
                        run.RunId, run.InfrastructureRetryCount, config.MaxInfrastructureRetries);
                    callbacks.EmitOutputLine($"⚠️ CI infrastructure failure — auto-retrying ({run.InfrastructureRetryCount}/{config.MaxInfrastructureRetries})...");

                    await context.RepoProvider.CommitAllAsync(run.WorkspacePath!,
                        $"chore: re-trigger CI after infrastructure failure ({run.InfrastructureRetryCount})",
                        config.BlacklistedPaths, allowEmpty: true, ct,
                        config.PipelineInjectedPaths);
                    await context.RepoProvider.PushBranchAsync(run.WorkspacePath!, run.BranchName!, forcePush: true, ct);

                    string? retrySha = null;
                    try { retrySha = await context.RepoProvider.GetHeadCommitShaAsync(run.WorkspacePath!, ct); }
                    catch (Exception ex) { _logger.Debug(ex, "Pipeline {RunId} could not read HEAD commit SHA for infra retry", run.RunId); }
                    var retryPollSha = retrySha;

                    callbacks.EmitOutputLine("⏳ Waiting for external CI (infrastructure retry)...");
                    ciStatus = await PollCiWithNotStartedRetryAsync(context, retryPollSha, config, callbacks, ct);
                    ciPassed = ciStatus.State == PipelineRunState.Passed;

                    ciLogPaths = (!ciPassed && run.WorkspacePath != null)
                        ? _ciLogWriter.WriteJobLogs(ciStatus, run.WorkspacePath, run.RunId)
                        : null;

                    if (!ciPassed)
                        classification = CiFailureClassifier.Classify(ciStatus);
                }
            }

            // TODO: Duration includes infrastructure retry wait times — consider recording per-attempt duration for better histogram granularity
            PipelineTelemetry.ExternalCiDuration.Record(
                ciPollStopwatch.Elapsed.TotalSeconds,
                PipelineTelemetry.BuildTags(run.RunType, run.ProjectId, run.ProjectName));

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
    /// Records blacklisted files on the run and notifies the UI.
    /// </summary>
    private void RecordBlacklistedFiles(
        PipelineRun run, IReadOnlyList<string> blacklisted,
        PipelineConfiguration config,
        IPipelineCallbacks callbacks)
    {
        if (blacklisted.Count == 0) return;

        _prOrchestrator.RecordBlacklistedFiles(run, blacklisted, config);
        callbacks.NotifyChange();
    }

    /// <summary>
    /// Polls CI with automatic retry when CI never starts (GitHub Actions sometimes doesn't trigger).
    /// First waits up to <see cref="PipelineConfiguration.CiNotStartedTimeout"/> for any runs to appear.
    /// If no runs appear, creates an empty commit and re-pushes to trigger CI, repeating up to
    /// <see cref="PipelineConfiguration.CiNotStartedMaxRetries"/> times.
    /// Once runs are detected (or retries exhausted), delegates to the full WaitForCompletionAsync.
    /// </summary>
    private async Task<PipelineRunStatus> PollCiWithNotStartedRetryAsync(
        QualityGateContext context,
        string? pollSha,
        PipelineConfiguration config,
        IPipelineCallbacks callbacks,
        CancellationToken ct)
    {
        var run = context.Run;
        var maxRetries = config.CiNotStartedMaxRetries;
        var notStartedTimeout = config.CiNotStartedTimeout;

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // Wait up to CiNotStartedTimeout for any workflow runs to appear
            var appeared = await WaitForCiRunsToAppearAsync(
                context.PipelineProvider!, run.BranchName!, pollSha, notStartedTimeout, config.ExternalCiPollInterval, ct);

            if (appeared)
            {
                // Runs detected — switch to the full wait-for-completion (uses the full ExternalCiTimeout)
                return await context.PipelineProvider!.WaitForCompletionAsync(
                    run.BranchName!, pollSha, config.ExternalCiTimeout, ct);
            }

            // CI never started within the short timeout
            if (attempt >= maxRetries)
            {
                _logger.Error("Pipeline {RunId} CI never started after {MaxRetries} re-push retries. " +
                              "Falling back to full timeout wait.", run.RunId, maxRetries);
                callbacks.EmitOutputLine($"⚠️ CI never started after {maxRetries} retries — waiting with full timeout as last resort...");
                return await context.PipelineProvider!.WaitForCompletionAsync(
                    run.BranchName!, pollSha, config.ExternalCiTimeout, ct);
            }

            _logger.Warning(
                "Pipeline {RunId} CI never started (attempt {Attempt}/{MaxRetries}, waited {Timeout}). Re-pushing to trigger.",
                run.RunId, attempt + 1, maxRetries, notStartedTimeout);
            callbacks.EmitOutputLine(
                $"⚠️ CI never started (attempt {attempt + 1}/{maxRetries}) — re-pushing to trigger GitHub Actions...");

            // Final check before re-pushing — avoid racing with GitHub's delayed trigger
            var lastCheck = await context.PipelineProvider!.GetRunStatusAsync(run.BranchName!, pollSha, ct);
            if (lastCheck.State != PipelineRunState.Pending || lastCheck.Jobs.Count > 0)
            {
                _logger.Information("Pipeline {RunId} CI appeared just before re-push (race avoided), proceeding to full wait", run.RunId);
                return await context.PipelineProvider!.WaitForCompletionAsync(
                    run.BranchName!, pollSha, config.ExternalCiTimeout, ct);
            }

            // Create empty commit and re-push
            await context.RepoProvider.CommitAllAsync(
                run.WorkspacePath!,
                $"chore: re-trigger CI (not started, attempt {attempt + 1})",
                config.BlacklistedPaths, allowEmpty: true, ct,
                config.PipelineInjectedPaths);
            await context.RepoProvider.PushBranchAsync(run.WorkspacePath!, run.BranchName!, forcePush: true, ct);

            // Update the poll SHA to the new commit
            try { pollSha = await context.RepoProvider.GetHeadCommitShaAsync(run.WorkspacePath!, ct); }
            catch (Exception shaEx) { _logger.Debug(shaEx, "Pipeline {RunId} could not read HEAD after re-push", run.RunId); }
        }

        // Should not reach here, but satisfy the compiler
        return await context.PipelineProvider!.WaitForCompletionAsync(
            run.BranchName!, pollSha, config.ExternalCiTimeout, ct);
    }

    /// <summary>
    /// Polls GetRunStatusAsync until at least one workflow run/job is detected or the timeout expires.
    /// Returns true if runs appeared, false if the timeout expired with no runs.
    /// </summary>
    private async Task<bool> WaitForCiRunsToAppearAsync(
        IPipelineProvider provider,
        string branchName,
        string? commitSha,
        TimeSpan timeout,
        TimeSpan pollInterval,
        CancellationToken ct)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var status = await provider.GetRunStatusAsync(branchName, commitSha, ct);

                // Any non-empty state (Running, Passed, Failed, Cancelled) or jobs present means CI started
                if (status.State != PipelineRunState.Pending || status.Jobs.Count > 0)
                    return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Transient API error (rate limit, network, etc.) — log and keep polling within the timeout
                _logger.Debug(ex, "WaitForCiRunsToAppearAsync transient error polling {Branch}, will retry", branchName);
            }

            await Task.Delay(pollInterval, ct);
        }
        return false;
    }
}
