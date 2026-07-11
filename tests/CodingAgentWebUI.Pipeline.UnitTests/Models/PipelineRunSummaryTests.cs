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
}
