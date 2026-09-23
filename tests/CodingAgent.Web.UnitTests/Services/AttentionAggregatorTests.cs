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
}
