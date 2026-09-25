using System.Diagnostics;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Encapsulates the CI-polling sub-loops extracted from <see cref="QualityGateExecutor"/>:
/// not-started-retry polling, infrastructure-retry polling, and post-PR CI waiting.
/// All methods are pure coordination — no commit/push logic lives here except for
/// empty re-trigger commits inside the retry paths.
/// </summary>
internal sealed class CiPollingCoordinator
{
    private readonly Serilog.ILogger _logger;
    private readonly CiLogWriter _ciLogWriter;
    private readonly CiPollingMetrics _metrics;

    internal CiPollingCoordinator(
        Serilog.ILogger logger,
        CiLogWriter ciLogWriter,
        CiPollingMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(ciLogWriter);
        ArgumentNullException.ThrowIfNull(metrics);

        _logger = logger;
        _ciLogWriter = ciLogWriter;
        _metrics = metrics;
    }

    // ── Public phase methods ───────────────────────────────────────────────────

    /// <summary>
    /// Polls CI and automatically retries on infrastructure failures up to
    /// <see cref="PipelineConfiguration.MaxInfrastructureRetries"/> times.
    /// Returns (ciPassed, finalStatus, ciLogPaths).
    /// The <paramref name="pollCt"/> must already be budget-limited (e.g. linked to an
    /// ExternalCiTimeout CTS) — this method does not create its own timeout.
    /// </summary>
    /// <remarks>
    /// TODO [WARNING]: The <paramref name="pollCt"/> budget contract is not enforced by any runtime
    /// guard. A caller that passes the raw pipeline token (without an ExternalCiTimeout-linked CTS)
    /// will violate the contract silently — the post-PR path was affected exactly this way before the
    /// fix in PollPostPrCiWithTelemetryAsync. If the method is ever called from a new site, the
    /// contract must be honoured manually. Consider adding a diagnostic assertion or renaming the
    /// parameter to <c>budgetedCt</c> to make the requirement visible at every call site.
    /// </remarks>
    internal async Task<(bool ciPassed, PipelineRunStatus ciStatus, IReadOnlyDictionary<long, string>? ciLogPaths)>
        PollAndHandleInfraRetryAsync(
            QualityGateContext context,
            string? pollSha,
            PipelineConfiguration config,
            IPipelineCallbacks callbacks,
            CancellationToken pollCt)
    {
        var run = context.Run;

        var ciStatus = await PollCiWithNotStartedRetryAsync(context, pollSha, config, callbacks, pollCt);
        var ciPassed = ciStatus.State == PipelineRunState.Passed;
        IReadOnlyDictionary<long, string>? ciLogPaths = null;

        if (ciStatus.State is PipelineRunState.ConflictRestart or PipelineRunState.PrMerged or PipelineRunState.PrClosed)
            return (false, ciStatus, null);

        (ciPassed, ciStatus) = await HandleBranchMovedRetryLoopAsync(
            context, ciStatus, ciPassed, pollSha, config, callbacks, pollCt);

        if (ciStatus.State is PipelineRunState.ConflictRestart or PipelineRunState.PrMerged or PipelineRunState.PrClosed)
            return (false, ciStatus, null);

        if (!ciPassed && run.WorkspacePath != null)
            ciLogPaths = _ciLogWriter.WriteJobLogs(ciStatus, run.WorkspacePath, run.RunId);

        if (!ciPassed)
        {
            var classification = CiFailureClassifier.Classify(ciStatus);
            while (!ciPassed
                   && classification == CiFailureClassifier.CiFailureCategory.Infrastructure
                   && run.InfrastructureRetryCount < config.MaxInfrastructureRetries)
            {
                (ciPassed, ciStatus, ciLogPaths) = await ExecuteInfraRetryAsync(
                    context, config, callbacks, pollCt);

                if (!ciPassed)
                    classification = CiFailureClassifier.Classify(ciStatus);
            }
        }

        return (ciPassed, ciStatus, ciLogPaths);
    }

