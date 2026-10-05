using AwesomeAssertions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.UnitTests.Models;

/// <summary>
/// Tests for <see cref="PipelineRun.CreateForRunType"/> — the single authoritative dispatch
/// from <see cref="PipelineRunType"/> to the matching <c>Create*</c> factory method.
/// Each test verifies that the <see cref="PipelineRun.RunType"/> on the returned run
/// matches the <see cref="PipelineRunCreationParams.RunType"/> passed in, confirming correct routing.
/// </summary>
// TODO: [WARNING] All five CreateForRunType_*_Produces* tests assert only run.RunType == input RunType.
// Since CreateCore unconditionally sets RunType = p.RunType (pass-through), these tests would all pass
// even if CreateForRunType were replaced with CreateImplementation(p) for every branch — they cannot
// detect a mis-routing regression. To be meaningful, each branch-specific test should assert a property
// that differs between the routed Create* methods: e.g. Review tests should assert ReviewPrBranchName /
// ReviewPrTargetBranch are preserved; Decomposition tests should confirm the RunType guard doesn't throw
// for valid types and does throw for invalid ones.
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

    [Fact]
    public void CreateForRunType_Implementation_ProducesImplementationRun()
    {
        var run = PipelineRun.CreateForRunType(MinimalParams(PipelineRunType.Implementation));

        run.RunType.Should().Be(PipelineRunType.Implementation);
    }

    [Fact]
    public void CreateForRunType_Review_ProducesRunWithReviewRunType()
    {
        var run = PipelineRun.CreateForRunType(MinimalParams(PipelineRunType.Review));

        run.RunType.Should().Be(PipelineRunType.Review);
    }

    [Fact]
    public void CreateForRunType_DecompositionAnalysis_ProducesDecompositionRun()
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
    public void CreateForRunType_Consolidation_ProducesRunWithConsolidationRunType()
    {
        // TODO: [WARNING] This test only asserts run.RunType == Consolidation. Completion-path routing
        // in AgentJobLifecycleService is keyed on IssueProviderConfigId == ConsolidationConstants.ProviderConfigId,
        // not on RunType. CreateForRunType intentionally does NOT inject the sentinel (callers set it),
        // so this test does not verify routing to ConsolidationJobCompletionStrategy. A separate test
        // that passes a Consolidation params object with the sentinel already set and asserts that
        // IssueProviderConfigId is preserved (not overridden to something else) would improve coverage.
        // The FromDistributionRequest_Consolidation_HasSentinelProviderConfigId test in PipelineRunFactoryTests
        // covers the full routing path via FromDistributionRequest.
        var run = PipelineRun.CreateForRunType(MinimalParams(PipelineRunType.Consolidation));

        run.RunType.Should().Be(PipelineRunType.Consolidation);
    }

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
}
