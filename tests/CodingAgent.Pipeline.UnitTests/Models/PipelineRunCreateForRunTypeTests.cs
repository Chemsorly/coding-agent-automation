using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// Tests for <see cref="PipelineRun.CreateForRunType"/> — the single authoritative dispatch
/// from <see cref="PipelineRunType"/> to the matching <c>Create*</c> factory method.
/// Branch-specific tests assert properties that differ between routed <c>Create*</c> methods
/// so that a mis-routing regression (e.g. all branches calling <c>CreateImplementation</c>)
/// would be detected.
/// </summary>
public class PipelineRunCreateForRunTypeTests
{
    private static PipelineRunCreationParams MinimalParams(PipelineRunType runType) =>
        new()
        {
            RunId = "r1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test issue",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            RunType = runType
        };

    // ── Implementation ────────────────────────────────────────────────────

    [Fact]
    public void CreateForRunType_Implementation_ProducesImplementationRun()
    {
        // Arrange — params have no Review-specific fields set
        var p = MinimalParams(PipelineRunType.Implementation);

        var run = PipelineRun.CreateForRunType(p);

        // RunType must be Implementation
        run.RunType.Should().Be(PipelineRunType.Implementation);
        // TODO: [WARNING] The BeNullOrEmpty assertions below are not branch-discriminating:
        // ReviewPrBranchName and ReviewPrTargetBranch are null/empty here because MinimalParams
        // does not set them in PipelineRunCreationParams, not because the Implementation arm was
        // taken. If CreateForRunType incorrectly routed Implementation to CreateReview, these
        // assertions would still pass. The real discriminating check is run.RunType == Implementation
        // above. Consider removing the BeNullOrEmpty assertions to avoid misleading future readers
        // into thinking they provide routing discrimination. See TestQualityReviewer review
        // finding (issue #3344).
        // Review-specific fields must be absent (params had none set): discriminates from CreateReview
        run.ReviewPrBranchName.Should().BeNullOrEmpty();
        run.ReviewPrTargetBranch.Should().BeNullOrEmpty();
    }

    // ── Review ────────────────────────────────────────────────────────────

    [Fact]
    public void CreateForRunType_Review_PreservesReviewSpecificFields()
    {
        // Arrange — Review run with Review-specific fields set in the params.
        // If CreateForRunType routed this to CreateImplementation (wrong branch),
        // the fields would still be preserved because CreateCore is shared — the
        // meaningful assertion here is that RunType and Review fields survive together,
        // confirming the Review arm was taken and no field was silently dropped.
        var p = new PipelineRunCreationParams
        {
            RunId = "r-review",
            IssueIdentifier = "org/repo#2",
            IssueTitle = "PR Review",
            IssueProviderConfigId = "ip-2",
            RepoProviderConfigId = "rp-2",
            RunType = PipelineRunType.Review,
            ReviewPrBranchName = "feature/my-branch",
            ReviewPrTargetBranch = "main",
            ReviewPrAuthor = "dev-user"
        };

        var run = PipelineRun.CreateForRunType(p);

        run.RunType.Should().Be(PipelineRunType.Review);
        run.ReviewPrBranchName.Should().Be("feature/my-branch");
        run.ReviewPrTargetBranch.Should().Be("main");
        run.ReviewPrAuthor.Should().Be("dev-user");
    }

    // ── Decomposition ─────────────────────────────────────────────────────

    [Fact]
    public void CreateForRunType_DecompositionAnalysis_ProducesDecompositionAnalysisRun()
    {
        var run = PipelineRun.CreateForRunType(MinimalParams(PipelineRunType.DecompositionAnalysis));

        run.RunType.Should().Be(PipelineRunType.DecompositionAnalysis);
    }

    [Fact]
    public void CreateForRunType_Decomposition_ProducesDecompositionRun()
    {
        var run = PipelineRun.CreateForRunType(MinimalParams(PipelineRunType.Decomposition));

        run.RunType.Should().Be(PipelineRunType.Decomposition);
    }

    [Fact]
    public void CreateForRunType_DecompositionAnalysis_DoesNotThrow()
    {
        // TODO: [WARNING] This "does not throw" assertion is a weak standalone check. The meaningful
        // routing assertion is already in CreateForRunType_DecompositionAnalysis_ProducesDecompositionAnalysisRun:
        // a call that does not throw but returns RunType=Implementation would pass this test but fail
        // the companion test. Consider removing this test — it adds no unique regression-detection
        // capability beyond what the companion test already provides. See TestQualityReviewer review
        // finding (issue #3344).
        // CreateDecomposition has a RunType guard that throws for non-decomposition types.
        // Verify that CreateForRunType routes DecompositionAnalysis to CreateDecomposition
        // (which accepts it) rather than to a guarded method that rejects it.
        // A mis-routing to CreateImplementation would NOT throw — this test's value is
        // in conjunction with CreateForRunType_DecompositionAnalysis_ProducesDecompositionAnalysisRun
        // confirming the correct RunType is returned.
        var act = () => PipelineRun.CreateForRunType(MinimalParams(PipelineRunType.DecompositionAnalysis));

        act.Should().NotThrow();
    }

