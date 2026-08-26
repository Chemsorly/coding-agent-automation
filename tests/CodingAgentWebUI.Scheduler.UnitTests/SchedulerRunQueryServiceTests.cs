using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Scheduler.Services;
using Xunit;

namespace CodingAgentWebUI.Scheduler.UnitTests;

/// <summary>
/// Unit tests for SchedulerRunQueryService — validates the read-only adapter behavior
/// and the in-process WasRecentlyCompleted / MarkRecentlyCompleted cache.
/// </summary>
public sealed class SchedulerRunQueryServiceTests
{
    private static SchedulerRunQueryService CreateService() => new();

    [Fact]
    public void GetActiveRuns_ReturnsEmpty()
    {
        var svc = CreateService();
        svc.GetActiveRuns().Should().BeEmpty();
        svc.HasActiveRuns.Should().BeFalse();
        svc.ActiveRunCount.Should().Be(0);
    }

    [Fact]
    public void GetRun_ReturnsNull()
    {
        var svc = CreateService();
        svc.GetRun(new RunId("any-id")).Should().BeNull();
    }

    [Fact]
    public void IsIssueBeingProcessed_AlwaysReturnsFalse()
    {
        var svc = CreateService();
        svc.IsIssueBeingProcessed(
            new IssueIdentifier("org/repo#1"),
            new ProviderConfigId("provider")).Should().BeFalse();
    }

    [Fact]
    public void WasRecentlyCompleted_BeforeMarkRecentlyCompleted_ReturnsFalse()
    {
        var svc = CreateService();
        svc.WasRecentlyCompleted(
            new IssueIdentifier("org/repo#1"),
            new ProviderConfigId("provider")).Should().BeFalse();
    }

    [Fact]
    public void MarkRecentlyCompleted_ThenWasRecentlyCompleted_ReturnsTrue()
    {
        var svc = CreateService();
        var issue = new IssueIdentifier("org/repo#42");
        var provider = new ProviderConfigId("provider-1");

        svc.MarkRecentlyCompleted(issue, provider);

        svc.WasRecentlyCompleted(issue, provider).Should().BeTrue(
            "issue should be marked as recently completed immediately after MarkRecentlyCompleted");
    }

    [Fact]
    public void WasRecentlyCompleted_DifferentIssue_ReturnsFalse()
    {
        var svc = CreateService();
        var provider = new ProviderConfigId("provider-1");

        svc.MarkRecentlyCompleted(new IssueIdentifier("org/repo#1"), provider);

        svc.WasRecentlyCompleted(new IssueIdentifier("org/repo#99"), provider)
            .Should().BeFalse("different issue must not be marked as completed");
    }

    [Theory]
    [InlineData("AddRun")]
    [InlineData("RemoveRun")]
    [InlineData("ReplaceRun")]
    [InlineData("AppendOutputLines")]
    [InlineData("GetOutputBuffer")]
    public void WriteMethods_ThrowNotSupportedException(string methodName)
    {
        var svc = CreateService();
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "provider",
            RepoProviderConfigId = "repo"
        };
        var runId = new RunId("r1");

        var act = methodName switch
        {
            "AddRun" => (Action)(() => svc.AddRun(run)),
            "RemoveRun" => () => svc.RemoveRun(runId),
            "ReplaceRun" => () => svc.ReplaceRun(run),
            "AppendOutputLines" => () => svc.AppendOutputLines(runId, []),
            "GetOutputBuffer" => () => svc.GetOutputBuffer(runId),
            _ => throw new ArgumentException($"Unknown method: {methodName}")
        };

        act.Should().Throw<NotSupportedException>(
            $"{methodName} must not be supported in the read-only Scheduler adapter");
    }
}