    /// <summary>
    /// Polls CI with automatic retry when CI never starts (GitHub Actions sometimes doesn't trigger).
    /// First waits up to <see cref="PipelineConfiguration.CiNotStartedTimeout"/> for any runs to appear.
    /// If no runs appear, performs a branch-wide CI check (SHA=null) to detect runs that already passed
    /// on a prior commit — if found, returns immediately without creating a re-trigger commit.
    /// Otherwise creates an empty commit and re-pushes to trigger CI, repeating up to
    /// <see cref="PipelineConfiguration.CiNotStartedMaxRetries"/> times.
    /// Before each empty-commit push (and before the exhaustion failure), checks PR mergeability.
    /// If <see cref="PrMergeabilityStatus.Conflicted"/>, returns <see cref="PipelineRunState.ConflictRestart"/>
    /// immediately without pushing any commit.
    /// When retries are exhausted, sets <see cref="PipelineRun.FailureReason"/> and returns a
    /// deterministic <see cref="PipelineRunState.Failed"/> status without blocking on a full timeout.
    /// </summary>
    internal async Task<PipelineRunStatus> PollCiWithNotStartedRetryAsync(
        QualityGateContext context,
        string? pollSha,
        PipelineConfiguration config,
        IPipelineCallbacks callbacks,
        CancellationToken ct)
    {
        var run = context.Run;
        var maxRetries = config.CiNotStartedMaxRetries;
        var pipelineProvider = context.PipelineProvider
            ?? throw new InvalidOperationException("PipelineProvider must not be null when entering CI polling");

        for (var attempt = 0; attempt <= maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var runResult = await TryCompleteCiOnCurrentShaAsync(
                pipelineProvider, run, pollSha, config, ct);
            if (runResult is not null)
                return runResult;

            var conflictResult = await CheckForMergeConflictAsync(context, callbacks, ct);
            if (conflictResult is not null)
                return conflictResult;

            var prStateResult = await CheckPullRequestStillOpenAsync(context, callbacks, ct);
            if (prStateResult is not null)
                return prStateResult;

            if (attempt >= maxRetries)
            {
                // TODO [WARNING] (DotNetSpecialist/TestQualityReviewer): This second call to CheckPullRequestStillOpenAsync is
                // redundant — the identical call just above (line ~133) already returns early if the PR is merged/closed.
                // No state-mutating operations occur between the two calls, so the second call can only differ under an
                // extremely narrow race window. It wastes one extra provider API request on every exhaustion path.
                // Consider removing the inner call and relying on the per-iteration check above.
                // Note: if removed, also verify MarkCompleted double-call risk (BuildPrMergedStatus/BuildPrClosedStatus call
                // run.MarkCompleted(), and MarkCompleted is not idempotent — it overwrites CompletedAt on each call).
                var prStateBeforeExhaustion = await CheckPullRequestStillOpenAsync(context, callbacks, ct);
                if (prStateBeforeExhaustion is not null)
                    return prStateBeforeExhaustion;

                return BuildNotStartedFailureStatus(run, maxRetries, callbacks);
            }

            _logger.Warning(
                "Pipeline {RunId} CI never started (attempt {Attempt}/{MaxRetries}, waited {Timeout}). Re-pushing to trigger.",
                run.RunId, attempt + 1, maxRetries, config.CiNotStartedTimeout);
            callbacks.EmitOutputLine(
                $"⚠️ CI never started (attempt {attempt + 1}/{maxRetries}) — re-pushing to trigger GitHub Actions...");

            var guardResult = await TryDetectAlreadyRunningCiAsync(
                pipelineProvider, run, pollSha, config, callbacks, ct);
            if (guardResult is not null)
                return guardResult;

            await context.RepoProvider.CommitAllAsync(
                run.WorkspacePath!,
                $"chore: re-trigger CI (not started, attempt {attempt + 1})",
                config.BlacklistedPaths, allowEmpty: true, ct,
                config.PipelineInjectedPaths);
            await context.RepoProvider.PushBranchAsync(run.WorkspacePath!, run.BranchName!, forcePush: true, ct);

            pollSha = await TryReadHeadShaAsync(context, "could not read HEAD after re-push", ct);
        }

        // Should not reach here — the attempt >= maxRetries branch always returns.
        return new PipelineRunStatus { State = PipelineRunState.Failed, Jobs = Array.Empty<PipelineJobResult>() };
    }

