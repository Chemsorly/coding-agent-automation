using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Models;

public class PipelineRunVariantFactoryTests
{
    // ─────────────────────────────────────────────────────────────────────
    // CreateImplementation
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public void CreateImplementation_SetsRequiredProperties()
    {
        var run = PipelineRun.CreateImplementation(
            runId: "r1",
            issueIdentifier: "org/repo#1",
            issueTitle: "Fix bug",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1");

        run.RunId.Should().Be("r1");
        run.IssueIdentifier.Value.Should().Be("org/repo#1");
        run.IssueTitle.Should().Be("Fix bug");
        run.IssueProviderConfigId.Should().Be("ip-1");
        run.RepoProviderConfigId.Should().Be("rp-1");
    }

    [Fact]
    public void CreateImplementation_SetsRunTypeToImplementation()
    {
        var run = PipelineRun.CreateImplementation(
            runId: "r1",
            issueIdentifier: "i",
            issueTitle: "t",
            issueProviderConfigId: "ip",
            repoProviderConfigId: "rp");

        run.RunType.Should().Be(PipelineRunType.Implementation);
    }

    [Fact]
    public void CreateImplementation_SetsInvariantDefaults()
    {
        var before = DateTimeOffset.UtcNow;
        var run = PipelineRun.CreateImplementation(
            runId: "r1",
            issueIdentifier: "i",
            issueTitle: "t",
            issueProviderConfigId: "ip",
            repoProviderConfigId: "rp");
        var after = DateTimeOffset.UtcNow;

        run.CurrentStep.Should().Be(PipelineStep.Created);
        run.StartedAtOffset.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        run.LastStepChangeAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        run.InitiatedBy.Should().Be("manual");
    }

    [Fact]
    public void CreateImplementation_PassesThroughSharedOptionalProperties()
    {
        var timestamp = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);

        var run = PipelineRun.CreateImplementation(
            runId: "r1",
            issueIdentifier: "i",
            issueTitle: "t",
            issueProviderConfigId: "ip",
            repoProviderConfigId: "rp",
            startedAt: timestamp,
            initiatedBy: "loop",
            agentId: "agent-1",
            agentProviderConfigId: "ap-1",
            brainProviderConfigId: "bp-1");

        run.StartedAtOffset.Should().Be(timestamp);
        run.InitiatedBy.Should().Be("loop");
        run.AgentId.Should().Be("agent-1");
        run.AgentProviderConfigId.Should().Be("ap-1");
        run.BrainProviderConfigId.Should().Be("bp-1");
    }

    [Fact]
    public void CreateImplementation_ReviewAndDecompositionFieldsAreNull()
    {
        var run = PipelineRun.CreateImplementation(
            runId: "r1",
            issueIdentifier: "i",
            issueTitle: "t",
            issueProviderConfigId: "ip",
            repoProviderConfigId: "rp");

        run.ReviewPrBranchName.Should().BeNull();
        run.ReviewPrTargetBranch.Should().BeNull();
        run.ReviewPrUrl.Should().BeNull();
        run.ReviewPrDescription.Should().BeNull();
        run.ReviewPrAuthor.Should().BeNull();
        run.LinkedIssueContexts.Should().BeNull();
        run.DecompositionSource.Should().BeNull();
    }

    [Fact]
    public void CreateImplementation_ProducesIdenticalResultToCreate()
    {
        var timestamp = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);

        var viaFactory = PipelineRun.CreateImplementation(
            runId: "r1",
            issueIdentifier: "org/repo#5",
            issueTitle: "Add feature",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1",
            startedAt: timestamp,
            initiatedBy: "loop",
            agentId: "agent-2",
            agentProviderConfigId: "ap-2",
            brainProviderConfigId: "bp-2");

        var viaCreate = PipelineRun.Create(
            runId: "r1",
            issueIdentifier: "org/repo#5",
            issueTitle: "Add feature",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1",
            runType: PipelineRunType.Implementation,
            startedAt: timestamp,
            initiatedBy: "loop",
            agentId: "agent-2",
            agentProviderConfigId: "ap-2",
            brainProviderConfigId: "bp-2");

