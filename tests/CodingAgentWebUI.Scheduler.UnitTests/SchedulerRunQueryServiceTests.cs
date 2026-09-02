using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Scheduler.Services;
using Moq;
using Xunit;

namespace CodingAgentWebUI.Scheduler.UnitTests;

/// <summary>
/// Unit tests for SchedulerRunQueryService — validates the read-only adapter behavior
/// and the in-process WasRecentlyCompleted / MarkRecentlyCompleted cache.
/// </summary>
public sealed class SchedulerRunQueryServiceTests
{
    private static SchedulerRunQueryService CreateService(
        IPipelineApiRunHistoryClient? client = null)
    {
        client ??= new Mock<IPipelineApiRunHistoryClient>().Object;
        return new SchedulerRunQueryService(client);
    }

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

    // ── GetActiveRunBranchesAsync — Scheduler-specific variant ────────────────

    /// <summary>
    /// Acceptance-criteria test (Issue #2270): Scheduler-specific variant.
    /// Demonstrates that <see cref="SchedulerRunQueryService.GetActiveRunBranchesAsync"/>
    /// returns branches from the API even though <see cref="SchedulerRunQueryService.GetActiveRuns"/>
    /// always returns empty.
    ///
    /// This is the key fix: in the old implementation GetActiveRuns() always returned [],
    /// so HousekeepingService could never populate activeRunBranches in the Scheduler
    /// deployment, causing branch updates to fire on live-run branches.
    /// </summary>
    [Fact]
    public async Task GetActiveRunBranchesAsync_ApiReturnsBranches_ReturnsThem()
    {
        // Arrange: API returns two active branch names.
        var clientMock = new Mock<IPipelineApiRunHistoryClient>();
        clientMock
            .Setup(c => c.GetActiveBranchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["feature/auto-42-my-feature", "feature/auto-99-other"]);

        var svc = CreateService(clientMock.Object);

        // Assert: GetActiveRuns() is still empty (as before — it cannot access in-memory state).
        svc.GetActiveRuns().Should().BeEmpty(
            "GetActiveRuns() has no in-process run state in the Scheduler and always returns empty");

        // Act
        var branches = await svc.GetActiveRunBranchesAsync(CancellationToken.None);

        // Assert: GetActiveRunBranchesAsync() returns the API-sourced branch names.
        branches.Should().Contain("feature/auto-42-my-feature",
            "GetActiveRunBranchesAsync must return branches reported by the API");
        branches.Should().Contain("feature/auto-99-other");
        branches.Should().HaveCount(2);
    }

    /// <summary>
    /// Verifies that GetActiveRunBranchesAsync uses case-insensitive branch comparison.
    /// </summary>
    [Fact]
    public async Task GetActiveRunBranchesAsync_BranchNamesAreCaseInsensitive()
    {
        var clientMock = new Mock<IPipelineApiRunHistoryClient>();
        clientMock
            .Setup(c => c.GetActiveBranchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)["Feature/Auto-42-My-Feature"]);

        var svc = CreateService(clientMock.Object);

        var branches = await svc.GetActiveRunBranchesAsync();

        branches.Contains("feature/auto-42-my-feature").Should().BeTrue(
            "branch name lookup must be case-insensitive");
        branches.Contains("FEATURE/AUTO-42-MY-FEATURE").Should().BeTrue(
            "branch name lookup must be case-insensitive regardless of casing");
    }

    /// <summary>
    /// Verifies that when the API returns an empty list (no active runs), GetActiveRunBranchesAsync
    /// also returns empty — consistent with the default interface implementation.
    /// </summary>
    [Fact]
    public async Task GetActiveRunBranchesAsync_ApiReturnsEmpty_ReturnsEmpty()
    {
        var clientMock = new Mock<IPipelineApiRunHistoryClient>();
        clientMock
            .Setup(c => c.GetActiveBranchesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<string>)[]);

        var svc = CreateService(clientMock.Object);

        var branches = await svc.GetActiveRunBranchesAsync();

        branches.Should().BeEmpty(
            "when the API reports no active runs, GetActiveRunBranchesAsync must return empty");
    }
}
