using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Terminal-state outcomes from a successful external CI poll. Used by
/// <see cref="QualityGateExecutor.ClassifyCiResult"/> to dispatch the five possible results
/// into named handlers inside <see cref="QualityGateExecutor.AppendExternalCiIfNeededAsync"/>.
/// Mirrors the <see cref="Models.RetryOutcome"/>/<see cref="QualityGateExecutor.ClassifyRetryOutcome"/> pattern.
/// </summary>
internal enum CiTerminalOutcome
{
    ConflictRestart,
    PrMerged,
    PrClosed,

    /// <summary>
    /// CI ran and returned a status with <see cref="PipelineRunStatus.IsInfrastructureFailure"/> = true
    /// (e.g. CI never started, or <see cref="CiFailureClassifier"/> classified the run as Infrastructure).
    /// Distinct from the exhausted-retries infra-failure path (provider API entirely unreachable),
    /// which is handled by <see cref="QualityGateExecutor.BuildExhaustedInfraGate"/> before this
    /// classifier is reached.
    /// </summary>
    InfrastructureFailure,

    NormalResult
}

public partial class QualityGateExecutor
{
    private const string ExternalCiName = "External CI";

    /// <summary>
    /// Carries the result of <see cref="RunInfraRetryPollingAsync"/> back to
    /// <see cref="AppendExternalCiIfNeededAsync"/>.
    /// <list type="bullet">
    ///   <item><term>CiResult has value</term><description>Polling succeeded; proceed to <see cref="ClassifyCiResult"/>.</description></item>
    ///   <item><term>CiResult is null, ExhaustedRetries = true</term><description>All retries consumed; produce an infra-failure gate.</description></item>
    ///   <item><term>CiResult is null, ExhaustedRetries = false</term><description>MaxInfrastructureRetries = 0; caller must re-throw <see cref="LastException"/>.</description></item>
    /// </list>
    /// </summary>
    internal readonly record struct InfraPollingResult(
        (bool ciPassed, PipelineRunStatus ciStatus, IReadOnlyDictionary<long, string>? ciLogPaths)? CiResult,
        Exception? LastException,
        bool ExhaustedRetries);

