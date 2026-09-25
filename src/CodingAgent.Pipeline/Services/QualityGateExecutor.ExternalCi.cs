using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services;

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

        if (!report.Compilation.Passed || !(report.Tests?.Passed ?? true)
            || context.PipelineProvider == null)
            return report;

        GateResult? ciGate = null;
        try
        {
            var skipCi = await CommitAndPushAsync(context, allowEmptyCommit, skipCiIfNoChanges, ct);
            if (skipCi)
                return report;

            await context.Callbacks.CreateDraftPrIfNotExists(context.Run, ct);
            var ciResult = await RunExternalCiPollAsync(context, ct);

            if (ciResult.ciStatus.State == PipelineRunState.ConflictRestart)
                return BuildConflictRestartReport(context.Run, report, context.Callbacks);

            if (ciResult.ciStatus.State == PipelineRunState.PrMerged)
                return BuildPrMergedReport(context.Run, report, context.Callbacks);

            if (ciResult.ciStatus.State == PipelineRunState.PrClosed)
                return BuildPrClosedReport(context.Run, report, context.Callbacks);

            ciGate = CiPollingCoordinator.BuildCiGateResult(
                ciResult.ciPassed, ciResult.ciStatus, ciResult.ciLogPaths, "CI", "External CI", context.Callbacks);

            // Propagate infrastructure failure flag so RunRetryLoopAsync can short-circuit LLM invocation
            if (ciResult.ciStatus.IsInfrastructureFailure)
                ciGate = new GateResult
                {
                    GateName = ciGate.GateName,
                    Passed = ciGate.Passed,
                    Details = ciGate.Details,
                    TestsFailed = ciGate.TestsFailed,
                    TestsPassed = ciGate.TestsPassed,
                    TestsSkipped = ciGate.TestsSkipped,
                    IsInfrastructureFailure = true
                };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ciGate = CiPollingCoordinator.BuildCiTimeoutGateResult(context.Config.ExternalCiTimeout, "External CI");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Pipeline {RunId} external CI check failed, treating as gate failure", context.Run.RunId);
            ciGate = CiPollingCoordinator.BuildCiErrorGateResult("External CI", ex.Message);
        }

        return new QualityGateReport
        {
            Compilation = report.Compilation,
            Tests = report.Tests!, // null when Tests is null (build-only QGC / legacy deserialization path)
            ExternalCi = ciGate
        };
    }

    /// <summary>
    /// Performs the commit SHA read, timeout-budgeted CI poll, and external-CI duration recording.
    /// Extracted from <see cref="AppendExternalCiIfNeededAsync"/> to keep that method under 60 lines.
    /// Returns (ciPassed, ciStatus, ciLogPaths).
    /// </summary>
    private async Task<(bool ciPassed, PipelineRunStatus ciStatus, IReadOnlyDictionary<long, string>? ciLogPaths)>
        RunExternalCiPollAsync(QualityGateContext context, CancellationToken ct)
    {
        var run = context.Run;
        var config = context.Config;
        var callbacks = context.Callbacks;

        string? commitSha = await _ciPollingCoordinator.TryReadHeadShaAsync(context, "could not read HEAD commit SHA", ct);
        callbacks.EmitOutputLine("⏳ Waiting for external CI...");
        var ciPollStopwatch = System.Diagnostics.Stopwatch.StartNew();

        using var timeoutCts = new CancellationTokenSource(config.ExternalCiTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        var result = await _ciPollingCoordinator.PollAndHandleInfraRetryAsync(
            context, commitSha, config, callbacks, linkedCts.Token);

        // TODO [WARNING]: _externalCiDuration.Record is only reached when PollAndHandleInfraRetryAsync
        // returns normally. If it throws (e.g. an unhandled provider exception), the duration is not
        // recorded and the stopwatch runs until the using-block disposes the CTSes on stack unwind.
        // The timeoutCts/linkedCts using declarations ensure disposal regardless, but the metric sample
        // is silently dropped for error paths. To fix: move _externalCiDuration.Record into a finally
        // block so it is emitted on both success and exception paths.
        // TODO: Duration includes infrastructure retry wait times — consider recording per-attempt duration
        _externalCiDuration.Record(
            ciPollStopwatch.Elapsed.TotalSeconds,
            PipelineTelemetry.BuildTags(run.RunType, run.ProjectId, run.ProjectName));

        return result;
    }

    /// <summary>
    /// Handles the conflict-restart outcome from CI polling: marks the run for re-queue as
    /// agent:next and returns the appropriate conflict-restart report.
    /// Extracted from <see cref="AppendExternalCiIfNeededAsync"/> to keep that method under 60 lines.
    /// </summary>
    private static QualityGateReport BuildConflictRestartReport(
        PipelineRun run, QualityGateReport report, IPipelineCallbacks callbacks)
    {
        // Conflict restart: PR is conflicted with main — re-queue as agent:next without creating a draft PR.
        // run.CurrentStep and run.FinalLabel are set here so PostCompletionBookkeepingAsync picks them up.
        run.FinalLabel = AgentLabels.Next;
        run.FailureReason = "PR conflicted with main — restarting pipeline";
        run.CurrentStep = PipelineStep.ConflictRestart;
        callbacks.EmitOutputLine("🔄 PR conflicted with main — re-queuing as agent:next for rework...");
        callbacks.TransitionTo(PipelineStep.ConflictRestart);
        run.MarkCompleted();
        return new QualityGateReport
        {
            Compilation = report.Compilation,
            Tests = report.Tests!, // null when Tests is null (build-only QGC / legacy deserialization path)
            ExternalCi = new GateResult
            {
                GateName = "External CI",
                Passed = false,
                Details = "Conflict restart — PR conflicted with main; re-dispatched as agent:next"
            }
        };
    }

    /// <summary>
    /// Handles the PR-merged outcome from CI polling: the PR was already merged, so the run ends
    /// successfully with no further action. <see cref="PipelineStep.PrMerged"/> is a terminal step
    /// that maps to <see cref="WorkItemStatus.Succeeded"/> via <c>CompletionOutcomeResolver</c>.
    /// </summary>
    // TODO [WARNING] (DotNetSpecialist): `run` and `callbacks` parameters are never used inside this method —
    // the output line and TransitionTo call were already emitted by BuildPrMergedStatus in CiPollingCoordinator.
    // Consider removing the unused parameters to make the contract explicit and avoid misleading future callers.
    private static QualityGateReport BuildPrMergedReport(
        PipelineRun run, QualityGateReport report, IPipelineCallbacks callbacks)
    {
        // run.CurrentStep is already set by BuildPrMergedStatus in CiPollingCoordinator.
        // This report is returned so ProceedToQualityGatesAsync can detect the terminal step
        // via the run.CurrentStep guard and return without further processing.
        return new QualityGateReport
        {
            Compilation = report.Compilation,
            Tests = report.Tests!,
            ExternalCi = new GateResult
            {
                GateName = "External CI",
                Passed = true,
                Details = "PR was merged — run ended Succeeded"
            }
        };
    }

    /// <summary>
    /// Handles the PR-closed outcome from CI polling: the PR was closed without merging, so the
    /// run ends as Cancelled. <see cref="PipelineStep.PrClosed"/> is a terminal step that maps to
    /// <see cref="WorkItemStatus.Cancelled"/> via <c>CompletionOutcomeResolver</c>.
    /// </summary>
    // TODO [WARNING] (DotNetSpecialist): `run` and `callbacks` parameters are never used inside this method —
    // the output line and TransitionTo call were already emitted by BuildPrClosedStatus in CiPollingCoordinator.
    // Consider removing the unused parameters to make the contract explicit and avoid misleading future callers.
    private static QualityGateReport BuildPrClosedReport(
        PipelineRun run, QualityGateReport report, IPipelineCallbacks callbacks)
    {
        // run.CurrentStep is already set by BuildPrClosedStatus in CiPollingCoordinator.
        return new QualityGateReport
        {
            Compilation = report.Compilation,
            Tests = report.Tests!,
            ExternalCi = new GateResult
            {
                GateName = "External CI",
                Passed = false,
                Details = "PR was closed without merging — run ended Cancelled"
            }
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
}
