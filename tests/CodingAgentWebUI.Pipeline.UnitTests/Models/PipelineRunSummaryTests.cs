using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Tests for PipelineRunSummary mapping from PipelineRun.ToSummary().
/// </summary>
public class PipelineRunSummaryTests
{
    [Fact]
    public void IsRework_WhenLinkedPullRequestSet_ReturnsTrue()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            LinkedPullRequest = new LinkedPullRequest
            {
                Number = 7,
                BranchName = "feature/auto-42-test",
                Url = "https://github.com/test/repo/pull/7",
                IsDraft = false
            }
        };

        var summary = run.ToSummary();

        summary.IsRework.Should().BeTrue();
    }

    [Fact]
    public void ToSummary_FlattensQualityGateOutcomes_FromLatestReport()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            LatestQualityReport = new QualityGateReport
            {
                Compilation = new GateResult { GateName = "Compilation", Passed = true },
                Tests = new GateResult { GateName = "Tests", Passed = false },
                Coverage = new GateResult { GateName = "Coverage", Passed = false }
            }
        };

        var summary = run.ToSummary();

        summary.QualityGateOutcomes.Should().NotBeNull();
        summary.QualityGateOutcomes!.Should().Contain(g => g.GateName == "Compilation" && g.Passed);
        summary.QualityGateOutcomes!.Should().Contain(g => g.GateName == "Tests" && !g.Passed);
        summary.QualityGateOutcomes!.Should().Contain(g => g.GateName == "Coverage" && !g.Passed);
    }

    [Fact]
    public void ToSummary_NoQualityReport_LeavesGateOutcomesNull()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };

        run.ToSummary().QualityGateOutcomes.Should().BeNull();
    }

    [Fact]
    public void IsRework_WhenLinkedPullRequestNull_ReturnsFalse()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };

        var summary = run.ToSummary();

        summary.IsRework.Should().BeFalse();
    }

    [Fact]
    public void AgentId_WhenSet_MapsToSummary()
    {
        // TODO: Add equivalent test for ProjectId mapping (both set and null cases) to match AgentId coverage
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            AgentId = "agent-01"
        };

        var summary = run.ToSummary();

        summary.AgentId.Should().Be("agent-01");
    }

    [Fact]
    public void AgentId_WhenNull_MapsNullToSummary()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };

        var summary = run.ToSummary();

        summary.AgentId.Should().BeNull();
    }

    [Fact]
    public void FailureReason_WhenSet_MapsToSummary()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            FailureReason = "Analysis failed after 2 attempt(s)"
        };

        var summary = run.ToSummary();

        summary.FailureReason.Should().Be("Analysis failed after 2 attempt(s)");
    }

    [Fact]
    public void FailureReason_WhenNull_MapsNullToSummary()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };

        var summary = run.ToSummary();

        summary.FailureReason.Should().BeNull();
    }

    [Fact]
    public void ToSummary_MapsNonEmptyPhaseBreakdown()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };
        run.Metrics.PhaseBreakdown["analysis"] = new PhaseUsage(1500, 0.05m);
        run.Metrics.PhaseBreakdown["codegen"] = new PhaseUsage(50000, 1.20m);

        var summary = run.ToSummary();

        summary.PhaseBreakdown.Should().NotBeNull();
        summary.PhaseBreakdown.Should().HaveCount(2);
        summary.PhaseBreakdown!["analysis"].Tokens.Should().Be(1500);
        summary.PhaseBreakdown["analysis"].Cost.Should().Be(0.05m);
        summary.PhaseBreakdown["codegen"].Tokens.Should().Be(50000);
        summary.PhaseBreakdown["codegen"].Cost.Should().Be(1.20m);
    }

    [Fact]
    public void ToSummary_MapsEmptyPhaseBreakdownToNull()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };

        var summary = run.ToSummary();

        summary.PhaseBreakdown.Should().BeNull();
    }

    // TODO: Add backward-compatibility deserialization test — deserialize a JSON string representing
    // a pre-feature PipelineRunSummary (without PhaseBreakdown property) and assert PhaseBreakdown is null.
    // TODO: Add backward-compatibility deserialization test for ProjectId — deserialize a JSON payload
    // from before this fix (lacking ProjectId field) into PipelineRunSummary and assert ProjectId is null without errors.

    [Fact]
    public void ToSummary_WithFinalStepOverride_UsesOverrideInsteadOfCurrentStep()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Override test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };
        run.CurrentStep = PipelineStep.RunningQualityGates;

        var summary = run.ToSummary(finalStepOverride: PipelineStep.Failed);

        summary.FinalStep.Should().Be(PipelineStep.Failed);
        run.CurrentStep.Should().Be(PipelineStep.RunningQualityGates,
            "ToSummary must not mutate the caller's PipelineRun.CurrentStep");
    }

    [Fact]
    public void ToSummary_WithNullFinalStepOverride_UsesCurrentStep()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Null override test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };
        run.CurrentStep = PipelineStep.Completed;

        var summary = run.ToSummary(finalStepOverride: null);

        summary.FinalStep.Should().Be(PipelineStep.Completed);
    }

    [Fact]
    public void AgentProviderConfigId_WhenSet_MapsToSummary()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            AgentProviderConfigId = "agent-provider-cfg-01"
        };

        var summary = run.ToSummary();

        summary.AgentProviderConfigId.Should().Be("agent-provider-cfg-01");
    }

    [Fact]
    public void AgentProviderConfigId_WhenNull_MapsNullToSummary()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };

        var summary = run.ToSummary();

        summary.AgentProviderConfigId.Should().BeNull();
    }

    [Fact]
    public void ToSummary_MapsCacheTokensFromMetrics()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "Cache Token Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };
        run.CacheReadTokens = 100;
        run.CacheWriteTokens = 50;

        var summary = run.ToSummary();

        summary.CacheReadTokens.Should().Be(100);
        summary.CacheWriteTokens.Should().Be(50);
    }

    // TODO: This zero-default test does not detect a regression if the `CacheReadTokens = CacheReadTokens`
    // mapping line is accidentally omitted from ToSummary() — the long default value of 0 would mask the
    // missing mapping. The non-zero mapping test (ToSummary_MapsCacheTokensFromMetrics) above already
    // catches that omission. Consider removing this test or converting it to assert a meaningful invariant.
    [Fact]
    public void ToSummary_WhenNoCacheTokens_CacheFieldsAreZero()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "42",
            IssueTitle = "No Cache Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };

        var summary = run.ToSummary();

        summary.CacheReadTokens.Should().Be(0);
        summary.CacheWriteTokens.Should().Be(0);
    }

    /// <summary>
    /// Type-locking test: asserts that IssueIdentifier is typed as IssueIdentifier (not string).
    /// Accessing .Value only compiles if the property is an IssueIdentifier struct — this test
    /// will fail to compile if the property type reverts to string.
    /// </summary>
    // Note: The type-lock is only partially enforced — `string issueIdStr = summary.IssueIdentifier`
    // compiles whether the property is IssueIdentifier or string (implicit conversion goes both ways).
    // The real compile-time guard is `_ = summary.IssueIdentifier.Value`. Consider also adding an edge
    // case assertion for default(IssueIdentifier) / null Value to improve coverage.
    [Fact]
    public void ToSummary_IssueIdentifier_IsTypedAsIssueIdentifier()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Type lock test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        };

        var summary = run.ToSummary();

        // .Value is a member of IssueIdentifier struct — fails to compile if the property reverts to string
        string issueIdStr = summary.IssueIdentifier; // implicit conversion fires at assignment
        issueIdStr.Should().Be("org/repo#42");
        // Also access .Value to confirm compile-time type (would not compile if property were string)
        _ = summary.IssueIdentifier.Value;
    }
}