    /// <summary>
    /// Determines which ValidateAsync overload to call based on the QGC context:
    /// - Non-empty QualityGateConfigs → multi-QGC validation
    /// - Empty (none matched or none configured) → skip, return passing report
    /// </summary>
    private async Task<QualityGateReport> RunQualityGateValidationAsync(
        QualityGateContext context, string workspacePath, CancellationToken ct)
    {
        if (context.QualityGateConfigs.Count > 0)
        {
            return await _qualityGateValidator.ValidateAsync(
                workspacePath,
                context.QualityGateConfigs,
                ct,
                reportEvent: context.ReportPipelineRunEvent);
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

            // Infrastructure-retry polling: when RunExternalCiPollAsync throws a transient exception
            // (e.g. provider API 5xx), retry up to MaxInfrastructureRetries times before returning
            // an infra-failure gate. OperationCanceledException propagates unconditionally so timeout
            // and pipeline cancellation are not swallowed. Exceptions from CommitAndPushAsync and
            // CreateDraftPrIfNotExists above fall through to the outer catch (unchanged).
            var pollingResult = await RunInfraRetryPollingAsync(context, ct);

            if (pollingResult.CiResult is null && !pollingResult.ExhaustedRetries)
            {
                // MaxInfrastructureRetries = 0: no retries configured — re-throw so the outer
                // catch (Exception ex) handles this with the original pre-fix behaviour.
                throw pollingResult.LastException!;
            }

            if (pollingResult.ExhaustedRetries)
            {
                // Re-poll attempts exhausted: provider API was unreachable for all attempts.
                // Classify as infrastructure failure so RunRetryLoopAsync skips the LLM invocation.
                // Distinct from CiTerminalOutcome.InfrastructureFailure (CI ran but reported infra failure).
                ciGate = BuildExhaustedInfraGate(pollingResult.LastException!);
            }
            else
            {
                var (ciPassed, ciStatus, ciLogPaths) = pollingResult.CiResult!.Value;
                switch (ClassifyCiResult(ciStatus))
                {
                    case CiTerminalOutcome.ConflictRestart:
                        return BuildConflictRestartReport(context.Run, report, context.Callbacks);
                    case CiTerminalOutcome.PrMerged:
                        return BuildPrMergedReport(report);
                    case CiTerminalOutcome.PrClosed:
                        return BuildPrClosedReport(report);
                    case CiTerminalOutcome.InfrastructureFailure:
                        // CI was polled successfully but returned an infrastructure-failure status
                        // (e.g. CI never started, or CiFailureClassifier.Infrastructure-classified).
                        // Distinct from ExhaustedRetries above (provider API was unreachable).
                        ciGate = BuildInfraFlaggedGateResult(ciPassed, ciStatus, ciLogPaths, context.Callbacks);
                        break;
                    default: // NormalResult
                        ciGate = CiPollingCoordinator.BuildCiGateResult(
                            ciPassed, ciStatus, ciLogPaths, "CI", ExternalCiName, context.Callbacks);
                        break;
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ciGate = CiPollingCoordinator.BuildCiTimeoutGateResult(context.Config.ExternalCiTimeout, ExternalCiName);
        }
        catch (OperationCanceledException) { throw; }
        catch (InvalidOperationException ex) when (ex.Message.Contains("may not create or update workflow files", StringComparison.OrdinalIgnoreCase))
        {
            _logger.Warning(ex, "Pipeline {RunId} push refused for workflow files", context.Run.RunId);
            var baseBranch = context.RepoProvider.BaseBranch;
            var message = $"Push refused: the pipeline's GitHub App may not create or update files under .github/workflows, and this branch's workflow files differ from origin/{baseBranch}. If your change edits .github/workflows, revert that part (the pipeline cannot push it). Otherwise main changed them after this run started: run git fetch origin {baseBranch} && git rebase origin/{baseBranch}, resolve any conflicts, and keep your changes.";
            var errorGate = CiPollingCoordinator.BuildCiErrorGateResult(ExternalCiName, message);
            ciGate = BuildWorkflowPushRefusedGate(context, errorGate);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Pipeline {RunId} external CI check failed, treating as gate failure", context.Run.RunId);
            ciGate = CiPollingCoordinator.BuildCiErrorGateResult(ExternalCiName, ex.Message);
        }

        return new QualityGateReport
        {
            Compilation = report.Compilation,
            Tests = report.Tests!, // null when Tests is null (build-only QGC / legacy deserialization path)
            ExternalCi = ciGate
        };
    }

    /// <summary>
    /// Retries <see cref="RunExternalCiPollAsync"/> up to
    /// <see cref="PipelineConfiguration.MaxInfrastructureRetries"/> times when it throws a
    /// transient exception. <see cref="OperationCanceledException"/> always propagates
    /// unconditionally. Returns an <see cref="InfraPollingResult"/> that distinguishes three
    /// outcomes: success, exhausted retries, and the MaxInfrastructureRetries = 0 re-throw case.
    /// Extracted from <see cref="AppendExternalCiIfNeededAsync"/> to reduce cognitive complexity
    /// (issue #3958).
    /// </summary>
    internal async Task<InfraPollingResult> RunInfraRetryPollingAsync(
        QualityGateContext context, CancellationToken ct)
    {
        (bool ciPassed, PipelineRunStatus ciStatus, IReadOnlyDictionary<long, string>? ciLogPaths)? ciResult = null;
        var localRetry = 0;
        Exception? lastEx = null;
        while (true)
        {
            try
            {
                ciResult = await RunExternalCiPollAsync(context, ct);
                break;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                lastEx = ex;
                if (localRetry >= context.Config.MaxInfrastructureRetries)
                    break; // exhausted — produce infra-failure gate below
                localRetry++;
                context.Run.InfrastructureRetryCount++;
                _logger.Warning(ex,
                    "Pipeline {RunId} could not read the CI status ({Error}), polling again ({Attempt}/{Max})",
                    context.Run.RunId, ex.Message, localRetry, context.Config.MaxInfrastructureRetries);
                context.Callbacks.EmitOutputLine(
                    $"⚠️ Could not read the CI status — polling again ({localRetry}/{context.Config.MaxInfrastructureRetries})...");
                await Task.Delay(context.Config.ExternalCiPollInterval, ct);
            }
        }

        return new InfraPollingResult(ciResult, lastEx, ExhaustedRetries: ciResult is null && localRetry > 0);
    }

    /// <summary>
    /// Classifies a successful CI poll result into a <see cref="CiTerminalOutcome"/>.
    /// Pure function — no I/O, no side effects.
    /// State-equality checks precede the <see cref="PipelineRunStatus.IsInfrastructureFailure"/>
    /// check so that a status with both a terminal <see cref="PipelineRunState"/> and the
    /// infrastructure-failure flag is routed to the correct State-based outcome.
    /// </summary>
    internal static CiTerminalOutcome ClassifyCiResult(PipelineRunStatus ciStatus)
    {
        if (ciStatus.State == PipelineRunState.ConflictRestart) return CiTerminalOutcome.ConflictRestart;
        if (ciStatus.State == PipelineRunState.PrMerged)        return CiTerminalOutcome.PrMerged;
        if (ciStatus.State == PipelineRunState.PrClosed)        return CiTerminalOutcome.PrClosed;
        if (ciStatus.IsInfrastructureFailure == true)           return CiTerminalOutcome.InfrastructureFailure;
        return CiTerminalOutcome.NormalResult;
    }

    /// <summary>
    /// Builds an infrastructure-failure <see cref="GateResult"/> for the exhausted-retries path:
    /// <see cref="RunExternalCiPollAsync"/> threw on every attempt (provider API unreachable).
    /// Sets <see cref="GateResult.IsInfrastructureFailure"/> so <c>RunRetryLoopAsync</c>
    /// short-circuits the LLM invocation.
    /// </summary>
    private static GateResult BuildExhaustedInfraGate(Exception lastEx)
    {
        var errorGate = CiPollingCoordinator.BuildCiErrorGateResult(
            ExternalCiName, "CI status unavailable — " + lastEx.Message);
        return new GateResult
        {
            GateName = errorGate.GateName,
            Passed = errorGate.Passed,
            Details = errorGate.Details,
            IsInfrastructureFailure = true
        };
    }

    /// <summary>
    /// Builds a <see cref="GateResult"/> for the <see cref="CiTerminalOutcome.InfrastructureFailure"/>
    /// path: CI ran but reported <see cref="PipelineRunStatus.IsInfrastructureFailure"/> = true.
    /// Propagates the infrastructure-failure flag so <c>RunRetryLoopAsync</c> short-circuits the LLM.
    /// </summary>
    private static GateResult BuildInfraFlaggedGateResult(
        bool ciPassed,
        PipelineRunStatus ciStatus,
        IReadOnlyDictionary<long, string>? ciLogPaths,
        IPipelineCallbacks callbacks)
    {
        var gate = CiPollingCoordinator.BuildCiGateResult(
            ciPassed, ciStatus, ciLogPaths, "CI", ExternalCiName, callbacks);
        return new GateResult
        {
            GateName = gate.GateName,
            Passed = gate.Passed,
            Details = gate.Details,
            TestsFailed = gate.TestsFailed,
            TestsPassed = gate.TestsPassed,
            TestsSkipped = gate.TestsSkipped,
            IsInfrastructureFailure = true
        };
    }

    /// <summary>
    /// Builds the <see cref="GateResult"/> for the workflow-file push-refused path.
    /// On the second or later consecutive push-refused failure the gate is classified as an
    /// infrastructure failure (so the retry loop does not waste LLM budget). On the first
    /// occurrence it is returned as a plain error gate so the agent gets a chance to remove the
    /// offending workflow file change.
    /// Extracted from <see cref="AppendExternalCiIfNeededAsync"/> to reduce cognitive complexity
    /// (issue #3958).
    /// </summary>
    private static GateResult BuildWorkflowPushRefusedGate(QualityGateContext context, GateResult errorGate)
    {
        var alreadyFailed = context.Run.RetryErrors.LastOrDefault()
            ?.Contains("Push refused: the pipeline's GitHub App", StringComparison.OrdinalIgnoreCase) == true;
        return alreadyFailed
            ? new GateResult
            {
                GateName = errorGate.GateName,
                Passed = errorGate.Passed,
                Details = errorGate.Details,
                IsInfrastructureFailure = true
            }
            : errorGate;
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

        using var ciSpan = PipelineTelemetry.ActivitySource.StartActivity("WaitForCi");
        ciSpan?.SetTag("pipeline.run_id", run.RunId);
        ciSpan?.SetTag("pipeline.run_type", run.RunType.ToString());
        ciSpan?.SetTag("pipeline.ci_path", "pre_pr");

        using var timeoutCts = new CancellationTokenSource(config.ExternalCiTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
        try
        {
            var result = await _ciPollingCoordinator.PollAndHandleInfraRetryAsync(
                context, commitSha, config, callbacks, linkedCts.Token);
            ciSpan?.SetTag("pipeline.ci_infra_retries", run.InfrastructureRetryCount);
            ciSpan?.SetTag("pipeline.ci_status", result.ciPassed ? "passed" : result.ciStatus.State.ToString().ToLowerInvariant());

            // Report CI wait duration server-side (issue #2979): replaces the agent-side
            // _externalCiDuration recording in the finally block.
            context.ReportPipelineRunEvent?.Invoke(new PipelineRunEventReport
            {
                Kind = PipelineRunEventKind.CiWait,
                DurationSeconds = ciPollStopwatch.Elapsed.TotalSeconds,
                Stage = PipelineTelemetry.CiWaitStages.PrePr,
                Result = result.ciPassed ? "pass" : "fail"
            });

            return result;
        }
        catch
        {
            ciSpan?.SetTag("pipeline.ci_infra_retries", run.InfrastructureRetryCount);
            ciSpan?.SetTag("pipeline.ci_status", "error");
            throw;
        }
        finally
        {
            // Duration recording moved to the try block above for server-side reporting (issue #2979).
            // The finally block is retained (with no-op body) to preserve the two-layer nesting
            // that the outer WaitForCi span relies on for correct attribute ordering.
            _ = ciPollStopwatch.Elapsed; // suppress unused-variable warning from compiler
        }
    }

    /// <summary>
    /// Handles the conflict-restart outcome from CI polling: marks the run for re-queue as
    /// agent:next and returns the appropriate conflict-restart report.
    /// Extracted from <see cref="AppendExternalCiIfNeededAsync"/> to keep that method under 60 lines.
    /// Delegates run mutations to <see cref="CiPollingCoordinator.ApplyConflictRestartToRun"/> so
    /// the post-PR path can reuse the same logic without duplication.
    /// </summary>
    private static QualityGateReport BuildConflictRestartReport(
        PipelineRun run, QualityGateReport report, IPipelineCallbacks callbacks)
    {
        var gate = CiPollingCoordinator.ApplyConflictRestartToRun(run, callbacks);
        return WithExternalCiOutcome(report, gate.Passed, gate.Details!);
    }

    /// <summary>
    /// Handles the PR-merged outcome from CI polling: the PR was already merged, so the run ends
    /// successfully with no further action. <see cref="PipelineStep.PrMerged"/> is a terminal step
    /// that maps to <see cref="WorkItemStatus.Succeeded"/> via <c>CompletionOutcomeResolver</c>.
    /// The output line and TransitionTo call were already emitted by BuildPrMergedStatus in
    /// <see cref="CiPollingCoordinator"/>, so only the report is built here.
    /// </summary>
    private static QualityGateReport BuildPrMergedReport(QualityGateReport report)
    {
        // run.CurrentStep is already set by BuildPrMergedStatus in CiPollingCoordinator.
        // This report is returned so ProceedToQualityGatesAsync can detect the terminal step
        // via the run.CurrentStep guard and return without further processing.
        return WithExternalCiOutcome(report, passed: true,
            details: "PR was merged — run ended Succeeded");
    }

    /// <summary>
    /// Handles the PR-closed outcome from CI polling: the PR was closed without merging, so the
    /// run ends as Cancelled. <see cref="PipelineStep.PrClosed"/> is a terminal step that maps to
    /// <see cref="WorkItemStatus.Cancelled"/> via <c>CompletionOutcomeResolver</c>.
    /// The output line and TransitionTo call were already emitted by BuildPrClosedStatus in
    /// <see cref="CiPollingCoordinator"/>, so only the report is built here.
    /// </summary>
    private static QualityGateReport BuildPrClosedReport(QualityGateReport report)
    {
        // run.CurrentStep is already set by BuildPrClosedStatus in CiPollingCoordinator.
        return WithExternalCiOutcome(report, passed: false,
            details: "PR was closed without merging — run ended Cancelled");
    }

    /// <summary>
    /// Constructs a <see cref="QualityGateReport"/> that preserves the <c>Compilation</c> and
    /// <c>Tests</c> pass-through from <paramref name="report"/> and sets the <c>ExternalCi</c>
    /// gate to the given <paramref name="passed"/> flag and <paramref name="details"/> string.
    /// Used by the three terminal Build* methods to eliminate duplicate
    /// <c>new GateResult { GateName = ExternalCiName, ... }</c> initializers.
    /// NOTE: <c>Tests = report.Tests</c> (no null-forgiving operator) is intentional — Tests may
    /// be null from legacy deserialization of build-only QGC payloads; the null is preserved as-is.
    /// </summary>
    private static QualityGateReport WithExternalCiOutcome(
        QualityGateReport report, bool passed, string details) =>
        new()
        {
            Compilation = report.Compilation,
            Tests = report.Tests, // null when Tests is null (build-only QGC / legacy deserialization path)
            ExternalCi = new GateResult
            {
                GateName = ExternalCiName,
                Passed = passed,
                Details = details
            }
        };

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
                _logger.Information("Pipeline {RunId} no changes after retry fix, creating empty commit to trigger CI", run.RunId); // NOSONAR S6667 — expected "No changes to commit"; the message says so
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
