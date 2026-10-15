using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for RunDiagnoser.Diagnose, one per rule, with boundary and edge cases.
/// All DateTimeOffset values are fixed — never DateTimeOffset.UtcNow.
/// </summary>
public class RunDiagnoserTests
{
    // ── Builder helper ────────────────────────────────────────────────────────

    private static readonly DateTimeOffset BaseTime =
        new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Builds a minimal failed PipelineRunSummary to satisfy required members.</summary>
    private static PipelineRunSummary Failed(
        string? runId = null,
        string? issueIdentifier = null,
        FailureReason? failureCategory = null,
        string? failureReason = null,
        PipelineStep? lastActiveStep = null,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null,
        IReadOnlyList<GateOutcome>? gateOutcomes = null,
        IReadOnlyList<string>? podLogTail = null,
        IReadOnlyList<string>? outputTail = null,
        int retryCount = 0,
        string? repoProviderConfigId = null) =>
        new PipelineRunSummary
        {
            RunId = runId ?? Guid.NewGuid().ToString(),
            IssueIdentifier = issueIdentifier ?? "1",
            IssueTitle = "Test issue",
            FinalStep = PipelineStep.Failed,
            FailureCategory = failureCategory,
            FailureReason = failureReason,
            LastActiveStep = lastActiveStep,
            StartedAtOffset = startedAt ?? BaseTime,
            CompletedAtOffset = completedAt ?? BaseTime.AddMinutes(5),
            QualityGateOutcomes = gateOutcomes,
            PodLogTail = podLogTail,
            OutputTail = outputTail,
            RetryCount = retryCount,
            RepoProviderConfigId = repoProviderConfigId,
        };

    // ── Early-return tests ────────────────────────────────────────────────────

    [Fact]
    public void Diagnose_NotFailed_ReturnsNull()
    {
        var run = new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "1",
            IssueTitle = "Test",
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = BaseTime,
        };