    /// <summary>
    /// Polls external CI after the PR has been promoted to ready-for-review. This validates
    /// CI workflows that only trigger on <c>pull_request</c> events (not on branch pushes),
    /// which would not have been caught by the pre-PR CI pass if that pass exited early via
    /// the <c>skipCiIfNoChanges</c> path.
    /// </summary>
    internal async Task<QualityGateReport> WaitForPostPrCiAsync(
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

        var priorInfraRetryCount = run.InfrastructureRetryCount;
        run.InfrastructureRetryCount = 0;

        var waitSw = Stopwatch.StartNew();
        GateResult ciGate;
        try
        {
            ciGate = await PollPostPrCiWithTelemetryAsync(
                context, commitSha, config, callbacks, priorInfraRetryCount, ct);
        }
        finally
        {
            waitSw.Stop();
            // Guard against genuine pipeline cancellation (ct.IsCancellationRequested).
            // Only emit the sample when the wait completed — not when the pipeline was cancelled mid-poll.
            if (!ct.IsCancellationRequested)
            {
                var stepTags = PipelineTelemetry.BuildStepTags("WaitForPostPrCi", run.RunType, run.ProjectId, run.ProjectName);
                _metrics.StepDuration.Record(waitSw.Elapsed.TotalSeconds, stepTags);
                _metrics.StepCount.Add(1, stepTags);
            }
        }

        return new QualityGateReport
        {
            Compilation = report.Compilation,
            Tests = report.Tests,
            ExternalCi = ciGate
        };
    }

    // ── Internal read-head helper (also used by QualityGateExecutor shells) ──

