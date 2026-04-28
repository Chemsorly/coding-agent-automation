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
}