        Assert.Null(RunDiagnoser.Diagnose(run));
    }

    [Fact]
    public void Diagnose_GateRejected_ReturnsNull()
    {
        var run = Failed(failureCategory: FailureReason.GateRejected);
        Assert.Null(RunDiagnoser.Diagnose(run));
    }

    // ── Rule 1: platform incident ─────────────────────────────────────────────
    // TODO: [WARNING] All boundary tests for rule 1 place "others" at a completedAt strictly AFTER
    // run.CompletedAtOffset (positive offsets). The Abs() call in RunDiagnoser makes both directions
    // equivalent, but no test verifies this. Add a test where others have completedAt BEFORE the run
    // (negative delta, e.g. BaseTime.AddMinutes(-5)) to confirm Abs() is not accidentally removed.
    // Without such a test, removing Abs() would silently break the negative-delta case.

    [Fact]
    public void Diagnose_ThreeOtherPlatformFailuresWithin10Minutes_IsTransient()
    {
        var run = Failed(
            runId: "run-0",
            issueIdentifier: "1",
            failureCategory: FailureReason.ExitCodeFailure,
            completedAt: BaseTime);

        var others = Enumerable.Range(1, 3)
            .Select(i => Failed(
                runId: $"run-{i}",
                issueIdentifier: $"{i + 10}",
                failureCategory: FailureReason.ExitCodeFailure,
                completedAt: BaseTime.AddMinutes(5)))
            .ToList();

        var result = RunDiagnoser.Diagnose(run, others);

        Assert.NotNull(result);
        Assert.Equal(RunDiagnosisVerdict.Transient, result.Verdict);
        Assert.Equal("Several runs failed at the same time", result.Title);
        Assert.Contains("3 other runs", result.Explanation);
    }

    [Fact]
    public void Diagnose_ThreeOtherPlatformFailures_Exactly10MinutesAway_IsTransient()
    {
        // Exactly 10 minutes — must count (boundary is inclusive)
        var run = Failed(
            runId: "run-0",
            issueIdentifier: "1",
            failureCategory: FailureReason.InfrastructureFailure,
            completedAt: BaseTime);

        var others = Enumerable.Range(1, 3)
            .Select(i => Failed(
                runId: $"run-{i}",
                issueIdentifier: $"{i + 10}",
                failureCategory: FailureReason.InfrastructureFailure,
                completedAt: BaseTime.AddMinutes(10)))
            .ToList();

        var result = RunDiagnoser.Diagnose(run, others);

        Assert.NotNull(result);
        Assert.Equal(RunDiagnosisVerdict.Transient, result.Verdict);
    }

    [Fact]
    public void Diagnose_ThreeOtherPlatformFailures_10MinutesAnd1SecondAway_IsNotTransientIncident()
    {
        // 10 minutes + 1 second — must NOT count; falls through to rule 5 (InfrastructureFailure)
        var run = Failed(
            runId: "run-0",
            issueIdentifier: "1",
            failureCategory: FailureReason.InfrastructureFailure,
            completedAt: BaseTime);

        var others = Enumerable.Range(1, 3)
            .Select(i => Failed(
                runId: $"run-{i}",
                issueIdentifier: $"{i + 10}",
                failureCategory: FailureReason.InfrastructureFailure,
                completedAt: BaseTime.AddMinutes(10).AddSeconds(1)))
            .ToList();

        var result = RunDiagnoser.Diagnose(run, others);

        Assert.NotNull(result);
        // Falls through to rule 5 (InfrastructureFailure → Transient with different title)
        Assert.Equal(RunDiagnosisVerdict.Transient, result.Verdict);
        Assert.Equal("The platform could not run the job", result.Title);
    }

    [Fact]
    public void Diagnose_TwoOtherPlatformFailures_IsNotTransientIncident()
    {
        // Only 2 others — not enough for rule 1; falls through to rule 6 (ExitCodeFailure → Agent)
        var run = Failed(
            runId: "run-0",
            issueIdentifier: "1",
            failureCategory: FailureReason.ExitCodeFailure,
            completedAt: BaseTime);

        var others = Enumerable.Range(1, 2)
            .Select(i => Failed(
                runId: $"run-{i}",
                issueIdentifier: $"{i + 10}",
                failureCategory: FailureReason.ExitCodeFailure,
                completedAt: BaseTime.AddMinutes(1)))
            .ToList();

        var result = RunDiagnoser.Diagnose(run, others);

        Assert.NotNull(result);
        Assert.Equal(RunDiagnosisVerdict.Agent, result.Verdict);
        Assert.Equal("The agent process crashed", result.Title);
    }

    // ── Rule 2: OOM ───────────────────────────────────────────────────────────
    // TODO: [WARNING] OOM detection is only tested with original-case strings ("OOMKilled",
    // "exit code 137"). The implementation uses StringComparison.OrdinalIgnoreCase but no test
    // exercises variant casing (e.g. "oomkilled" or "EXIT CODE 137"). If OrdinalIgnoreCase were
    // changed to Ordinal, both existing OOM tests would still pass. Add tests with variant casing.

    [Fact]
    public void Diagnose_OomKilled_IsConfiguration()
    {
        var run = Failed(failureCategory: FailureReason.ExitCodeFailure, failureReason: "OOMKilled");

        var result = RunDiagnoser.Diagnose(run);

        Assert.NotNull(result);
        Assert.Equal(RunDiagnosisVerdict.Configuration, result.Verdict);
        Assert.Equal("The agent ran out of memory", result.Title);
    }

    [Fact]
    public void Diagnose_ExitCode137_IsConfiguration()
    {
        var run = Failed(failureCategory: FailureReason.ExitCodeFailure, failureReason: "exit code 137");

        var result = RunDiagnoser.Diagnose(run);

        Assert.NotNull(result);
        Assert.Equal(RunDiagnosisVerdict.Configuration, result.Verdict);
        Assert.Equal("The agent ran out of memory", result.Title);
    }

    // ── Rule 3: token refresh ─────────────────────────────────────────────────

    [Fact]
    public void Diagnose_TokenRefreshFailure_IsConfiguration()
    {
        var run = Failed(failureCategory: FailureReason.TokenRefreshFailure);

        var result = RunDiagnoser.Diagnose(run);

        Assert.NotNull(result);
        Assert.Equal(RunDiagnosisVerdict.Configuration, result.Verdict);
        Assert.Equal("The agent lost access to the providers", result.Title);
    }

    // ── Rule 4: timeout ───────────────────────────────────────────────────────

    [Fact]
    public void Diagnose_Timeout_NamesLastActiveStep()
    {
        var run = Failed(
            failureCategory: FailureReason.Timeout,
            lastActiveStep: PipelineStep.RunningQualityGates);

        var result = RunDiagnoser.Diagnose(run);

        Assert.NotNull(result);
        Assert.Equal(RunDiagnosisVerdict.Configuration, result.Verdict);
        Assert.Equal("The run hit the agent timeout", result.Title);
        Assert.Contains("Running Quality Gates", result.Explanation);
    }

    [Fact]
    public void Diagnose_Timeout_NullLastActiveStep_UsesGenericExplanation()
    {
        var run = Failed(failureCategory: FailureReason.Timeout, lastActiveStep: null);

        var result = RunDiagnoser.Diagnose(run);

        Assert.NotNull(result);
        Assert.Equal(RunDiagnosisVerdict.Configuration, result.Verdict);
        Assert.Contains("before it finished", result.Explanation);
    }

    // ── Rule 5: infrastructure failure ───────────────────────────────────────

    [Fact]
    public void Diagnose_InfrastructureFailure_IsTransient()
    {
        var run = Failed(failureCategory: FailureReason.InfrastructureFailure);

        var result = RunDiagnoser.Diagnose(run);

        Assert.NotNull(result);
        Assert.Equal(RunDiagnosisVerdict.Transient, result.Verdict);
        Assert.Equal("The platform could not run the job", result.Title);
    }

    // ── Rule 6: exit code failure (non-OOM) ───────────────────────────────────

    [Fact]
    public void Diagnose_ExitCodeFailureWithoutOom_IsAgent_WithLastPodLogLineAsEvidence()
    {
        var run = Failed(
            failureCategory: FailureReason.ExitCodeFailure,
            failureReason: "exit code 1",
            podLogTail: ["line1", "line2", ""]);

        var result = RunDiagnoser.Diagnose(run);

        Assert.NotNull(result);
        Assert.Equal(RunDiagnosisVerdict.Agent, result.Verdict);
        Assert.Equal("The agent process crashed", result.Title);
        // Evidence: "exit code 1" + last non-empty pod log line "line2"
        Assert.Contains("line2", result.Evidence);
    }

    [Fact]
    public void Diagnose_ExitCodeFailureWithoutOom_FallsBackToOutputTailWhenNoPodLog()
    {
        var run = Failed(
            failureCategory: FailureReason.ExitCodeFailure,
            failureReason: "exit code 1",
            podLogTail: null,
            outputTail: ["output-line-1", "output-line-2"]);

        var result = RunDiagnoser.Diagnose(run);

        Assert.NotNull(result);
        Assert.Contains("output-line-2", result.Evidence);
    }

    // ── Rule 7: same gate failed in two other runs of repo ────────────────────

    [Fact]
    public void Diagnose_SameGateFailedInTwoOtherRunsOfRepo_IsRepository()
    {
        var run = Failed(
            runId: "run-0",
            issueIdentifier: "1",
            failureCategory: FailureReason.QualityGateExhausted,
            gateOutcomes: [new GateOutcome("build", false)],
            startedAt: BaseTime,
            repoProviderConfigId: "repo-1");

        var others = Enumerable.Range(1, 2)
            .Select(i => Failed(
                runId: $"run-{i}",
                issueIdentifier: $"{i + 10}",
                gateOutcomes: [new GateOutcome("build", false)],
                startedAt: BaseTime.AddHours(-1),
                repoProviderConfigId: "repo-1"))
            .ToList();

        var result = RunDiagnoser.Diagnose(run, others);

        Assert.NotNull(result);
        Assert.Equal(RunDiagnosisVerdict.Repository, result.Verdict);
        Assert.Contains("build", result.Title);
        Assert.Contains("2 other runs", result.Explanation);
    }

    [Fact]
    public void Diagnose_SameGateInRunsStartedAfterThisRun_AreNotCounted()
    {
        // Runs that started AFTER run.StartedAtOffset must not count for rule 7 (upper bound)
        var run = Failed(
            runId: "run-0",
            issueIdentifier: "1",
            gateOutcomes: [new GateOutcome("tests", false)],
            startedAt: BaseTime,
            repoProviderConfigId: "repo-1");

        var others = Enumerable.Range(1, 2)
            .Select(i => Failed(
                runId: $"run-{i}",
                issueIdentifier: $"{i + 10}",
                gateOutcomes: [new GateOutcome("tests", false)],
                startedAt: BaseTime.AddHours(1),   // started 1 hour AFTER run — must not count
                repoProviderConfigId: "repo-1"))
            .ToList();

        var result = RunDiagnoser.Diagnose(run, others);

        Assert.NotNull(result);
        // Falls to rule 8 (Agent) since cross-run condition not met
        Assert.Equal(RunDiagnosisVerdict.Agent, result.Verdict);
    }

    [Fact]
    public void Diagnose_SameGateExactly24HoursBefore_Counts()
    {
        // Exactly 24 hours before — must count (boundary is inclusive on the lower side)
        var run = Failed(
            runId: "run-0",
            issueIdentifier: "1",
            gateOutcomes: [new GateOutcome("lint", false)],
            startedAt: BaseTime,
            repoProviderConfigId: "repo-1");

        var others = Enumerable.Range(1, 2)
            .Select(i => Failed(
                runId: $"run-{i}",
                issueIdentifier: $"{i + 10}",
                gateOutcomes: [new GateOutcome("lint", false)],
                startedAt: BaseTime.AddHours(-24),   // exactly 24 hours before
                repoProviderConfigId: "repo-1"))
            .ToList();

        var result = RunDiagnoser.Diagnose(run, others);

        Assert.NotNull(result);
        Assert.Equal(RunDiagnosisVerdict.Repository, result.Verdict);
    }

    // ── Rule 8: gate failed only on this run ──────────────────────────────────
    // TODO: [WARNING] No test exercises the exclusive lower boundary of the rule-7 time window.
    // The test Diagnose_SameGateExactly24HoursBefore_Counts confirms the inclusive lower bound
    // (exactly −24 h counts), but no test places others at BaseTime.AddHours(-24).AddSeconds(-1)
    // (just outside the window). Without this test, relaxing the lower-bound check from
    // >= run.StartedAtOffset.AddHours(-24) to >= run.StartedAtOffset.AddHours(-48) would not be caught.
    // Add a test: others at startedAt = BaseTime.AddHours(-24).AddSeconds(-1) must fall to rule 8.
    // TODO: [WARNING] No test exercises the run.RepoProviderConfigId == null code path. When
    // RepoProviderConfigId is null the diagnoser skips the cross-run count entirely and falls
    // unconditionally to rule 8 (Agent). Add a test: run with gate failure, repoProviderConfigId: null,
    // and 2+ matching recent runs — the result must still be Agent (rule 8), not Repository (rule 7).
    // Without this test, accidentally changing the null guard (is not null → is null) would not be caught.

    [Fact]
    public void Diagnose_GateFailedOnlyHere_IsAgent()
    {
        var run = Failed(
            runId: "run-0",
            gateOutcomes: [new GateOutcome("tests", false)],
            retryCount: 2,
            repoProviderConfigId: "repo-1");

        // Only 1 matching recent run — not enough for rule 7
        var others = new List<PipelineRunSummary>
        {
            Failed(runId: "run-1", issueIdentifier: "999",
                gateOutcomes: [new GateOutcome("tests", false)],
                startedAt: BaseTime.AddHours(-1),
                repoProviderConfigId: "repo-1"),
        };

        var result = RunDiagnoser.Diagnose(run, others);

        Assert.NotNull(result);
        Assert.Equal(RunDiagnosisVerdict.Agent, result.Verdict);
        Assert.Contains("tests", result.Title);
        Assert.Contains("2 retries", result.Explanation);
    }

    // ── Rule 9: unknown ───────────────────────────────────────────────────────

    [Fact]
    public void Diagnose_NoCategory_IsUnknown_WithFailureReasonAsExplanation()
    {
        var run = Failed(failureCategory: null, failureReason: "some unexpected error");

        var result = RunDiagnoser.Diagnose(run);

        Assert.NotNull(result);
        Assert.Equal(RunDiagnosisVerdict.Unknown, result.Verdict);
        Assert.Equal("No rule matched this failure", result.Title);
        Assert.Equal("some unexpected error", result.Explanation);
    }

    [Fact]
    public void Diagnose_NoCategory_NoFailureReason_IsUnknown_WithGenericExplanation()
    {
        var run = Failed(failureCategory: null, failureReason: null);

        var result = RunDiagnoser.Diagnose(run);

        Assert.NotNull(result);
        Assert.Equal(RunDiagnosisVerdict.Unknown, result.Verdict);
        Assert.Contains("without a recorded reason", result.Explanation);
    }

    // ── Self-exclusion ────────────────────────────────────────────────────────

    [Fact]
    public void Diagnose_RunItselfInRecentRuns_IsNotCounted()
    {
        var run = Failed(
            runId: "run-0",
            issueIdentifier: "1",
            failureCategory: FailureReason.ExitCodeFailure,
            completedAt: BaseTime);

        // Add the run itself plus only 2 real others — self must not count toward the 3 threshold
        var others = new List<PipelineRunSummary>
        {
            run,  // the run itself — must be excluded
            Failed(runId: "run-1", issueIdentifier: "2",
                failureCategory: FailureReason.ExitCodeFailure,
                completedAt: BaseTime.AddMinutes(1)),
            Failed(runId: "run-2", issueIdentifier: "3",
                failureCategory: FailureReason.ExitCodeFailure,
                completedAt: BaseTime.AddMinutes(2)),
        };

        var result = RunDiagnoser.Diagnose(run, others);

        Assert.NotNull(result);
        // Rule 1 needs 3 others; with self excluded only 2 remain → falls to rule 6
        Assert.Equal(RunDiagnosisVerdict.Agent, result.Verdict);
    }

    // ── Evidence ──────────────────────────────────────────────────────────────

    // TODO: [WARNING] Diagnose_Evidence_IsCappedAt3EntriesOf300Chars only asserts Count <= 3, but
    // BuildEvidence is structurally incapable of producing more than 2 entries from any single rule:
    // entry 2 (pod/output log line) is mutually exclusive with entry 3 (gate names). The EvidenceMaxEntries
    // = 3 cap and the entries.Count < EvidenceMaxEntries guard are dead-code paths. The count assertion
    // is therefore vacuously true and would not catch a regression that raised the achievable entry count
    // above 3. Consider replacing with an assertion on the exact count and exact content for a known rule,
    // or adding a hypothetical test using a helper that bypasses structural exclusivity to exercise the cap.

    [Fact]
    public void Diagnose_Evidence_IsCappedAt3EntriesOf300Chars()
    {
        var longReason = new string('x', 500);
        var run = Failed(
            failureCategory: null,  // → rule 9
            failureReason: longReason,
            podLogTail: [new string('p', 500)]);

        var result = RunDiagnoser.Diagnose(run);

        Assert.NotNull(result);
        Assert.True(result.Evidence.Count <= 3, $"Evidence count was {result.Evidence.Count}");
        foreach (var entry in result.Evidence)
            Assert.True(entry.Length <= 300, $"Entry length {entry.Length} exceeds 300");
    }

    [Fact]
    public void Diagnose_GateEvidence_IncludesFailedGateNames()
    {
        var run = Failed(
            gateOutcomes: [new GateOutcome("build", false), new GateOutcome("tests", false)],
            repoProviderConfigId: "repo-1");

        var result = RunDiagnoser.Diagnose(run);  // no recent runs → rule 8

        Assert.NotNull(result);
        Assert.Contains(result.Evidence, e => e.Contains("Failed gates:") && e.Contains("build") && e.Contains("tests"));
    }
}