        AssertRunFieldsEqual(viaFactory, viaCreate);
    }

    // ─────────────────────────────────────────────────────────────────────
    // CreateReview
    // ─────────────────────────────────────────────────────────────────────

    // TODO: Add CreateReview_SetsInvariantDefaults test (analogous to CreateImplementation_SetsInvariantDefaults) verifying CurrentStep, StartedAtOffset, LastStepChangeAt, and InitiatedBy defaults. Same for CreateDecomposition. Currently only CreateImplementation has this coverage.

    [Fact]
    public void CreateReview_SetsRunTypeToReview()
    {
        var run = PipelineRun.CreateReview(
            runId: "r1",
            issueIdentifier: "org/repo#10",
            issueTitle: "PR title",
            issueProviderConfigId: "ip",
            repoProviderConfigId: "rp",
            reviewPrBranchName: "feature/x",
            reviewPrTargetBranch: "main");

        run.RunType.Should().Be(PipelineRunType.Review);
    }

    [Fact]
    public void CreateReview_SetsReviewSpecificProperties()
    {
        var contexts = new List<LinkedIssueContext>
        {
            new() { Identifier = "#2", Title = "Related", Description = "desc" }
        };

        var run = PipelineRun.CreateReview(
            runId: "r1",
            issueIdentifier: "org/repo#10",
            issueTitle: "PR title",
            issueProviderConfigId: "ip",
            repoProviderConfigId: "rp",
            reviewPrBranchName: "feature/x",
            reviewPrTargetBranch: "main",
            reviewPrUrl: "https://github.com/org/repo/pull/10",
            reviewPrDescription: "PR body",
            reviewPrAuthor: "dev1",
            linkedIssueContexts: contexts);

        run.ReviewPrBranchName.Should().Be("feature/x");
        run.ReviewPrTargetBranch.Should().Be("main");
        run.ReviewPrUrl.Should().Be("https://github.com/org/repo/pull/10");
        run.ReviewPrDescription.Should().Be("PR body");
        run.ReviewPrAuthor.Should().Be("dev1");
        run.LinkedIssueContexts.Should().BeSameAs(contexts);
    }

    [Fact]
    public void CreateReview_DecompositionFieldIsNull()
    {
        var run = PipelineRun.CreateReview(
            runId: "r1",
            issueIdentifier: "i",
            issueTitle: "t",
            issueProviderConfigId: "ip",
            repoProviderConfigId: "rp",
            reviewPrBranchName: "feature/x",
            reviewPrTargetBranch: "main");

        run.DecompositionSource.Should().BeNull();
    }

    [Fact]
    public void CreateReview_NullableReviewFields_DefaultToNull()
    {
        var run = PipelineRun.CreateReview(
            runId: "r1",
            issueIdentifier: "i",
            issueTitle: "t",
            issueProviderConfigId: "ip",
            repoProviderConfigId: "rp",
            reviewPrBranchName: "feature/x",
            reviewPrTargetBranch: "main");

        run.ReviewPrUrl.Should().BeNull();
        run.ReviewPrDescription.Should().BeNull();
        run.ReviewPrAuthor.Should().BeNull();
        run.LinkedIssueContexts.Should().BeNull();
    }

    [Fact]
    public void CreateReview_ProducesIdenticalResultToCreate()
    {
        var timestamp = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);
        var contexts = new List<LinkedIssueContext>
        {
            new() { Identifier = "#3", Title = "Linked", Description = "d" }
        };

        var viaFactory = PipelineRun.CreateReview(
            runId: "r1",
            issueIdentifier: "org/repo#10",
            issueTitle: "PR title",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1",
            reviewPrBranchName: "feature/y",
            reviewPrTargetBranch: "develop",
            startedAt: timestamp,
            initiatedBy: "loop",
            agentId: "agent-3",
            agentProviderConfigId: "ap-3",
            brainProviderConfigId: "bp-3",
            reviewPrUrl: "https://github.com/org/repo/pull/10",
            reviewPrDescription: "desc",
            reviewPrAuthor: "author1",
            linkedIssueContexts: contexts);

        var viaCreate = PipelineRun.Create(
            runId: "r1",
            issueIdentifier: "org/repo#10",
            issueTitle: "PR title",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1",
            runType: PipelineRunType.Review,
            startedAt: timestamp,
            initiatedBy: "loop",
            agentId: "agent-3",
            agentProviderConfigId: "ap-3",
            brainProviderConfigId: "bp-3",
            reviewPrBranchName: "feature/y",
            reviewPrTargetBranch: "develop",
            reviewPrUrl: "https://github.com/org/repo/pull/10",
            reviewPrDescription: "desc",
            reviewPrAuthor: "author1",
            linkedIssueContexts: contexts);

        AssertRunFieldsEqual(viaFactory, viaCreate);
    }

    // ─────────────────────────────────────────────────────────────────────
    // CreateDecomposition
    // ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(PipelineRunType.DecompositionAnalysis)]
    [InlineData(PipelineRunType.Decomposition)]
    public void CreateDecomposition_SetsCorrectRunType(PipelineRunType phaseType)
    {
        var run = PipelineRun.CreateDecomposition(
            runId: "r1",
            issueIdentifier: "org/repo#20",
            issueTitle: "Epic",
            issueProviderConfigId: "ip",
            repoProviderConfigId: "rp",
            phaseType: phaseType);

        run.RunType.Should().Be(phaseType);
    }

    [Fact]
    public void CreateDecomposition_SetsDecompositionSource()
    {
        var run = PipelineRun.CreateDecomposition(
            runId: "r1",
            issueIdentifier: "org/repo#20",
            issueTitle: "Epic",
            issueProviderConfigId: "ip",
            repoProviderConfigId: "rp",
            phaseType: PipelineRunType.DecompositionAnalysis,
            decompositionSource: "project-level");

        run.DecompositionSource.Should().Be("project-level");
    }

    [Fact]
    public void CreateDecomposition_ReviewFieldsAreNull()
    {
        var run = PipelineRun.CreateDecomposition(
            runId: "r1",
            issueIdentifier: "i",
            issueTitle: "t",
            issueProviderConfigId: "ip",
            repoProviderConfigId: "rp",
            phaseType: PipelineRunType.Decomposition);

        run.ReviewPrBranchName.Should().BeNull();
        run.ReviewPrTargetBranch.Should().BeNull();
        run.ReviewPrUrl.Should().BeNull();
        run.ReviewPrDescription.Should().BeNull();
        run.ReviewPrAuthor.Should().BeNull();
        run.LinkedIssueContexts.Should().BeNull();
    }

    [Fact]
    public void CreateDecomposition_RejectsInvalidRunType()
    {
        var act = () => PipelineRun.CreateDecomposition(
            runId: "r1",
            issueIdentifier: "i",
            issueTitle: "t",
            issueProviderConfigId: "ip",
            repoProviderConfigId: "rp",
            phaseType: PipelineRunType.Implementation);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("phaseType");
    }

    [Fact]
    public void CreateDecomposition_RejectsReviewRunType()
    {
        var act = () => PipelineRun.CreateDecomposition(
            runId: "r1",
            issueIdentifier: "i",
            issueTitle: "t",
            issueProviderConfigId: "ip",
            repoProviderConfigId: "rp",
            phaseType: PipelineRunType.Review);

        act.Should().Throw<ArgumentOutOfRangeException>()
            .Which.ParamName.Should().Be("phaseType");
    }

    [Fact]
    public void CreateDecomposition_ProducesIdenticalResultToCreate()
    {
        var timestamp = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);

        var viaFactory = PipelineRun.CreateDecomposition(
            runId: "r1",
            issueIdentifier: "org/repo#20",
            issueTitle: "Epic title",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1",
            phaseType: PipelineRunType.DecompositionAnalysis,
            startedAt: timestamp,
            initiatedBy: "loop",
            agentId: "agent-4",
            agentProviderConfigId: "ap-4",
            brainProviderConfigId: "bp-4",
            decompositionSource: "template-level");

        var viaCreate = PipelineRun.Create(
            runId: "r1",
            issueIdentifier: "org/repo#20",
            issueTitle: "Epic title",
            issueProviderConfigId: "ip-1",
            repoProviderConfigId: "rp-1",
            runType: PipelineRunType.DecompositionAnalysis,
            startedAt: timestamp,
            initiatedBy: "loop",
            agentId: "agent-4",
            agentProviderConfigId: "ap-4",
            brainProviderConfigId: "bp-4",
            decompositionSource: "template-level");

        AssertRunFieldsEqual(viaFactory, viaCreate);
    }

    // ─────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Field-by-field comparison of two PipelineRun instances.
    /// Uses field comparison (not reference equality) because PipelineRun contains
    /// concurrent collections that make reference equality inappropriate.
    /// </summary>
    // TODO: AssertRunFieldsEqual omits LastStepChangeAt (non-deterministic, uses UtcNow independently) and StartedAt (DateTime, obsolete). StartedAt is deterministic when a fixed timestamp is provided and could be asserted here to strengthen the equivalence guarantee.
    private static void AssertRunFieldsEqual(PipelineRun actual, PipelineRun expected)
    {
        actual.RunId.Should().Be(expected.RunId);
        actual.IssueIdentifier.Should().Be(expected.IssueIdentifier);
        actual.IssueTitle.Should().Be(expected.IssueTitle);
        actual.IssueProviderConfigId.Should().Be(expected.IssueProviderConfigId);
        actual.RepoProviderConfigId.Should().Be(expected.RepoProviderConfigId);
        actual.RunType.Should().Be(expected.RunType);
        actual.StartedAtOffset.Should().Be(expected.StartedAtOffset);
        actual.CurrentStep.Should().Be(expected.CurrentStep);
        actual.InitiatedBy.Should().Be(expected.InitiatedBy);
        actual.AgentId.Should().Be(expected.AgentId);
        actual.AgentProviderConfigId.Should().Be(expected.AgentProviderConfigId);
        actual.BrainProviderConfigId.Should().Be(expected.BrainProviderConfigId);
        actual.ReviewPrBranchName.Should().Be(expected.ReviewPrBranchName);
        actual.ReviewPrTargetBranch.Should().Be(expected.ReviewPrTargetBranch);
        actual.ReviewPrUrl.Should().Be(expected.ReviewPrUrl);
        actual.ReviewPrDescription.Should().Be(expected.ReviewPrDescription);
        actual.ReviewPrAuthor.Should().Be(expected.ReviewPrAuthor);
        actual.LinkedIssueContexts.Should().BeSameAs(expected.LinkedIssueContexts);
        actual.DecompositionSource.Should().Be(expected.DecompositionSource);
    }
}