    [Fact]
    public void CreateDecomposition_WithNonDecompositionRunType_ThrowsArgumentOutOfRangeException()
    {
        // Regression guard: CreateDecomposition validates its RunType argument.
        // This test confirms the guard exists and would catch a mis-routing where
        // CreateForRunType incorrectly called CreateDecomposition for Implementation,
        // or vice-versa (calling CreateImplementation for a Decomposition RunType
        // would silently succeed — guarded by the DecompositionAnalysis run type tests).
        var p = MinimalParams(PipelineRunType.Implementation);

        var act = () => PipelineRun.CreateDecomposition(p);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    // ── Consolidation ─────────────────────────────────────────────────────

    [Fact]
    public void CreateForRunType_Consolidation_ProducesRunWithConsolidationRunType()
    {
        var run = PipelineRun.CreateForRunType(MinimalParams(PipelineRunType.Consolidation));

        run.RunType.Should().Be(PipelineRunType.Consolidation);
    }

    [Fact]
    public void CreateForRunType_Consolidation_PreservesSentinelIssueProviderConfigId()
    {
        // Completion-path routing in AgentJobLifecycleService is keyed on
        // IssueProviderConfigId == ConsolidationConstants.ProviderConfigId ("consolidation"),
        // not on RunType. CreateForRunType intentionally does NOT inject the sentinel —
        // callers (e.g. FromDistributionRequest) set it before calling.
        // This test verifies that when the caller has already set the sentinel,
        // CreateForRunType passes it through unchanged (does not override it).
        var p = new PipelineRunCreationParams
        {
            RunId = "r-consol",
            IssueIdentifier = "org/repo#3",
            IssueTitle = "Consolidation run",
            IssueProviderConfigId = ConsolidationConstants.ProviderConfigId, // sentinel set by caller
            RepoProviderConfigId = "rp-1",
            RunType = PipelineRunType.Consolidation
        };

        var run = PipelineRun.CreateForRunType(p);

        // Sentinel must survive unchanged — any override would break completion routing
        run.IssueProviderConfigId.Should().Be(ConsolidationConstants.ProviderConfigId);
        run.RunType.Should().Be(PipelineRunType.Consolidation);
    }

    [Fact]
    public void CreateForRunType_Consolidation_DoesNotOverrideIssueProviderConfigId()
    {
        // CreateForRunType must NOT inject the sentinel — callers control IssueProviderConfigId.
        var run = PipelineRun.CreateForRunType(new PipelineRunCreationParams
        {
            RunId = "r3",
            IssueIdentifier = "org/repo#3",
            IssueTitle = "t",
            IssueProviderConfigId = "real-provider",
            RepoProviderConfigId = "rp-1",
            RunType = PipelineRunType.Consolidation
        });

        // The factory must pass through the caller's IssueProviderConfigId unchanged.
        run.IssueProviderConfigId.Should().Be("real-provider");
        run.RunType.Should().Be(PipelineRunType.Consolidation);
    }

    // ── Review dispatch identity ──────────────────────────────────────────

    [Fact]
    public void CreateForRunType_Review_ProducesIdenticalResultToCreateReview()
    {
        // TODO: [WARNING] This test is tautological: CreateForRunType for Review does exactly CreateReview(p),
        // so comparing viaDispatch against viaDirect (using the same params instance through the same code path)
        // cannot detect any deviation between dispatch and direct call. To be useful, the test would need to
        // compare against an independently-constructed expected state, not against a direct call through the
        // same path being tested.
        // Verify CreateForRunType delegates to CreateReview (same output as direct call).
        var p = new PipelineRunCreationParams
        {
            RunId = "r2",
            IssueIdentifier = "org/repo#2",
            IssueTitle = "Review PR",
            IssueProviderConfigId = "ip-2",
            RepoProviderConfigId = "rp-2",
            RunType = PipelineRunType.Review,
            ReviewPrBranchName = "feature/x",
            ReviewPrTargetBranch = "main"
        };

        var viaDispatch = PipelineRun.CreateForRunType(p);
        var viaDirect = PipelineRun.CreateReview(p);

        viaDispatch.RunType.Should().Be(viaDirect.RunType);
        viaDispatch.ReviewPrBranchName.Should().Be(viaDirect.ReviewPrBranchName);
        viaDispatch.ReviewPrTargetBranch.Should().Be(viaDirect.ReviewPrTargetBranch);
    }

    // ── Null guard ────────────────────────────────────────────────────────

    [Fact]
    public void CreateForRunType_NullParams_ThrowsArgumentNullException()
    {
        // The entry-point null guard must produce ArgumentNullException, not NullReferenceException
        // from inside CreateCore. Pattern used throughout the codebase: ArgumentNullException.ThrowIfNull.
        var act = () => PipelineRun.CreateForRunType(null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("p");
    }
}