    /// <summary>
    /// Best-effort read of the HEAD commit SHA for the current run's workspace.
    /// Returns <see langword="null"/> and logs at Debug level when the read fails for any
    /// non-cancellation reason. Callers use the result as an optional SHA hint for CI polling;
    /// a null value causes CI polling to fall back to branch-only filtering.
    /// </summary>
    internal async Task<string?> TryReadHeadShaAsync(
        QualityGateContext context, string purpose, CancellationToken ct)
    {
        try
        {
            return await context.RepoProvider.GetHeadCommitShaAsync(context.Run.WorkspacePath!, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Pipeline {RunId} {Purpose}", context.Run.RunId, purpose);
            return null;
        }
    }

    // ── Static gate-result builders ───────────────────────────────────────────

    /// <summary>
    /// Builds a <see cref="GateResult"/> for an external CI poll result and emits a UI status line.
    /// Used by both <see cref="QualityGateExecutor.AppendExternalCiIfNeededAsync"/> and
    /// <see cref="WaitForPostPrCiAsync"/>.
    /// </summary>
    internal static GateResult BuildCiGateResult(
        bool ciPassed,
        PipelineRunStatus ciStatus,
        IReadOnlyDictionary<long, string>? ciLogPaths,
        string detailsPrefix,
        string uiPrefix,
        IPipelineCallbacks callbacks)
    {
        var details = ciPassed
            ? $"{detailsPrefix} passed. {ciStatus.Jobs.Count} job(s) completed."
            : QualityGateValidator.BuildCiFailureDetails(ciStatus, ciLogPaths);

        // GateName is intentionally "External CI" for all call sites.
        var gate = new GateResult
        {
            GateName = "External CI",
            Passed = ciPassed,
            Details = details
        };

        callbacks.EmitOutputLine(ciPassed
            ? $"✅ {uiPrefix} passed ({ciStatus.Jobs.Count} jobs)"
            : $"❌ {uiPrefix} failed: {details}");

        return gate;
    }

    /// <summary>
    /// Builds a <see cref="GateResult"/> for an external CI timeout.
    /// </summary>
    internal static GateResult BuildCiTimeoutGateResult(TimeSpan timeout, string prefix) =>
        new GateResult
        {
            GateName = "External CI",
            Passed = false,
            Details = $"{prefix} timed out after {timeout}"
        };

    /// <summary>
    /// Builds a <see cref="GateResult"/> for an unexpected external CI exception.
    /// </summary>
    internal static GateResult BuildCiErrorGateResult(string prefix, string message) =>
        new GateResult
        {
            GateName = "External CI",
            Passed = false,
            Details = $"{prefix} error: {message}"
        };

    // ── Private helpers: PollCiWithNotStartedRetryAsync decomposition ─────────

    /// <summary>
    /// Waits for CI runs to appear on the current SHA and, if they appear,
    /// waits for completion. Returns the final status, or null if no runs appeared
    /// within <see cref="PipelineConfiguration.CiNotStartedTimeout"/>.
    /// </summary>
    private async Task<PipelineRunStatus?> TryCompleteCiOnCurrentShaAsync(
        IPipelineProvider pipelineProvider,
        PipelineRun run,
        string? pollSha,
        PipelineConfiguration config,
        CancellationToken ct)
    {
        var appeared = await WaitForCiRunsToAppearAsync(
            pipelineProvider, run.BranchName!, pollSha,
            config.CiNotStartedTimeout, config.ExternalCiPollInterval, ct);

        if (!appeared)
            return null;

        return await pipelineProvider.WaitForCompletionAsync(
            run.BranchName!, pollSha, config.ExternalCiTimeout, ct);
    }

    /// <summary>
    /// Performs the last-check-before-re-push guard and the branch-wide running/passed
    /// detection. Returns a terminal status when one of these conditions fires, or null
    /// when the caller should proceed to creating an empty re-trigger commit.
    /// </summary>
    private async Task<PipelineRunStatus?> TryDetectAlreadyRunningCiAsync(
        IPipelineProvider pipelineProvider,
        PipelineRun run,
        string? pollSha,
        PipelineConfiguration config,
        IPipelineCallbacks callbacks,
        CancellationToken ct)
    {
        // Final SHA-specific check before re-pushing — avoid racing with GitHub's delayed trigger
        var lastCheck = await pipelineProvider.GetRunStatusAsync(run.BranchName!, pollSha, ct);
        if (lastCheck.State != PipelineRunState.Pending || lastCheck.Jobs.Count > 0)
        {
            _logger.Information("Pipeline {RunId} CI appeared just before re-push (race avoided), proceeding to full wait", run.RunId);
            return await pipelineProvider.WaitForCompletionAsync(
                run.BranchName!, pollSha, config.ExternalCiTimeout, ct);
        }

        // Branch-wide check: detect if CI is already running or passed on a prior SHA of this branch.
        var branchStatus = await pipelineProvider.GetRunStatusAsync(run.BranchName!, commitSha: null, ct);
        if (branchStatus?.State == PipelineRunState.Passed)
        {
            _logger.Information(
                "Pipeline {RunId} CI already passed on a prior SHA on branch {Branch} — skipping re-trigger",
                run.RunId, run.BranchName);
            callbacks.EmitOutputLine("✅ CI already passed on this branch — skipping re-trigger");
            return branchStatus;
        }

        if (branchStatus?.State == PipelineRunState.Running)
        {
            _logger.Information(
                "Pipeline {RunId} CI already running on branch {Branch} — waiting for completion instead of re-triggering",
                run.RunId, run.BranchName);
            callbacks.EmitOutputLine("⏳ CI already running on this branch — waiting for completion...");
            return await pipelineProvider.WaitForCompletionAsync(
                run.BranchName!, commitSha: null, config.ExternalCiTimeout, ct);
        }

        return null;
    }

    /// <summary>
    /// Builds a deterministic CI-never-started failure status, sets
    /// <see cref="PipelineRun.FailureReason"/> and <see cref="PipelineRun.FailureCategory"/>,
    /// and emits a UI error line. The returned status carries
    /// <see cref="PipelineRunStatus.IsInfrastructureFailure"/> = <c>true</c> so that the
    /// quality-gate retry loop does not invoke the LLM fix agent — this is an infrastructure
    /// problem (CI never triggered), not a code-level failure.
    /// </summary>
    private PipelineRunStatus BuildNotStartedFailureStatus(
        PipelineRun run, int maxRetries, IPipelineCallbacks callbacks)
    {
        var msg = $"CI never started after {maxRetries} retries";
        run.FailureReason = msg;
        run.FailureCategory = FailureReason.InfrastructureFailure;
        _logger.Error("Pipeline {RunId} {Message} — failing run (InfrastructureFailure)", run.RunId, msg);
        callbacks.EmitOutputLine($"❌ {msg} — treating as infrastructure failure (no code fix needed)");
        return new PipelineRunStatus
        {
            State = PipelineRunState.Failed,
            Jobs = Array.Empty<PipelineJobResult>(),
            IsInfrastructureFailure = true
        };
    }

    // ── Private helpers: PollAndHandleInfraRetryAsync decomposition ───────────

    /// <summary>
    /// Runs the branch-moved cancellation retry loop: when CI is Cancelled and the branch HEAD
    /// has moved to a new commit, re-enters CI polling on the new HEAD SHA.
    /// Returns updated (ciPassed, ciStatus).
    /// </summary>
    private async Task<(bool ciPassed, PipelineRunStatus ciStatus)> HandleBranchMovedRetryLoopAsync(
        QualityGateContext context,
        PipelineRunStatus ciStatus,
        bool ciPassed,
        string? initialPollSha,
        PipelineConfiguration config,
        IPipelineCallbacks callbacks,
        CancellationToken pollCt)
    {
        var run = context.Run;
        var branchMovedRetries = 0;
        var lastPolledSha = initialPollSha;

        // TODO [WARNING]: The two exit conditions from this loop are handled identically:
        // (a) HEAD unchanged → genuine pre-emption, infra-retry path correct.
        // (b) branchMovedRetries >= CiCancelledMoveMaxRetries → retries exhausted.
        // Consider adding an explicit log/comment when exiting due to exhausted retries.
        while (ciStatus.State == PipelineRunState.Cancelled
               && run.WorkspacePath != null
               && branchMovedRetries < config.CiCancelledMoveMaxRetries)
        {
            var currentHead = await TryReadHeadShaAsync(context, "could not read HEAD after Cancelled", pollCt);

            if (currentHead == null || currentHead == lastPolledSha)
                break;

            branchMovedRetries++;
            _logger.Information(
                "Pipeline {RunId} CI cancelled because branch moved ({OldSha} → {NewSha}), re-polling on new HEAD (attempt {N}/{Max})",
                run.RunId, lastPolledSha, currentHead, branchMovedRetries, config.CiCancelledMoveMaxRetries);
            callbacks.EmitOutputLine(
                $"⏳ CI superseded by new commit on branch — re-polling on updated HEAD (attempt {branchMovedRetries}/{config.CiCancelledMoveMaxRetries})...");

            lastPolledSha = currentHead;
            ciStatus = await PollCiWithNotStartedRetryAsync(context, currentHead, config, callbacks, pollCt);

            if (ciStatus.State is PipelineRunState.ConflictRestart or PipelineRunState.PrMerged or PipelineRunState.PrClosed)
                return (false, ciStatus);

            ciPassed = ciStatus.State == PipelineRunState.Passed;
        }

        return (ciPassed, ciStatus);
    }

    // ── Private helpers: ExecuteInfraRetryAsync ───────────────────────────────

    /// <summary>
    /// Performs one infrastructure-failure retry: increments the counter, logs, creates an empty
    /// commit, re-pushes, and polls CI again. Returns (ciPassed, newStatus, ciLogPaths).
    /// The <paramref name="ct"/> must be the timeout-budgeted token from
    /// <see cref="PollAndHandleInfraRetryAsync"/>.
    /// </summary>
    private async Task<(bool ciPassed, PipelineRunStatus ciStatus, IReadOnlyDictionary<long, string>? ciLogPaths)>
        ExecuteInfraRetryAsync(
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

        await PushInfraRetryCommitAsync(context, config, run, ct);

        var retrySha = await TryReadHeadShaAsync(context, "could not read HEAD commit SHA for infra retry", ct);

        callbacks.EmitOutputLine("⏳ Waiting for external CI (infrastructure retry)...");
        var ciStatus = await PollCiWithNotStartedRetryAsync(context, retrySha, config, callbacks, ct);
        var ciPassed = ciStatus.State == PipelineRunState.Passed;

        IReadOnlyDictionary<long, string>? ciLogPaths = (!ciPassed && run.WorkspacePath != null)
            ? _ciLogWriter.WriteJobLogs(ciStatus, run.WorkspacePath, run.RunId)
            : null;

        return (ciPassed, ciStatus, ciLogPaths);
    }

    /// <summary>
    /// Creates an empty commit and pushes it to re-trigger CI after an infrastructure failure.
    /// Extracted from <see cref="ExecuteInfraRetryAsync"/> to keep that method under 60 lines.
    /// </summary>
    private static async Task PushInfraRetryCommitAsync(
        QualityGateContext context,
        PipelineConfiguration config,
        PipelineRun run,
        CancellationToken ct)
    {
        // TODO [WARNING] (#2359): The empty-commit push below precedes PollCiWithNotStartedRetryAsync,
        // which means if PollCiWithNotStartedRetryAsync detects a Conflicted PR on the infra-retry path,
        // one unnecessary empty commit has already been sent to the conflicted branch. The ConflictRestart
        // status still propagates correctly, but consider calling CheckForMergeConflictAsync before the
        // CommitAllAsync/PushBranchAsync below, mirroring the primary path.
        await context.RepoProvider.CommitAllAsync(run.WorkspacePath!,
            $"chore: re-trigger CI after infrastructure failure ({run.InfrastructureRetryCount})",
            config.BlacklistedPaths, allowEmpty: true, ct,
            config.PipelineInjectedPaths);
        await context.RepoProvider.PushBranchAsync(run.WorkspacePath!, run.BranchName!, forcePush: true, ct);
    }

    // ── Private helpers: WaitForPostPrCiAsync decomposition ──────────────────

    /// <summary>
    /// Runs the actual CI poll for post-PR CI, recording post-PR CI duration, handling
    /// the InfrastructureRetryCount restore, and mapping exceptions to gate results.
    /// Extracted to keep <see cref="WaitForPostPrCiAsync"/> under 60 lines.
    /// The inner finally restores InfrastructureRetryCount; the outer finally in
    /// WaitForPostPrCiAsync records step telemetry (guarded by !ct.IsCancellationRequested).
    /// </summary>
    private async Task<GateResult> PollPostPrCiWithTelemetryAsync(
        QualityGateContext context,
        string? commitSha,
        PipelineConfiguration config,
        IPipelineCallbacks callbacks,
        int priorInfraRetryCount,
        CancellationToken ct)
    {
        var run = context.Run;
        GateResult ciGate;
        try
        {
            var ciPollStopwatch = Stopwatch.StartNew();
            // Apply the same single-window ExternalCiTimeout budget as the pre-PR path
            // (RunExternalCiPollAsync in QualityGateExecutor.ExternalCi.cs). Without this,
            // PollAndHandleInfraRetryAsync would receive the raw pipeline token (no timeout),
            // allowing post-PR CI to block indefinitely when CI hangs or infra-retry loops
            // run repeatedly. The linked token fires after ExternalCiTimeout, and the
            // catch (OperationCanceledException) when (!ct.IsCancellationRequested) arm
            // below then produces the "Post-PR CI timed out after X" gate result.
            using var timeoutCts = new CancellationTokenSource(config.ExternalCiTimeout);
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            var (ciPassed, ciStatus, ciLogPaths) = await PollAndHandleInfraRetryAsync(
                context, commitSha, config, callbacks, linkedCts.Token);

            _metrics.PostPrCiDuration.Record(
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
            // Restore the accumulated count so the run summary reflects total infra retries
            // across both pre-PR and post-PR CI polls.
            // TODO [WARNING]: This inner finally is intentionally distinct from the outer finally
            // in WaitForPostPrCiAsync (which records step telemetry). The two-layer nesting must
            // be preserved: the outer finally must not assume InfrastructureRetryCount has already
            // been restored when it records metrics, because the inner finally runs first (closest
            // to the throw site). Removing or merging these finally blocks would break the invariant
            // that telemetry is recorded with the correct count. See review finding from DotNetSpecialist.
            run.InfrastructureRetryCount += priorInfraRetryCount;
        }

        return ciGate;
    }

    // ── Private helper: CheckForMergeConflictAsync ────────────────────────────

    /// <summary>
    /// Resolves the pull request number for the current run, preferring
    /// <see cref="PipelineRun.PullRequestNumber"/> (string) and falling back to
    /// <see cref="PipelineRun.LinkedPullRequest"/>.<c>Number</c> (int).
    /// Returns <c>null</c> if neither is available or parseable.
    /// </summary>
    private static int? ResolvePullRequestNumber(PipelineRun run)
    {
        var raw = run.PullRequestNumber ?? run.LinkedPullRequest?.Number.ToString();
        return raw is not null && int.TryParse(raw, out var n) ? n : null;
    }

    /// <summary>
    /// Checks whether the PR associated with the current run is still open.
    /// Returns a <see cref="PipelineRunStatus"/> with <see cref="PipelineRunState.PrMerged"/>
    /// or <see cref="PipelineRunState.PrClosed"/> if the PR is no longer open, or <c>null</c>
    /// if the PR is open or the check fails (fail-open).
    /// </summary>
    private async Task<PipelineRunStatus?> CheckPullRequestStillOpenAsync(
        QualityGateContext context,
        IPipelineCallbacks callbacks,
        CancellationToken ct)
    {
        var run = context.Run;
        var prNum = ResolvePullRequestNumber(run);
        if (prNum is null)
            return null;  // No PR number resolvable — fail open

        PullRequestState state;
        try
        {
            state = await context.RepoProvider.GetPullRequestStateAsync(prNum.Value, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Pipeline {RunId} PR state check failed for PR #{PrNum} — falling through", run.RunId, prNum);
            return null;  // Fail open on any error
        }

        return state switch
        {
            PullRequestState.Merged => BuildPrMergedStatus(run, prNum.Value, callbacks),
            PullRequestState.Closed => BuildPrClosedStatus(run, prNum.Value, callbacks),
            _ => null   // Open or unknown — keep polling
        };
    }

    /// <summary>
    /// Builds a terminal <see cref="PipelineRunStatus"/> for a run whose PR was merged.
    /// Marks the run completed and transitions to <see cref="PipelineStep.PrMerged"/>.
    /// </summary>
    // TODO [WARNING] (DotNetSpecialist): run.MarkCompleted() is not idempotent — it overwrites CompletedAt/CompletedAtOffset
    // on every call. CheckPullRequestStillOpenAsync may be invoked twice in the same loop iteration (once at the per-iteration
    // check and again inside the attempt >= maxRetries block), so MarkCompleted() could be called twice for the same run,
    // causing CompletedAt to be reset to a later timestamp. Resolving the redundant double-call (see TODO above in
    // PollCiWithNotStartedRetryAsync) eliminates this risk entirely.
    private PipelineRunStatus BuildPrMergedStatus(PipelineRun run, int prNum, IPipelineCallbacks callbacks)
    {
        run.FinalLabel = null;    // Run succeeds — no error label
        run.FailureReason = null;
        // TODO [WARNING] (DotNetSpecialist): run.CurrentStep is mutated before run.MarkCompleted() stamps
        // CompletedAt. A concurrent reader observing the PipelineRun object between these two lines will see
        // CurrentStep = PrMerged with CompletedAt = null. Swapping the order (MarkCompleted() first, then
        // CurrentStep assignment) would close this window. This is consistent with ConflictRestart ordering.
        run.CurrentStep = PipelineStep.PrMerged;
        callbacks.EmitOutputLine($"✅ PR #{prNum} was merged — nothing left to do");
        callbacks.TransitionTo(PipelineStep.PrMerged);
        run.MarkCompleted();
        _logger.Information("Pipeline {RunId} PR #{PrNum} was merged — terminating run as Succeeded", run.RunId, prNum);
        return new PipelineRunStatus { State = PipelineRunState.PrMerged, Jobs = Array.Empty<PipelineJobResult>() };
    }

    /// <summary>
    /// Builds a terminal <see cref="PipelineRunStatus"/> for a run whose PR was closed without merging.
    /// Marks the run completed and transitions to <see cref="PipelineStep.PrClosed"/>.
    /// </summary>
    private PipelineRunStatus BuildPrClosedStatus(PipelineRun run, int prNum, IPipelineCallbacks callbacks)
    {
        run.FinalLabel = AgentLabels.Cancelled;
        // TODO [WARNING] (DotNetSpecialist): Same ordering concern as BuildPrMergedStatus — CurrentStep is
        // set before MarkCompleted(), so a concurrent reader sees CurrentStep = PrClosed with CompletedAt = null.
        run.CurrentStep = PipelineStep.PrClosed;
        callbacks.EmitOutputLine($"🚫 PR #{prNum} was closed without merge — cancelling run");
        callbacks.TransitionTo(PipelineStep.PrClosed);
        run.MarkCompleted();
        _logger.Information("Pipeline {RunId} PR #{PrNum} was closed without merge — terminating run as Cancelled", run.RunId, prNum);
        return new PipelineRunStatus { State = PipelineRunState.PrClosed, Jobs = Array.Empty<PipelineJobResult>() };
    }

    /// <summary>
    /// Checks whether the PR associated with the current run is conflicted with the base branch.
    /// Returns a <see cref="PipelineRunStatus"/> with <see cref="PipelineRunState.ConflictRestart"/>
    /// if the PR is <see cref="PrMergeabilityStatus.Conflicted"/>, or <c>null</c> otherwise.
    /// </summary>
    private async Task<PipelineRunStatus?> CheckForMergeConflictAsync(
        QualityGateContext context,
        IPipelineCallbacks callbacks,
        CancellationToken ct)
    {
        var run = context.Run;

        var prNum = ResolvePullRequestNumber(run);
        if (prNum is null)
        {
            _logger.Debug("Pipeline {RunId} skipping mergeability check — no resolvable PR number", run.RunId);
            return null;
        }

        PrMergeabilityStatus mergeability;
        try
        {
            mergeability = await context.RepoProvider.IsPullRequestBehindBaseAsync(prNum.Value, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Debug(ex, "Pipeline {RunId} mergeability check failed for PR #{PrNum}, falling through to normal re-trigger", run.RunId, prNum);
            return null;
        }

        if (mergeability == PrMergeabilityStatus.Conflicted)
        {
            _logger.Warning("Pipeline {RunId} PR #{PrNum} is conflicted (dirty) — signalling conflict restart", run.RunId, prNum);
            callbacks.EmitOutputLine("⚠️ PR has merge conflicts with main — restarting pipeline from beginning...");
            return new PipelineRunStatus { State = PipelineRunState.ConflictRestart, Jobs = Array.Empty<PipelineJobResult>() };
        }

        _logger.Debug("Pipeline {RunId} PR #{PrNum} mergeability is {Status} — continuing with normal re-trigger", run.RunId, prNum, mergeability);
        return null;
    }

    // ── Private helper: WaitForCiRunsToAppearAsync ────────────────────────────

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
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.Elapsed < timeout)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                var status = await provider.GetRunStatusAsync(branchName, commitSha, ct);
                if (status.State != PipelineRunState.Pending || status.Jobs.Count > 0)
                    return true;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.Debug(ex, "WaitForCiRunsToAppearAsync transient error polling {Branch}, will retry", branchName);
            }

            await Task.Delay(pollInterval, ct);
        }
        return false;
    }

    // ── Constants ─────────────────────────────────────────────────────────────

    private const string PostPrCiPrefix = "Post-PR CI";
}
