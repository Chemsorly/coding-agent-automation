using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Models;
using CodingAgent.Scheduler.Services;
using Moq;
using Xunit;

namespace CodingAgent.Scheduler.UnitTests;

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
    public void IsIssueBeingProcessed_AlwaysReturnsFalse()
    {
        var svc = CreateService();
        svc.IsIssueBeingProcessed(
            new IssueIdentifier("org/repo#1"),
            new ProviderConfigId("provider")).Should().BeFalse();
    }

    [Fact]
    public void IsIssueBeingProcessed_EmptyIdentifier_ThrowsArgumentException()
    {
        var svc = CreateService();
        var act = () => svc.IsIssueBeingProcessed(
            new IssueIdentifier(""),
            new ProviderConfigId("provider"));
        act.Should().Throw<ArgumentException>(
            "IsIssueBeingProcessed must throw for an empty identifier in all implementations");
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

    // ── GetActiveRunBranchesAsync — Scheduler-specific variant ────────────────

    /// <summary>
    /// Acceptance-criteria test (Issue #2270): Scheduler-specific variant.
    /// Demonstrates that <see cref="SchedulerRunQueryService.GetActiveRunBranchesAsync"/>
    /// returns branches from the API even though the Scheduler has no in-memory run state.
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
    /// also returns empty.
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
