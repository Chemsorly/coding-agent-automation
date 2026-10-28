using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="AttentionAggregator"/> — the pure aggregation helper that
/// de-duplicates run history into the three Attention sections.
/// All tests are plain xUnit facts with no Blazor/DI overhead.
/// </summary>
public class AttentionAggregatorTests
{
    // ── helpers ────────────────────────────────────────────────────────────

    private static PipelineRunSummary MakeRun(
        string issueId,
        PipelineRunType runType,
        PipelineStep finalStep,
        DateTimeOffset startedAt,
        string? failureReason = null,
        AnalysisGateResult? analysisRecommendation = null) => new()
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = issueId,
            IssueTitle = $"Issue {issueId}",
            RunType = runType,
            FinalStep = finalStep,
            StartedAtOffset = startedAt,
            FailureReason = failureReason,
            AnalysisRecommendation = analysisRecommendation
        };

    private static readonly DateTimeOffset T1 = new(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T2 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset T3 = new(2026, 1, 1, 14, 0, 0, TimeSpan.Zero);

    // ── 1. Duplicate runs counted once ─────────────────────────────────────

    /// <summary>
    /// Two Failed Implementation runs for the same issue must produce exactly one entry
    /// in FailedRuns, showing the most recent failure reason.
    /// </summary>
    [Fact]
    public void DuplicateFailedRuns_SameIssue_CountedOnce_WithLatestReason()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRun("owner/repo#42", PipelineRunType.Implementation, PipelineStep.Failed,
                T1, failureReason: "old reason"),
            MakeRun("owner/repo#42", PipelineRunType.Implementation, PipelineStep.Failed,
                T2, failureReason: "latest reason"),
        };

        var result = AttentionAggregator.Aggregate(runs);

        Assert.Single(result.FailedRuns);
        Assert.Equal("latest reason", result.FailedRuns[0].FailureReason);
        Assert.Empty(result.NeedsRefinement);
        Assert.Empty(result.PlansToApprove);
    }

    // ── 2. Superseded failure: later Completed run removes the issue ───────

    /// <summary>
    /// A Failed run at T=1 followed by a Completed run at T=2 (same issue, same run type)
    /// must NOT appear in FailedRuns — the Completed run supersedes it.
    /// </summary>
    [Fact]
    public void SupersededFailure_LaterCompletedRun_NotInFailedRuns()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRun("owner/repo#99", PipelineRunType.Implementation, PipelineStep.Failed, T1,
                failureReason: "build broke"),
            MakeRun("owner/repo#99", PipelineRunType.Implementation, PipelineStep.Completed, T2),
        };

        var result = AttentionAggregator.Aggregate(runs);

        Assert.Empty(result.FailedRuns);
        Assert.Empty(result.NeedsRefinement);
    }

    // ── 3. Auto-restarted failure shows the newest run's reason ───────────

    /// <summary>
    /// When the pipeline auto-restarts after a conflict failure, the newer Failed run is the
    /// representative. FailedRuns contains exactly one entry with the newest failure reason.
    /// </summary>
    [Fact]
    public void AutoRestartedFailure_ShowsNewestReason()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRun("owner/repo#7", PipelineRunType.Implementation, PipelineStep.Failed, T1,
                failureReason: "PR conflicted with main — restarting pipeline"),
            MakeRun("owner/repo#7", PipelineRunType.Implementation, PipelineStep.Failed, T2,
                failureReason: "tests failed after retry"),
        };

        var result = AttentionAggregator.Aggregate(runs);

        Assert.Single(result.FailedRuns);
        Assert.Equal("tests failed after retry", result.FailedRuns[0].FailureReason);
    }

    // ── 4. Plan superseded by Phase-2 Decomposition run ───────────────────

    /// <summary>
    /// A completed DecompositionAnalysis (Phase-1 plan) at T=1 followed by a Decomposition
    /// (Phase-2) run at T=2 for the same epic must NOT appear in PlansToApprove.
    /// The Phase-2 run becomes the group representative, and its RunType is Decomposition
    /// (not DecompositionAnalysis), so it is automatically excluded.
    /// </summary>
    [Fact]
    public void PlanSupersededByPhase2_NotInPlansToApprove()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRun("owner/repo#200", PipelineRunType.DecompositionAnalysis, PipelineStep.Completed, T1),
            MakeRun("owner/repo#200", PipelineRunType.Decomposition, PipelineStep.Completed, T2),
        };

        var result = AttentionAggregator.Aggregate(runs);

        Assert.Empty(result.PlansToApprove);
        Assert.Empty(result.FailedRuns);
    }

    // ── 5. Plan not superseded stays in PlansToApprove ────────────────────

    /// <summary>
    /// A completed DecompositionAnalysis with no Phase-2 run must appear in PlansToApprove.
    /// </summary>
    [Fact]
    public void PlanNotSuperseded_InPlansToApprove()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRun("owner/repo#201", PipelineRunType.DecompositionAnalysis, PipelineStep.Completed, T1),
        };

        var result = AttentionAggregator.Aggregate(runs);

        Assert.Single(result.PlansToApprove);
        Assert.Equal("owner/repo#201", result.PlansToApprove[0].IssueIdentifier.Value);
    }

    // ── 6. Empty input ─────────────────────────────────────────────────────

    [Fact]
    public void EmptyInput_ReturnsAllEmptyResult()
    {
        var result = AttentionAggregator.Aggregate([]);

        Assert.Empty(result.NeedsRefinement);
        Assert.Empty(result.FailedRuns);
        Assert.Empty(result.PlansToApprove);
        Assert.Equal(0, result.TotalCount);
    }

    // ── 7. NeedsRefinement deduplication ──────────────────────────────────

    /// <summary>
    /// Two NotReady runs for the same issue must produce exactly one entry in NeedsRefinement.
    /// </summary>
    [Fact]
    public void NeedsRefinement_DuplicateRuns_CountedOnce()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRun("owner/repo#50", PipelineRunType.Implementation, PipelineStep.Failed, T1,
                analysisRecommendation: AnalysisGateResult.NotReady),
            MakeRun("owner/repo#50", PipelineRunType.Implementation, PipelineStep.Failed, T2,
                analysisRecommendation: AnalysisGateResult.NotReady),
        };

        var result = AttentionAggregator.Aggregate(runs);

        Assert.Single(result.NeedsRefinement);
    }

    // ── 8. Mixed states: latest run wins the classification ────────────────

    /// <summary>
    /// Same issue/runType: NotReady run at T=1, Failed (no NotReady) run at T=2.
    /// T=2 becomes the representative. It appears in FailedRuns only, not NeedsRefinement.
    /// </summary>
    [Fact]
    public void MixedStates_LatestRunWinsClassification()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRun("owner/repo#60", PipelineRunType.Implementation, PipelineStep.Failed, T1,
                analysisRecommendation: AnalysisGateResult.NotReady),
            MakeRun("owner/repo#60", PipelineRunType.Implementation, PipelineStep.Failed, T2,
                failureReason: "build failed", analysisRecommendation: null),
        };

        var result = AttentionAggregator.Aggregate(runs);

        Assert.Single(result.FailedRuns);
        Assert.Empty(result.NeedsRefinement);
    }

    // ── 9. Multiple distinct issues, each with one Failed run ──────────────

    [Fact]
    public void MultipleDistinctIssues_EachCountedOnce()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRun("owner/repo#1", PipelineRunType.Implementation, PipelineStep.Failed, T1),
            MakeRun("owner/repo#2", PipelineRunType.Implementation, PipelineStep.Failed, T1),
            MakeRun("owner/repo#3", PipelineRunType.Implementation, PipelineStep.Failed, T1),
        };

        var result = AttentionAggregator.Aggregate(runs);

        Assert.Equal(3, result.FailedRuns.Count);
        Assert.Equal(3, result.TotalCount);
    }

    // ── 10. Cross-run-type isolation ──────────────────────────────────────

    /// <summary>
    /// Same issue: Failed Implementation at T=1, Completed Review at T=2.
    /// The two run types are in separate groups. The Completed Review does NOT supersede
    /// the failed Implementation — it appears in FailedRuns. This is intentional.
    /// </summary>
    [Fact]
    public void CrossRunTypeIsolation_CompletedReviewDoesNotSuppressFailedImplementation()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRun("owner/repo#77", PipelineRunType.Implementation, PipelineStep.Failed,  T1,
                failureReason: "tests failed"),
            MakeRun("owner/repo#77", PipelineRunType.Review,          PipelineStep.Completed, T2),
        };

        var result = AttentionAggregator.Aggregate(runs);

        // Implementation group: representative is the Failed run at T=1 → appears in FailedRuns.
        Assert.Single(result.FailedRuns);
        Assert.Equal("tests failed", result.FailedRuns[0].FailureReason);

        // Review group: representative is Completed → not in FailedRuns, not in any other section.
        Assert.Empty(result.NeedsRefinement);
        Assert.Empty(result.PlansToApprove);
    }

    // ── 11. Consolidation runs excluded ───────────────────────────────────

    /// <summary>
    /// Consolidation failures are tracked by #2567 and are out of scope for this change.
    /// Failed Consolidation runs must NOT appear in any attention section.
    /// </summary>
    [Fact]
    public void ConsolidationRuns_ExcludedFromAllSections()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRun("owner/repo#42", PipelineRunType.Consolidation, PipelineStep.Failed, T1,
                failureReason: "consolidation failed"),
        };

        var result = AttentionAggregator.Aggregate(runs);

        Assert.Empty(result.FailedRuns);
        Assert.Empty(result.NeedsRefinement);
        Assert.Empty(result.PlansToApprove);
        Assert.Equal(0, result.TotalCount);
    }

    // ── Additional edge cases ──────────────────────────────────────────────

    // TODO: [WARNING] There is no test for the double-classification scenario where a representative
    // has BOTH AnalysisRecommendation == NotReady AND FinalStep == Failed simultaneously. This is a
    // real scenario: an analysis run can fail (FinalStep == Failed) while also flagging the issue as
    // NotReady. The current aggregator classifies independently, so such a run appears in BOTH
    // NeedsRefinement AND FailedRuns, causing TotalCount to over-count by 1 and the badge to disagree
    // with the Attention page. A test asserting the intended behaviour (double-count vs. priority rule)
    // is needed to lock in the semantics and prevent silent regression.

    /// <summary>
    /// TotalCount equals the sum of all three section counts.
    /// Each run is in exactly one section (unambiguous states used deliberately).
    /// </summary>
    [Fact]
    public void TotalCount_EqualsSumOfAllSections()
    {
        var runs = new List<PipelineRunSummary>
        {
            // NeedsRefinement only: Cancelled + NotReady (FinalStep != Failed so not in FailedRuns)
            MakeRun("owner/repo#10", PipelineRunType.Implementation, PipelineStep.Cancelled, T1,
                analysisRecommendation: AnalysisGateResult.NotReady),
            // FailedRuns only: Failed without NotReady
            MakeRun("owner/repo#11", PipelineRunType.Implementation, PipelineStep.Failed, T1),
            // PlansToApprove only
            MakeRun("owner/repo#12", PipelineRunType.DecompositionAnalysis, PipelineStep.Completed, T1),
        };

        var result = AttentionAggregator.Aggregate(runs);

        Assert.Single(result.NeedsRefinement);
        Assert.Single(result.FailedRuns);
        Assert.Single(result.PlansToApprove);
        Assert.Equal(3, result.TotalCount);
    }

    /// <summary>
    /// A mixed batch containing Consolidation, Implementation, and DecompositionAnalysis runs:
    /// only the non-Consolidation runs should be aggregated.
    /// </summary>
    [Fact]
    public void MixedBatch_ConsolidationFiltered_OthersAggregated()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRun("owner/repo#1", PipelineRunType.Consolidation, PipelineStep.Failed, T1),
            MakeRun("owner/repo#2", PipelineRunType.Implementation, PipelineStep.Failed, T1),
            MakeRun("owner/repo#3", PipelineRunType.DecompositionAnalysis, PipelineStep.Completed, T1),
        };

        var result = AttentionAggregator.Aggregate(runs);

        Assert.Single(result.FailedRuns);
        Assert.Equal("owner/repo#2", result.FailedRuns[0].IssueIdentifier.Value);
        Assert.Single(result.PlansToApprove);
        Assert.Equal("owner/repo#3", result.PlansToApprove[0].IssueIdentifier.Value);
    }

    /// <summary>
    /// Three phases for the same epic: Phase-1 DecompositionAnalysis at T=1, Phase-2 Decomposition
    /// at T=2 (which then fails), another Phase-1 at T=3 (re-analysis after Phase-2 failure).
    /// Latest in the "decomp" group is T=3 (DecompositionAnalysis/Completed), so it IS a plan.
    /// </summary>
    [Fact]
    public void EpicGrouping_LatestPhase1AfterFailedPhase2_IsAPlan()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRun("owner/repo#300", PipelineRunType.DecompositionAnalysis, PipelineStep.Completed, T1),
            MakeRun("owner/repo#300", PipelineRunType.Decomposition,         PipelineStep.Failed,    T2),
            MakeRun("owner/repo#300", PipelineRunType.DecompositionAnalysis, PipelineStep.Completed, T3),
        };

        var result = AttentionAggregator.Aggregate(runs);

        // Latest in the decomp group is T=3 (DecompositionAnalysis/Completed) → plan to approve
        Assert.Single(result.PlansToApprove);
        Assert.Empty(result.FailedRuns);
    }

    // ── FindMcpServerProblems tests ────────────────────────────────────────

    private static PipelineRunSummary MakeRunWithMcp(
        string issueId,
        DateTimeOffset startedAt,
        IReadOnlyList<string>? mcpServers = null,
        IReadOnlyDictionary<string, string>? unavailableMcpServers = null,
        IReadOnlyDictionary<string, McpCallCounts>? toolCalls = null)
    {
        PhaseUsage? phase = null;
        if (unavailableMcpServers is not null || toolCalls is not null)
        {
            phase = new PhaseUsage(0, null)
            {
                ToolCalls = toolCalls,
                UnavailableMcpServers = unavailableMcpServers,
            };
        }

        return new PipelineRunSummary
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = issueId,
            IssueTitle = $"Issue {issueId}",
            RunType = PipelineRunType.Implementation,
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = startedAt,
            McpServers = mcpServers,
            PhaseBreakdown = phase is not null
                ? new Dictionary<string, PhaseUsage> { ["codegen"] = phase }
                : null,
        };
    }

    private static PipelineRunSummary MakeRunWithMcpMultiPhase(
        string issueId,
        DateTimeOffset startedAt,
        IReadOnlyList<string>? mcpServers,
        IReadOnlyDictionary<string, PhaseUsage> phaseBreakdown) => new()
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = issueId,
            IssueTitle = $"Issue {issueId}",
            RunType = PipelineRunType.Implementation,
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = startedAt,
            McpServers = mcpServers,
            PhaseBreakdown = phaseBreakdown,
        };

    private static McpCallCounts Calls(int calls, int failedConnection = 0, int failedAuth = 0, int failedOther = 0, int failedUnknown = 0)
        => new() { Calls = calls, FailedConnection = failedConnection, FailedAuth = failedAuth, FailedOther = failedOther, FailedUnknown = failedUnknown };

    /// <summary>
    /// Newest run has sonarqube unavailable (needs-auth) → listed with "unavailable (needs-auth)", ProblemRuns = 1.
    /// </summary>
    [Fact]
    public void FindMcpServerProblems_UnavailableNeedsAuth_Listed()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRunWithMcp("owner/repo#1", T2,
                unavailableMcpServers: new Dictionary<string, string> { ["sonarqube"] = "needs-auth" }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        var problem = Assert.Single(result);
        Assert.Equal("sonarqube", problem.Server);
        Assert.Equal("unavailable (needs-auth)", problem.Reason);
        Assert.Equal(1, problem.ProblemRuns);
    }

    /// <summary>
    /// Newest run has 3 of 3 grafana calls failed (connection 2, auth 1) → "all 3 calls failed (connection 2, auth 1)".
    /// </summary>
    [Fact]
    public void FindMcpServerProblems_AllCallsFailed_ConnectionAndAuth_Listed()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRunWithMcp("owner/repo#1", T2,
                toolCalls: new Dictionary<string, McpCallCounts>
                {
                    ["grafana/query"] = Calls(calls: 2, failedConnection: 2),
                    ["grafana/list"] = Calls(calls: 1, failedAuth: 1),
                }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        var problem = Assert.Single(result);
        Assert.Equal("grafana", problem.Server);
        Assert.Equal("all 3 calls failed (connection 2, auth 1)", problem.Reason);
        Assert.Equal(1, problem.ProblemRuns);
    }

    /// <summary>
    /// Newest run has 2 of 2 calls failed but one of them is "other" → not listed (FailedOther > 0).
    /// </summary>
    [Fact]
    public void FindMcpServerProblems_AllFailedButSomeOther_NotListed()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRunWithMcp("owner/repo#1", T2,
                toolCalls: new Dictionary<string, McpCallCounts>
                {
                    ["github/get_issue"] = Calls(calls: 2, failedConnection: 1, failedOther: 1),
                }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        Assert.Empty(result);
    }

    /// <summary>
    /// Newest run has 1 of 3 calls failed (not all) → not listed.
    /// </summary>
    [Fact]
    public void FindMcpServerProblems_PartialFailure_NotListed()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRunWithMcp("owner/repo#1", T2,
                toolCalls: new Dictionary<string, McpCallCounts>
                {
                    ["github/get_issue"] = Calls(calls: 3, failedConnection: 1),
                }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        Assert.Empty(result);
    }

    /// <summary>
    /// An OK run newer than a problem run → server is not listed.
    /// </summary>
    [Fact]
    public void FindMcpServerProblems_OkRunNewerThanProblem_NotListed()
    {
        var runs = new List<PipelineRunSummary>
        {
            // T2 (newer) — OK: one call succeeded
            MakeRunWithMcp("owner/repo#1", T2,
                toolCalls: new Dictionary<string, McpCallCounts>
                {
                    ["github/get_issue"] = Calls(calls: 1),
                }),
            // T1 (older) — all calls failed
            MakeRunWithMcp("owner/repo#2", T1,
                toolCalls: new Dictionary<string, McpCallCounts>
                {
                    ["github/get_issue"] = Calls(calls: 2, failedConnection: 2),
                }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        Assert.Empty(result);
    }

    /// <summary>
    /// Pattern: problem (T4, newest), neutral (T3), problem (T2), OK (T1, oldest) → listed with ProblemRuns = 2.
    /// </summary>
    [Fact]
    public void FindMcpServerProblems_ProblemNeutralProblemOk_ListedWithProblemRunsTwo()
    {
        var T4 = new DateTimeOffset(2026, 1, 1, 16, 0, 0, TimeSpan.Zero);
        var runs = new List<PipelineRunSummary>
        {
            // T4 (newest): problem — all calls failed
            MakeRunWithMcp("owner/repo#4", T4,
                toolCalls: new Dictionary<string, McpCallCounts>
                {
                    ["sonarqube/get_issues"] = Calls(calls: 1, failedConnection: 1),
                }),
            // T3: neutral — sonarqube configured but not called and not unavailable
            MakeRunWithMcp("owner/repo#3", T3,
                mcpServers: ["sonarqube"]),
            // T2: problem — all calls failed
            MakeRunWithMcp("owner/repo#2", T2,
                toolCalls: new Dictionary<string, McpCallCounts>
                {
                    ["sonarqube/get_issues"] = Calls(calls: 2, failedAuth: 2),
                }),
            // T1 (oldest): OK — calls succeeded
            MakeRunWithMcp("owner/repo#1", T1,
                toolCalls: new Dictionary<string, McpCallCounts>
                {
                    ["sonarqube/get_issues"] = Calls(calls: 3),
                }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        var problem = Assert.Single(result);
        Assert.Equal("sonarqube", problem.Server);
        Assert.Equal(2, problem.ProblemRuns);
        Assert.Equal(T4, problem.LastProblemRun.StartedAtOffset);
    }

    /// <summary>
    /// web/search calls that all failed → web is never listed.
    /// </summary>
    [Fact]
    public void FindMcpServerProblems_WebCallsFailed_WebNeverListed()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRunWithMcp("owner/repo#1", T2,
                toolCalls: new Dictionary<string, McpCallCounts>
                {
                    ["web/search"] = Calls(calls: 3, failedConnection: 3),
                }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        Assert.Empty(result);
    }

    /// <summary>
    /// Server names that differ only in case are treated as one server.
    /// "Sonarqube" and "sonarqube" must produce a single entry, not two.
    /// </summary>
    [Fact]
    public void FindMcpServerProblems_NamesInDifferentCase_TreatedAsOneServer()
    {
        var runs = new List<PipelineRunSummary>
        {
            // T2 (newer): configured as "Sonarqube" (uppercase S) — neutral
            MakeRunWithMcp("owner/repo#2", T2,
                mcpServers: ["Sonarqube"]),
            // T1 (older): all calls failed under "sonarqube" (lowercase)
            MakeRunWithMcp("owner/repo#1", T1,
                toolCalls: new Dictionary<string, McpCallCounts>
                {
                    ["sonarqube/get_issues"] = Calls(calls: 1, failedConnection: 1),
                }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        // The neutral newer run means the first non-neutral is the problem at T1. Still one server.
        var problem = Assert.Single(result);
        Assert.Equal(1, problem.ProblemRuns);
    }

    /// <summary>
    /// TotalCount from Aggregate includes McpServerProblems.Count.
    /// </summary>
    [Fact]
    public void Aggregate_TotalCount_IncludesMcpProblems()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRunWithMcp("owner/repo#1", T2,
                unavailableMcpServers: new Dictionary<string, string> { ["sonarqube"] = "failed" }),
        };

        var result = AttentionAggregator.Aggregate(runs);

        Assert.Single(result.McpServerProblems);
        Assert.Equal(1, result.TotalCount);
    }

    /// <summary>
    /// Runs with no tool data (no PhaseBreakdown, no McpServers) give an empty list.
    /// </summary>
    [Fact]
    public void FindMcpServerProblems_RunsWithNoToolData_EmptyResult()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRun("owner/repo#1", PipelineRunType.Implementation, PipelineStep.Completed, T1),
            MakeRun("owner/repo#2", PipelineRunType.Implementation, PipelineStep.Failed, T2),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        Assert.Empty(result);
    }

    /// <summary>
    /// Newest run has sonarqube unavailable with status "failed" → reason is "unavailable (failed)".
    /// Exercises the else branch of BuildReason's unavailability check.
    /// </summary>
    [Fact]
    public void FindMcpServerProblems_UnavailableFailed_ReasonStringCorrect()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRunWithMcp("owner/repo#1", T2,
                unavailableMcpServers: new Dictionary<string, string> { ["sonarqube"] = "failed" }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        var problem = Assert.Single(result);
        Assert.Equal("sonarqube", problem.Server);
        Assert.Equal("unavailable (failed)", problem.Reason);
    }

    /// <summary>
    /// When two distinct servers have problems, results are sorted newest LastProblemRun first.
    /// </summary>
    [Fact]
    public void FindMcpServerProblems_MultipleServers_SortedNewestFirst()
    {
        var runs = new List<PipelineRunSummary>
        {
            // T1 (older): grafana problem
            MakeRunWithMcp("owner/repo#1", T1,
                unavailableMcpServers: new Dictionary<string, string> { ["grafana"] = "failed" }),
            // T2 (newer): sonarqube problem
            MakeRunWithMcp("owner/repo#2", T2,
                unavailableMcpServers: new Dictionary<string, string> { ["sonarqube"] = "needs-auth" }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        Assert.Equal(2, result.Count);
        // Newest first: sonarqube (T2) before grafana (T1).
        Assert.Equal("sonarqube", result[0].Server);
        Assert.Equal(T2, result[0].LastProblemRun.StartedAtOffset);
        Assert.Equal("grafana", result[1].Server);
        Assert.Equal(T1, result[1].LastProblemRun.StartedAtOffset);
    }

    // ── Multi-phase characterization tests ────────────────────────────────

    /// <summary>
    /// A run with two phases where tool-call counts for the same server must be accumulated
    /// across phases. Phase "analysis" has 1 failed call (Problem if evaluated alone);
    /// phase "codegen" has 2 successful calls. Only the combined total — 3 calls, 1 failed —
    /// produces a partial failure (not listed). If "codegen" were ignored, "analysis" alone
    /// would be all-failed and the server would be listed as a Problem, failing the assertion.
    /// This ensures the test catches a regression where any phase is skipped.
    /// </summary>
    [Fact]
    public void FindMcpServerProblems_MultiPhaseRun_ToolCallsAccumulatedAcrossPhases()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRunWithMcpMultiPhase("owner/repo#1", T2, mcpServers: null,
                phaseBreakdown: new Dictionary<string, PhaseUsage>
                {
                    ["analysis"] = new PhaseUsage(0, null)
                    {
                        ToolCalls = new Dictionary<string, McpCallCounts>
                        {
                            // 1 call, 1 failed → alone this is all-failed (Problem)
                            ["sonarqube/search"] = Calls(calls: 1, failedConnection: 1),
                        },
                    },
                    ["codegen"] = new PhaseUsage(0, null)
                    {
                        ToolCalls = new Dictionary<string, McpCallCounts>
                        {
                            // 2 calls, 0 failed → combined total is 3 calls, 1 failed = partial failure
                            ["sonarqube/get_issues"] = Calls(calls: 2),
                        },
                    },
                }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        // Combined: 3 calls, 1 failed — partial failure → not listed.
        // Without phase "codegen": 1 call, 1 failed, all-failed → Problem → would be listed → test would fail.
        Assert.Empty(result);
    }

    /// <summary>
    /// A run with two phases where all calls fail across phases (no partial success).
    /// Phase "analysis" has 1 failed call; phase "codegen" has 2 failed calls.
    /// Total: 3 calls, 3 failed, FailedOther=0 → listed with "all 3 calls failed (connection 3)".
    /// </summary>
    [Fact]
    public void FindMcpServerProblems_MultiPhaseRun_AllCallsFailedAcrossPhases()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRunWithMcpMultiPhase("owner/repo#1", T2, mcpServers: null,
                phaseBreakdown: new Dictionary<string, PhaseUsage>
                {
                    ["analysis"] = new PhaseUsage(0, null)
                    {
                        ToolCalls = new Dictionary<string, McpCallCounts>
                        {
                            ["sonarqube/search"] = Calls(calls: 1, failedConnection: 1),
                        },
                    },
                    ["codegen"] = new PhaseUsage(0, null)
                    {
                        ToolCalls = new Dictionary<string, McpCallCounts>
                        {
                            ["sonarqube/get_issues"] = Calls(calls: 2, failedConnection: 2),
                        },
                    },
                }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        var problem = Assert.Single(result);
        Assert.Equal("sonarqube", problem.Server);
        Assert.Equal("all 3 calls failed (connection 3)", problem.Reason);
    }

    /// <summary>
    /// A run where the server is unavailable in one phase but has successful calls in another.
    /// IsUnavailable must take precedence over HasCalls — server is listed as unavailable.
    /// </summary>
    [Fact]
    public void FindMcpServerProblems_MultiPhaseRun_UnavailabilityInOnePhase_CallsInAnother_IsUnavailable()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRunWithMcpMultiPhase("owner/repo#1", T2, mcpServers: null,
                phaseBreakdown: new Dictionary<string, PhaseUsage>
                {
                    ["analysis"] = new PhaseUsage(0, null)
                    {
                        UnavailableMcpServers = new Dictionary<string, string>
                        {
                            ["sonarqube"] = "needs-auth",
                        },
                    },
                    ["codegen"] = new PhaseUsage(0, null)
                    {
                        ToolCalls = new Dictionary<string, McpCallCounts>
                        {
                            ["sonarqube/get_issues"] = Calls(calls: 1),
                        },
                    },
                }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        var problem = Assert.Single(result);
        Assert.Equal("sonarqube", problem.Server);
        Assert.Equal("unavailable (needs-auth)", problem.Reason);
    }

    /// <summary>
    /// A run where the server is unavailable in two phases with different statuses.
    /// The reason must reflect the FIRST-found status ("needs-auth" from phase "analysis"),
    /// not the last ("failed" from phase "codegen"). This locks in the first-found semantics
    /// that SummarizeForServer preserves via the if (!isUnavailable) guard.
    ///
    /// A <see cref="SortedDictionary{TKey,TValue}"/> with <see cref="StringComparer.Ordinal"/>
    /// is used so that "analysis" (&lt; "codegen" alphabetically) is always iterated first,
    /// making the test deterministic regardless of runtime dictionary implementation.
    /// </summary>
    [Fact]
    public void FindMcpServerProblems_MultiPhaseRun_UnavailabilityStatusFromFirstPhase()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRunWithMcpMultiPhase("owner/repo#1", T2, mcpServers: null,
                phaseBreakdown: new SortedDictionary<string, PhaseUsage>(StringComparer.Ordinal)
                {
                    ["analysis"] = new PhaseUsage(0, null)
                    {
                        UnavailableMcpServers = new Dictionary<string, string>
                        {
                            ["sonarqube"] = "needs-auth",
                        },
                    },
                    ["codegen"] = new PhaseUsage(0, null)
                    {
                        UnavailableMcpServers = new Dictionary<string, string>
                        {
                            ["sonarqube"] = "failed",
                        },
                    },
                }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        var problem = Assert.Single(result);
        Assert.Equal("sonarqube", problem.Server);
        // First-found phase has "needs-auth"; second phase has "failed". First wins.
        Assert.Equal("unavailable (needs-auth)", problem.Reason);
    }

    /// <summary>
    /// All calls failed with the Unknown failure category.
    /// Reason must include "unknown N" — exercises the FailedUnknown branch of BuildReason.
    /// </summary>
    [Fact]
    public void FindMcpServerProblems_AllCallsFailedWithUnknownCategory_ReasonIncludesUnknown()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRunWithMcp("owner/repo#1", T2,
                toolCalls: new Dictionary<string, McpCallCounts>
                {
                    ["sonarqube/get_issues"] = Calls(calls: 2, failedUnknown: 2),
                }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        var problem = Assert.Single(result);
        Assert.Equal("sonarqube", problem.Server);
        Assert.Equal("all 2 calls failed (unknown 2)", problem.Reason);
    }
}