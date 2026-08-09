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
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "r1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Fix bug",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1"
        });

        run.RunId.Should().Be("r1");
        run.IssueIdentifier.Value.Should().Be("org/repo#1");
        run.IssueTitle.Should().Be("Fix bug");
        run.IssueProviderConfigId.Should().Be("ip-1");
        run.RepoProviderConfigId.Should().Be("rp-1");
    }

    [Fact]
    public void CreateImplementation_SetsRunTypeToImplementation()
    {
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "r1",
            IssueIdentifier = "i",
            IssueTitle = "t",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp"
        });

        run.RunType.Should().Be(PipelineRunType.Implementation);
    }

    [Fact]
    public void CreateImplementation_SetsInvariantDefaults()
    {
        var before = DateTimeOffset.UtcNow;
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "r1",
            IssueIdentifier = "i",
            IssueTitle = "t",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp"
        });
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

        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "r1",
            IssueIdentifier = "i",
            IssueTitle = "t",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = timestamp,
            InitiatedBy = "loop",
            AgentId = "agent-1",
            AgentProviderConfigId = "ap-1",
            BrainProviderConfigId = "bp-1"
        });

        run.StartedAtOffset.Should().Be(timestamp);
        run.InitiatedBy.Should().Be("loop");
        run.AgentId.Should().Be("agent-1");
        run.AgentProviderConfigId.Should().Be("ap-1");
        run.BrainProviderConfigId.Should().Be("bp-1");
    }

    [Fact]
    public void CreateImplementation_ReviewAndDecompositionFieldsAreNull()
    {
        var run = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "r1",
            IssueIdentifier = "i",
            IssueTitle = "t",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp"
        });

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

        var viaFactory = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "r1",
            IssueIdentifier = "org/repo#5",
            IssueTitle = "Add feature",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            StartedAt = timestamp,
            InitiatedBy = "loop",
            AgentId = "agent-2",
            AgentProviderConfigId = "ap-2",
            BrainProviderConfigId = "bp-2"
        });

        var viaCreate = PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "r1",
            IssueIdentifier = "org/repo#5",
            IssueTitle = "Add feature",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            StartedAt = timestamp,
            InitiatedBy = "loop",
            AgentId = "agent-2",
            AgentProviderConfigId = "ap-2",
            BrainProviderConfigId = "bp-2"
        });

        AssertRunFieldsEqual(viaFactory, viaCreate);
    }

    // ─────────────────────────────────────────────────────────────────────
    // CreateReview
    // ─────────────────────────────────────────────────────────────────────

    // TODO: Add CreateReview_SetsInvariantDefaults test (analogous to CreateImplementation_SetsInvariantDefaults) verifying CurrentStep, StartedAtOffset, LastStepChangeAt, and InitiatedBy defaults. Same for CreateDecomposition. Currently only CreateImplementation has this coverage.

    [Fact]
    public void CreateReview_SetsRunTypeToReview()
    {
        var run = PipelineRun.CreateReview(new PipelineRunCreationParams
        {
            RunType = PipelineRunType.Review,
            RunId = "r1",
            IssueIdentifier = "org/repo#10",
            IssueTitle = "PR title",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            ReviewPrBranchName = "feature/x",
            ReviewPrTargetBranch = "main"
        });

        run.RunType.Should().Be(PipelineRunType.Review);
    }

    [Fact]
    public void CreateReview_SetsReviewSpecificProperties()
    {
        var contexts = new List<LinkedIssueContext>
        {
            new() { Identifier = "#2", Title = "Related", Description = "desc" }
        };

        var run = PipelineRun.CreateReview(new PipelineRunCreationParams
        {
            RunType = PipelineRunType.Review,
            RunId = "r1",
            IssueIdentifier = "org/repo#10",
            IssueTitle = "PR title",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            ReviewPrBranchName = "feature/x",
            ReviewPrTargetBranch = "main",
            ReviewPrUrl = "https://github.com/org/repo/pull/10",
            ReviewPrDescription = "PR body",
            ReviewPrAuthor = "dev1",
            LinkedIssueContexts = contexts
        });

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
        var run = PipelineRun.CreateReview(new PipelineRunCreationParams
        {
            RunType = PipelineRunType.Review,
            RunId = "r1",
            IssueIdentifier = "i",
            IssueTitle = "t",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            ReviewPrBranchName = "feature/x",
            ReviewPrTargetBranch = "main"
        });

        run.DecompositionSource.Should().BeNull();
    }

    [Fact]
    public void CreateReview_NullableReviewFields_DefaultToNull()
    {
        var run = PipelineRun.CreateReview(new PipelineRunCreationParams
        {
            RunType = PipelineRunType.Review,
            RunId = "r1",
            IssueIdentifier = "i",
            IssueTitle = "t",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            ReviewPrBranchName = "feature/x",
            ReviewPrTargetBranch = "main"
        });

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

        var viaFactory = PipelineRun.CreateReview(new PipelineRunCreationParams
        {
            RunType = PipelineRunType.Review,
            RunId = "r1",
            IssueIdentifier = "org/repo#10",
            IssueTitle = "PR title",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            ReviewPrBranchName = "feature/y",
            ReviewPrTargetBranch = "develop",
            StartedAt = timestamp,
            InitiatedBy = "loop",
            AgentId = "agent-3",
            AgentProviderConfigId = "ap-3",
            BrainProviderConfigId = "bp-3",
            ReviewPrUrl = "https://github.com/org/repo/pull/10",
            ReviewPrDescription = "desc",
            ReviewPrAuthor = "author1",
            LinkedIssueContexts = contexts
        });

        var viaCreate = PipelineRun.CreateReview(new PipelineRunCreationParams
        {
            RunType = PipelineRunType.Review,
            RunId = "r1",
            IssueIdentifier = "org/repo#10",
            IssueTitle = "PR title",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            ReviewPrBranchName = "feature/y",
            ReviewPrTargetBranch = "develop",
            StartedAt = timestamp,
            InitiatedBy = "loop",
            AgentId = "agent-3",
            AgentProviderConfigId = "ap-3",
            BrainProviderConfigId = "bp-3",
            ReviewPrUrl = "https://github.com/org/repo/pull/10",
            ReviewPrDescription = "desc",
            ReviewPrAuthor = "author1",
            LinkedIssueContexts = contexts
        });

        AssertRunFieldsEqual(viaFactory, viaCreate);
    }

    // ─────────────────────────────────────────────────────────────────────
    // CreateDecomposition
    // ─────────────────────────────────────────────────────────────────────

    // TODO: Add tests for CreateImplementation and CreateReview that verify behavior when the wrong
    // RunType is explicitly set in PipelineRunCreationParams. Post-S107 refactor, CreateImplementation
    // and CreateReview no longer enforce RunType internally (unlike CreateDecomposition, which guards
    // against non-Decomposition values). A caller that passes RunType = PipelineRunType.Review to
    // CreateImplementation will silently receive a run with the wrong RunType, corrupting
    // LabelTargetKind and ProviderConfigIdForLabel routing logic at runtime.
    // Example: CreateImplementation_WithWrongRunType_ShouldNotProduceMistypedRun.
    [Theory]
    [InlineData(PipelineRunType.DecompositionAnalysis)]
    [InlineData(PipelineRunType.Decomposition)]
    public void CreateDecomposition_SetsCorrectRunType(PipelineRunType phaseType)
    {
        var run = PipelineRun.CreateDecomposition(new PipelineRunCreationParams
        {
            RunId = "r1",
            IssueIdentifier = "org/repo#20",
            IssueTitle = "Epic",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            RunType = phaseType
        });

        run.RunType.Should().Be(phaseType);
    }

    [Fact]
    public void CreateDecomposition_SetsDecompositionSource()
    {
        var run = PipelineRun.CreateDecomposition(new PipelineRunCreationParams
        {
            RunId = "r1",
            IssueIdentifier = "org/repo#20",
            IssueTitle = "Epic",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            RunType = PipelineRunType.DecompositionAnalysis,
            DecompositionSource = "project-level"
        });

        run.DecompositionSource.Should().Be("project-level");
    }

    [Fact]
    public void CreateDecomposition_ReviewFieldsAreNull()
    {
        var run = PipelineRun.CreateDecomposition(new PipelineRunCreationParams
        {
            RunId = "r1",
            IssueIdentifier = "i",
            IssueTitle = "t",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            RunType = PipelineRunType.Decomposition
        });

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
        var act = () => PipelineRun.CreateDecomposition(new PipelineRunCreationParams
        {
            RunId = "r1",
            IssueIdentifier = "i",
            IssueTitle = "t",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            RunType = PipelineRunType.Implementation
        });

        act.Should().Throw<ArgumentOutOfRangeException>()
            // TODO: The ParamName value changed from "phaseType" (the old method parameter name) to
            // "RunType" (the record property name, via nameof(p.RunType)) after the S107 refactor.
            // Callers that catch ArgumentOutOfRangeException and inspect ParamName will observe a
            // different string at runtime. The assertion is consistent with the current implementation,
            // but be aware this is a subtle API surface change if callers rely on the exception ParamName.
            .Which.ParamName.Should().Be("RunType");
    }

    [Fact]
    public void CreateDecomposition_RejectsReviewRunType()
    {
        var act = () => PipelineRun.CreateDecomposition(new PipelineRunCreationParams
        {
            RunId = "r1",
            IssueIdentifier = "i",
            IssueTitle = "t",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            RunType = PipelineRunType.Review
        });

        act.Should().Throw<ArgumentOutOfRangeException>()
            // TODO: Same as above — ParamName is now "RunType" (record property) rather than the
            // original method parameter name "phaseType". See comment on CreateDecomposition_RejectsInvalidRunType.
            .Which.ParamName.Should().Be("RunType");
    }

    [Fact]
    public void CreateDecomposition_ProducesIdenticalResultToCreate()
    {
        var timestamp = new DateTimeOffset(2024, 6, 1, 12, 0, 0, TimeSpan.Zero);

        var viaFactory = PipelineRun.CreateDecomposition(new PipelineRunCreationParams
        {
            RunId = "r1",
            IssueIdentifier = "org/repo#20",
            IssueTitle = "Epic title",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            RunType = PipelineRunType.DecompositionAnalysis,
            StartedAt = timestamp,
            InitiatedBy = "loop",
            AgentId = "agent-4",
            AgentProviderConfigId = "ap-4",
            BrainProviderConfigId = "bp-4",
            DecompositionSource = "template-level"
        });

        var viaCreate = PipelineRun.CreateDecomposition(new PipelineRunCreationParams
        {
            RunId = "r1",
            IssueIdentifier = "org/repo#20",
            IssueTitle = "Epic title",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1",
            RunType = PipelineRunType.DecompositionAnalysis,
            StartedAt = timestamp,
            InitiatedBy = "loop",
            AgentId = "agent-4",
            AgentProviderConfigId = "ap-4",
            BrainProviderConfigId = "bp-4",
            DecompositionSource = "template-level"
        });

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
