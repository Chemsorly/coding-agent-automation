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
        var result = AttentionAggregator.Aggregate(new List<PipelineRunSummary>());

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

    // ── FindMcpServerProblems ──────────────────────────────────────────────

    /// <summary>
    /// Builds a PipelineRunSummary with MCP-specific data for FindMcpServerProblems tests.
    /// All four required properties are set; RunType defaults to Implementation.
    /// </summary>
    private static PipelineRunSummary MakeMcpRun(
        DateTimeOffset startedAt,
        IReadOnlyDictionary<string, string>? unavailable = null,
        IReadOnlyDictionary<string, McpCallCounts>? toolCalls = null,
        IReadOnlyList<string>? mcpServers = null) => new()
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "owner/repo#1",
            IssueTitle = "Test issue",
            FinalStep = PipelineStep.Completed,
            StartedAtOffset = startedAt,
            PhaseBreakdown = toolCalls != null || unavailable != null
                ? new Dictionary<string, PhaseUsage>
                {
                    ["codegen"] = new PhaseUsage(0, null) { ToolCalls = toolCalls, UnavailableMcpServers = unavailable }
                }
                : null,
            McpServers = mcpServers,
        };

    /// <summary>sonarqube unavailable with needs-auth → listed with correct reason and ProblemRuns=1.</summary>
    [Fact]
    public void FindMcpServerProblems_SonarqubeUnavailableNeedsAuth_Listed()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeMcpRun(T1, unavailable: new Dictionary<string, string> { ["sonarqube"] = "needs-auth" }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        Assert.Single(result);
        Assert.Equal("sonarqube", result[0].Server);
        Assert.Equal("unavailable (needs-auth)", result[0].Reason);
        Assert.Equal(1, result[0].ProblemRuns);
    }

    /// <summary>All 3 grafana calls failed (connection 2, auth 1) → listed with formatted reason.</summary>
    [Fact]
    public void FindMcpServerProblems_GrafanaAllCallsFailedConnectionAndAuth_Listed()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeMcpRun(T1, toolCalls: new Dictionary<string, McpCallCounts>
            {
                ["grafana/query"] = new McpCallCounts { Calls = 3, FailedConnection = 2, FailedAuth = 1 }
            }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        Assert.Single(result);
        Assert.Equal("grafana", result[0].Server);
        Assert.Equal("all 3 calls failed (connection 2, auth 1)", result[0].Reason);
        Assert.Equal(1, result[0].ProblemRuns);
    }

    /// <summary>All calls failed but one was "other" → not listed (FailedOther > 0 disqualifies).</summary>
    [Fact]
    public void FindMcpServerProblems_AllCallsFailedWithOtherCause_NotListed()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeMcpRun(T1, toolCalls: new Dictionary<string, McpCallCounts>
            {
                ["sonar/issues"] = new McpCallCounts { Calls = 2, FailedConnection = 1, FailedOther = 1 }
            }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        Assert.Empty(result);
    }

    /// <summary>Only 1 of 3 calls failed → not listed (not all calls failed).</summary>
    [Fact]
    public void FindMcpServerProblems_OneOfThreeCallsFailed_NotListed()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeMcpRun(T1, toolCalls: new Dictionary<string, McpCallCounts>
            {
                ["sonar/issues"] = new McpCallCounts { Calls = 3, FailedConnection = 1 }
            }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        Assert.Empty(result);
    }

    /// <summary>Newer run is OK → server not listed even though an older run had problems.</summary>
    [Fact]
    public void FindMcpServerProblems_OkRunNewerThanProblem_NotListed()
    {
        var runs = new List<PipelineRunSummary>
        {
            // Newer: 1 of 3 failed = OK
            MakeMcpRun(T2, toolCalls: new Dictionary<string, McpCallCounts>
            {
                ["sonar/issues"] = new McpCallCounts { Calls = 3, FailedConnection = 1 }
            }),
            // Older: unavailable
            MakeMcpRun(T1, unavailable: new Dictionary<string, string> { ["sonar"] = "failed" }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        Assert.Empty(result);
    }

    /// <summary>problem, neutral, problem, OK (newest first) → listed with ProblemRuns=2.</summary>
    [Fact]
    public void FindMcpServerProblems_ProblemNeutralProblemOk_ProblemRunsIs2()
    {
        var run1Id = Guid.NewGuid().ToString();
        var runs = new List<PipelineRunSummary>
        {
            // T4 (newest): all calls failed — Problem
            new()
            {
                RunId = run1Id, IssueIdentifier = "owner/repo#1", IssueTitle = "T issue",
                FinalStep = PipelineStep.Completed, StartedAtOffset = T3.AddHours(2),
                PhaseBreakdown = new Dictionary<string, PhaseUsage>
                {
                    ["codegen"] = new PhaseUsage(0, null)
                    {
                        ToolCalls = new Dictionary<string, McpCallCounts>
                            { ["grafana/query"] = new McpCallCounts { Calls = 1, FailedAuth = 1 } }
                    }
                }
            },
            // T3: no MCP data — Neutral
            MakeMcpRun(T3),
            // T2: all calls failed — Problem
            MakeMcpRun(T2, toolCalls: new Dictionary<string, McpCallCounts>
            {
                ["grafana/query"] = new McpCallCounts { Calls = 2, FailedAuth = 2 }
            }),
            // T1 (oldest): partial failure — OK
            MakeMcpRun(T1, toolCalls: new Dictionary<string, McpCallCounts>
            {
                ["grafana/query"] = new McpCallCounts { Calls = 3, FailedAuth = 1 }
            }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        Assert.Single(result);
        Assert.Equal("grafana", result[0].Server);
        Assert.Equal(2, result[0].ProblemRuns);
        Assert.Equal(run1Id, result[0].LastProblemRun.RunId);
    }

    /// <summary>web/search calls all failed → web server is never listed.</summary>
    [Fact]
    public void FindMcpServerProblems_WebSearchCallsAllFailed_WebNeverListed()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeMcpRun(T1, toolCalls: new Dictionary<string, McpCallCounts>
            {
                ["web/search"] = new McpCallCounts { Calls = 3, FailedConnection = 3 }
            }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        Assert.Empty(result);
        Assert.DoesNotContain(result, p => p.Server.Equals("web", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Server names that differ only by case are treated as one server.</summary>
    [Fact]
    public void FindMcpServerProblems_NamesInDifferentCase_TreatedAsSameServer()
    {
        var runs = new List<PipelineRunSummary>
        {
            // Uses "Sonarqube" in McpServers
            MakeMcpRun(T2, mcpServers: ["Sonarqube"]),
            // Uses "SONARQUBE" in UnavailableMcpServers
            MakeMcpRun(T1, unavailable: new Dictionary<string, string> { ["SONARQUBE"] = "needs-auth" }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        // The second run (unavailable) is a Problem; the first run has the server in McpServers
        // but no calls and not unavailable (Neutral). So ProblemRuns = 1.
        // The canonical casing comes from the newest run (T2) where it was first inserted.
        Assert.Single(result);
        Assert.Equal("Sonarqube", result[0].Server);
    }

    /// <summary>TotalCount from Aggregate includes McpServerProblems.</summary>
    [Fact]
    public void FindMcpServerProblems_TotalCount_IncludesMcpProblems()
    {
        var runs = new List<PipelineRunSummary>
        {
            // A regular failed run
            MakeRun("owner/repo#11", PipelineRunType.Implementation, PipelineStep.Failed, T1),
            // An MCP-problem run
            MakeMcpRun(T2, unavailable: new Dictionary<string, string> { ["sonarqube"] = "needs-auth" }),
        };

        var result = AttentionAggregator.Aggregate(runs);

        Assert.Single(result.McpServerProblems);
        Assert.Equal(2, result.TotalCount);
    }

    /// <summary>Runs with no tool data produce an empty list.</summary>
    [Fact]
    public void FindMcpServerProblems_RunsWithNoToolData_EmptyResult()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeRun("owner/repo#1", PipelineRunType.Implementation, PipelineStep.Failed, T1),
            MakeRun("owner/repo#2", PipelineRunType.Implementation, PipelineStep.Completed, T2),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        Assert.Empty(result);
    }

    /// <summary>FailedUnknown maps to the label "unknown", not "other".</summary>
    [Fact]
    public void FindMcpServerProblems_AllCallsFailedWithUnknownCause_ReasonIncludesUnknown()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeMcpRun(T1, toolCalls: new Dictionary<string, McpCallCounts>
            {
                ["grafana/query"] = new McpCallCounts { Calls = 2, FailedUnknown = 2 }
            }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        Assert.Single(result);
        Assert.Equal("all 2 calls failed (unknown 2)", result[0].Reason);
    }

    /// <summary>Unavailable server with status "failed" produces "unavailable (failed)".</summary>
    [Fact]
    public void FindMcpServerProblems_SonarqubeUnavailableFailed_ListedWithCorrectReason()
    {
        var runs = new List<PipelineRunSummary>
        {
            MakeMcpRun(T1, unavailable: new Dictionary<string, string> { ["sonarqube"] = "failed" }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        Assert.Single(result);
        Assert.Equal("sonarqube", result[0].Server);
        Assert.Equal("unavailable (failed)", result[0].Reason);
        Assert.Equal(1, result[0].ProblemRuns);
    }

    /// <summary>Results are sorted by LastProblemRun.StartedAtOffset descending (newest first).</summary>
    [Fact]
    public void FindMcpServerProblems_MultipleProblems_SortedNewestFirst()
    {
        var runs = new List<PipelineRunSummary>
        {
            // grafana problem run at T1 (older)
            MakeMcpRun(T1, unavailable: new Dictionary<string, string> { ["grafana"] = "needs-auth" }),
            // sonarqube problem run at T3 (newer)
            MakeMcpRun(T3, unavailable: new Dictionary<string, string> { ["sonarqube"] = "needs-auth" }),
        };

        var result = AttentionAggregator.FindMcpServerProblems(runs);

        Assert.Equal(2, result.Count);
        // Newest problem run first — sonarqube (T3) must precede grafana (T1).
        Assert.Equal("sonarqube", result[0].Server);
        Assert.Equal("grafana", result[1].Server);
    }
}
