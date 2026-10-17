using AwesomeAssertions;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using CodingAgent.Web.Ui;
using Moq;

namespace CodingAgent.Web.UnitTests.Services;

/// <summary>
/// Spec 051 2.3b: the API computes the backlog (its BacklogServiceTests cover that). The web service
/// only reads it, derives the blocked issues and degrades to an empty backlog when the API fails.
/// </summary>
public class BlockedIssuesServiceTests
{
    private static readonly BacklogResult Backlog = new(
    [
        new BacklogIssue("10", "Blocked by an issue", "https://x/issues/10", IsReady: false, BlockedBy: [5]),
        new BacklogIssue("11", "Blocked by another tracker", null, IsReady: false, BlockedBy: [], BlockedByUrls: ["https://y/issues/3"]),
        new BacklogIssue("12", "Parked", null, IsReady: false, BlockedBy: [], Labels: ["backlog"]),
        new BacklogIssue("13", "Ready", null, IsReady: true, BlockedBy: []),
    ], IsTruncated: true);

    private static BlockedIssuesService Service(Mock<IUiBrowseClient> browse) => new(browse.Object, Microsoft.Extensions.Logging.Abstractions.NullLogger<BlockedIssuesService>.Instance);

    [Fact]
    public async Task GetBacklogAsync_ReturnsTheApisBacklog_ForTheProject()
    {
        var browse = new Mock<IUiBrowseClient>();
        browse.Setup(b => b.GetBacklogAsync("proj-1", It.IsAny<CancellationToken>())).ReturnsAsync(Backlog);

        var result = await Service(browse).GetBacklogAsync("proj-1", CancellationToken.None);

        result.Should().BeSameAs(Backlog);
    }

    [Fact]
    public async Task GetBlockedIssuesAsync_ReturnsTheIssuesWaitingOnOpenDependencies()
    {
        var browse = new Mock<IUiBrowseClient>();
        browse.Setup(b => b.GetBacklogAsync(null, It.IsAny<CancellationToken>())).ReturnsAsync(Backlog);

        var blocked = await Service(browse).GetBlockedIssuesAsync(null, CancellationToken.None);

        blocked.Select(b => b.Identifier).Should().Equal("10", "11");
        blocked[0].BlockedBy.Should().Equal(5);
        blocked[1].BlockedByUrls.Should().Equal("https://y/issues/3");
    }

    [Fact]
    public async Task GetBacklogAsync_WhenTheApiFails_ReturnsAnEmptyBacklog()
    {
        var browse = new Mock<IUiBrowseClient>();
        browse.Setup(b => b.GetBacklogAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("api down"));

        var result = await Service(browse).GetBacklogAsync(null, CancellationToken.None);

        result.Issues.Should().BeEmpty();
        result.IsTruncated.Should().BeFalse();
    }

    [Fact]
    public async Task GetBacklogAsync_WhenTheCallerCancels_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var browse = new Mock<IUiBrowseClient>();
        browse.Setup(b => b.GetBacklogAsync(It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException(cts.Token));

        var act = () => Service(browse).GetBacklogAsync(null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
