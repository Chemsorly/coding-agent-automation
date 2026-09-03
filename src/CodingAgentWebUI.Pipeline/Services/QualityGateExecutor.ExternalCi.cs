using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Telemetry;

namespace CodingAgentWebUI.Pipeline.Services;

public partial class QualityGateExecutor
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
            || !(report.SecurityScan?.Passed ?? true)
            || context.PipelineProvider == null)
            return report;

        GateResult? ciGate = null;
        try
        {
            var skipCi = await CommitAndPushAsync(context, allowEmptyCommit, skipCiIfNoChanges, ct);
            if (skipCi)
                return report;

            // Create draft PR if not exists — ensures CI results (coverage comments) land on the PR
            await callbacks.CreateDraftPrIfNotExists(run, ct);

            string? commitSha = null;
            try { commitSha = await context.RepoProvider.GetHeadCommitShaAsync(run.WorkspacePath!, ct); }
            catch (Exception ex) { _logger.Debug(ex, "Pipeline {RunId} could not read HEAD commit SHA", run.RunId); }

            callbacks.EmitOutputLine("⏳ Waiting for external CI...");
            var ciPollStopwatch = System.Diagnostics.Stopwatch.StartNew();
            var (ciPassed, ciStatus, ciLogPaths) = await PollAndHandleInfraRetryAsync(context, commitSha, config, callbacks, ct);

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
            SecurityScan = report.SecurityScan,
            ExternalCi = ciGate
        };
    }

    /// <summary>
    /// Commits and pushes the workspace branch. Returns true when CI should be skipped
    /// (no-changes + skipCiIfNoChanges path). Throws on all other non-cancellation errors.
    /// </summary>
    private async Task<bool> CommitAndPushAsync(
        QualityGateContext context,
        bool allowEmptyCommit,
        bool skipCiIfNoChanges,
        CancellationToken ct)
    {
        var run = context.Run;
        var config = context.Config;
        var callbacks = context.Callbacks;

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
                callbacks.EmitOutputLine("✅ External CI skipped — no changes since last CI pass");
                return true;
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
        _logger.Information("Pipeline {RunId} pushed branch {BranchName} for CI validation", run.RunId, run.BranchName);
        callbacks.EmitOutputLine($"📦 Committed changes for CI validation");
        callbacks.EmitOutputLine($"🔀 Pushed to origin/{run.BranchName}");
        return false;
    }

    /// <summary>
    /// Polls CI and automatically retries on infrastructure failures up to
    /// <see cref="PipelineConfiguration.MaxInfrastructureRetries"/> times.
    /// Returns (ciPassed, finalStatus, ciLogPaths).
    /// </summary>
    private async Task<(bool ciPassed, PipelineRunStatus ciStatus, IReadOnlyDictionary<long, string>? ciLogPaths)> PollAndHandleInfraRetryAsync(
        QualityGateContext context,
        string? pollSha,
        PipelineConfiguration config,
        IPipelineCallbacks callbacks,
        CancellationToken ct)
    {
        var run = context.Run;

        // Budget the entire polling session (initial poll + all branch-moved re-polls) against a
        // single ExternalCiTimeout window.  When the timeout fires the linked token is cancelled;
        // AppendExternalCiIfNeededAsync's catch (OperationCanceledException when !ct.IsCancellationRequested)
        // turns that into a "timed out" gate result, matching the existing behaviour for a single poll.
        using var timeoutCts = new CancellationTokenSource(config.ExternalCiTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var pollCt = linkedCts.Token;

        var ciStatus = await PollCiWithNotStartedRetryAsync(context, pollSha, config, callbacks, pollCt);
        var ciPassed = ciStatus.State == PipelineRunState.Passed;
        IReadOnlyDictionary<long, string>? ciLogPaths = null;

        // Branch-moved cancellation: when CI is Cancelled and the branch HEAD has moved to a new
        // commit (e.g. a teammate push, a bot merge-from-main, or the pipeline's own retry commit
        // triggering GitHub's cancel-in-progress concurrency rule), re-enter CI polling on the new
        // HEAD SHA rather than treating the cancellation as a gate failure that consumes a retry slot.
        var branchMovedRetries = 0;
        while (ciStatus.State == PipelineRunState.Cancelled
               && run.WorkspacePath != null
               && branchMovedRetries < config.CiCancelledMoveMaxRetries)
        {
            string? currentHead = null;
            try { currentHead = await context.RepoProvider.GetHeadCommitShaAsync(run.WorkspacePath, pollCt); }
            catch (OperationCanceledException) { throw; }
            // TODO [WARNING]: catch (Exception) here swallows any non-cancellation exception from
            // GetHeadCommitShaAsync, logging it at Debug and leaving currentHead null (which causes
            // the break below). This is intentional for transient errors, but differs from the
            // explicit catch (OperationCanceledException) { throw; } guard used in WaitForCiRunsToAppearAsync.
            // Consider aligning the pattern if GetHeadCommitShaAsync gains non-transient failure modes.
            // Additionally, if GetHeadCommitShaAsync internally uses Task.WhenAll or similar, it may throw
            // AggregateException wrapping OperationCanceledException. The explicit OCE guard above does NOT
            // catch AggregateException, so such a cancellation would be swallowed here — leaving currentHead
            // null and causing the loop to break (falling through to infra-retry) instead of propagating
            // cancellation. Consider unwrapping AggregateException or calling ex.IsCancellation() if the
            // provider implementation ever uses aggregate tasks internally.
            catch (Exception ex) { _logger.Debug(ex, "Pipeline {RunId} could not read HEAD after Cancelled", run.RunId); }

            if (currentHead == null || currentHead == pollSha)
                break;  // HEAD unchanged — genuine pre-emption or unreadable HEAD, fall through to infra-retry path

            branchMovedRetries++;
            _logger.Information(
                "Pipeline {RunId} CI cancelled because branch moved ({OldSha} → {NewSha}), re-polling on new HEAD (attempt {N}/{Max})",
                run.RunId, pollSha, currentHead, branchMovedRetries, config.CiCancelledMoveMaxRetries);
            callbacks.EmitOutputLine(
                $"⏳ CI superseded by new commit on branch — re-polling on updated HEAD (attempt {branchMovedRetries}/{config.CiCancelledMoveMaxRetries})...");

            pollSha = currentHead;
            ciStatus = await PollCiWithNotStartedRetryAsync(context, pollSha, config, callbacks, pollCt);
            ciPassed = ciStatus.State == PipelineRunState.Passed;
        }
        // TODO [WARNING]: The two exit conditions from the branch-moved loop are handled identically:
        // (a) HEAD unchanged (currentHead == pollSha) → genuine pre-emption, infra-retry path correct.
        // (b) branchMovedRetries >= CiCancelledMoveMaxRetries → retries exhausted, branch kept moving.
        // Both fall through to the same infra-retry section below. For case (b), CiFailureClassifier.Classify
        // on a Cancelled status with no failed jobs returns Unknown (not Infrastructure), so the infra-retry
        // while-loop is a no-op and the gate fails — the intended behaviour. However, the distinction is
        // invisible to future readers and any change that causes case (b) to classify as Infrastructure would
        // re-introduce the retry storm. Consider adding an explicit log/comment when exiting due to exhausted
        // retries, or an early break-label to make the two paths distinguishable.
        //
        // TODO [WARNING]: The local `pollSha` variable is mutated inside the loop (pollSha = currentHead)
        // but is not read after the loop exits — ExecuteInfraRetryAsync reads a fresh SHA after its own push.
        // The mutation is harmless but misleading; a reader might expect pollSha to feed into the infra-retry
        // path. This is a code clarity issue, not a correctness defect.

        // Write logs for the final ciStatus only — moved here from immediately after the initial poll
        // to avoid writing misleading Cancelled-state log files for discarded intermediate polls.
        if (!ciPassed && run.WorkspacePath != null)
            ciLogPaths = _ciLogWriter.WriteJobLogs(ciStatus, run.WorkspacePath, run.RunId);

        if (!ciPassed)
        {
            var classification = CiFailureClassifier.Classify(ciStatus);
            while (!ciPassed
                   && classification == CiFailureClassifier.CiFailureCategory.Infrastructure
                   && run.InfrastructureRetryCount < config.MaxInfrastructureRetries)
            {
                // TODO [WARNING]: ExecuteInfraRetryAsync is invoked with the original outer `ct`, not `pollCt`.
                // This means infra-retry polling runs outside the ExternalCiTimeout budget established by
                // `timeoutCts` above. If branch-moved re-polls consume most of the ExternalCiTimeout window,
                // a subsequent infra-retry can add a full additional ExternalCiTimeout duration, violating the
                // single-window guarantee documented in the property summary for CiCancelledMoveMaxRetries.
                // To fix: pass `pollCt` instead of `ct` to ExecuteInfraRetryAsync, or restructure so both
                // code paths share the same linked token.
                (ciPassed, ciStatus, ciLogPaths) = await ExecuteInfraRetryAsync(
                    context, config, callbacks, ct);

                if (!ciPassed)
                    classification = CiFailureClassifier.Classify(ciStatus);
            }
        }

        return (ciPassed, ciStatus, ciLogPaths);
    }

    /// <summary>
    /// Performs one infrastructure-failure retry: increments the counter, logs, creates an empty
    /// commit, re-pushes, and polls CI again. Returns (ciPassed, newStatus, ciLogPaths).
    /// </summary>
    private async Task<(bool ciPassed, PipelineRunStatus ciStatus, IReadOnlyDictionary<long, string>? ciLogPaths)> ExecuteInfraRetryAsync(
        QualityGateContext context,
        PipelineConfiguration config,
        IPipelineCallbacks callbacks,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var run = context.Run;
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

        callbacks.EmitOutputLine("⏳ Waiting for external CI (infrastructure retry)...");
        var ciStatus = await PollCiWithNotStartedRetryAsync(context, retrySha, config, callbacks, ct);
        var ciPassed = ciStatus.State == PipelineRunState.Passed;

        IReadOnlyDictionary<long, string>? ciLogPaths = (!ciPassed && run.WorkspacePath != null)
            ? _ciLogWriter.WriteJobLogs(ciStatus, run.WorkspacePath, run.RunId)
            : null;

        return (ciPassed, ciStatus, ciLogPaths);
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
        var pipelineProvider = context.PipelineProvider
            ?? throw new InvalidOperationException("PipelineProvider must not be null when entering CI polling");

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // Wait up to CiNotStartedTimeout for any workflow runs to appear
            var appeared = await WaitForCiRunsToAppearAsync(
                pipelineProvider, run.BranchName!, pollSha, notStartedTimeout, config.ExternalCiPollInterval, ct);

            if (appeared)
            {
                // Runs detected — switch to the full wait-for-completion (uses the full ExternalCiTimeout)
                return await pipelineProvider.WaitForCompletionAsync(
                    run.BranchName!, pollSha, config.ExternalCiTimeout, ct);
            }

            // CI never started within the short timeout
            if (attempt >= maxRetries)
            {
                _logger.Error("Pipeline {RunId} CI never started after {MaxRetries} re-push retries. " +
                              "Falling back to full timeout wait.", run.RunId, maxRetries);
                callbacks.EmitOutputLine($"⚠️ CI never started after {maxRetries} retries — waiting with full timeout as last resort...");
                return await pipelineProvider.WaitForCompletionAsync(
                    run.BranchName!, pollSha, config.ExternalCiTimeout, ct);
            }

            _logger.Warning(
                "Pipeline {RunId} CI never started (attempt {Attempt}/{MaxRetries}, waited {Timeout}). Re-pushing to trigger.",
                run.RunId, attempt + 1, maxRetries, notStartedTimeout);
            callbacks.EmitOutputLine(
                $"⚠️ CI never started (attempt {attempt + 1}/{maxRetries}) — re-pushing to trigger GitHub Actions...");

            // Final check before re-pushing — avoid racing with GitHub's delayed trigger
            var lastCheck = await pipelineProvider.GetRunStatusAsync(run.BranchName!, pollSha, ct);
            if (lastCheck.State != PipelineRunState.Pending || lastCheck.Jobs.Count > 0)
            {
                _logger.Information("Pipeline {RunId} CI appeared just before re-push (race avoided), proceeding to full wait", run.RunId);
                return await pipelineProvider.WaitForCompletionAsync(
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
        return await pipelineProvider.WaitForCompletionAsync(
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
