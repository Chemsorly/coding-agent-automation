using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for <see cref="IAgentIssueOperations"/> default interface method implementations.
/// Exercises <c>ListOpenIssuesForProviderAsync</c> and <c>ListClosedIssuesForProviderAsync</c>
/// via a minimal concrete stub that does not override those methods, to verify the documented
/// fallback behaviour (delegates to own-tracker methods, ignoring the provider ID).
/// </summary>
public sealed class IAgentIssueOperationsDefaultMethodTests
{
    /// <summary>
    /// Minimal concrete implementation that exposes only the base interface methods.
    /// <see cref="IAgentIssueOperations.ListOpenIssuesForProviderAsync"/> and
    /// <see cref="IAgentIssueOperations.ListClosedIssuesForProviderAsync"/> are NOT overridden
    /// so the default interface implementations are exercised.
    /// </summary>
    private sealed class StubIssueOps : IAgentIssueOperations
    {
        private readonly CodingAgent.Pipeline.Models.PagedResult<CodingAgent.Pipeline.Models.IssueSummary> _openResult;
        private readonly CodingAgent.Pipeline.Models.PagedResult<CodingAgent.Pipeline.Models.IssueSummary> _closedResult;

        public StubIssueOps(
            CodingAgent.Pipeline.Models.PagedResult<CodingAgent.Pipeline.Models.IssueSummary> openResult,
            CodingAgent.Pipeline.Models.PagedResult<CodingAgent.Pipeline.Models.IssueSummary> closedResult)
        {
            _openResult = openResult;
            _closedResult = closedResult;
        }

        public Task<string?> PostCommentAsync(CodingAgent.Pipeline.Models.IssueIdentifier issueIdentifier, string body, CancellationToken ct)
            => Task.FromResult<string?>(null);

        public Task SwapLabelAsync(CodingAgent.Pipeline.Models.IssueIdentifier issueIdentifier, string newLabel, CancellationToken ct)
            => Task.CompletedTask;

        public Task<CodingAgent.Pipeline.Models.PagedResult<CodingAgent.Pipeline.Models.IssueSummary>> ListOpenIssuesAsync(
            int page, int pageSize, IReadOnlyList<string>? labels, CancellationToken ct)
            => Task.FromResult(_openResult);

        public Task<CodingAgent.Pipeline.Models.PagedResult<CodingAgent.Pipeline.Models.IssueSummary>> ListClosedIssuesAsync(
            int page, int pageSize, IReadOnlyList<string>? labels, DateTime? since, CancellationToken ct)
            => Task.FromResult(_closedResult);
    }

    private static CodingAgent.Pipeline.Models.PagedResult<CodingAgent.Pipeline.Models.IssueSummary> MakeResult(string title) =>
        new()
        {
            Items = [new CodingAgent.Pipeline.Models.IssueSummary { Identifier = "1", Title = title, Labels = [] }],
            HasMore = false,
            Page = 1,
            PageSize = 50
        };

    [Fact]
    public async Task ListOpenIssuesForProviderAsync_DefaultImpl_DelegatesToListOpenIssuesAsync_IgnoringProviderId()
    {
        var expected = MakeResult("Open Issue From Own Tracker");
        IAgentIssueOperations sut = new StubIssueOps(expected, MakeResult("closed"));

        // Calling with a different provider ID — the default impl must silently delegate to own tracker
        var result = await sut.ListOpenIssuesForProviderAsync("some-other-provider", 1, 50, null, CancellationToken.None);

        result.Should().Be(expected, "default ListOpenIssuesForProviderAsync must delegate to ListOpenIssuesAsync");
    }

    [Fact]
    public async Task ListClosedIssuesForProviderAsync_DefaultImpl_DelegatesToListClosedIssuesAsync_IgnoringProviderId()
    {
        var expected = MakeResult("Closed Issue From Own Tracker");
        IAgentIssueOperations sut = new StubIssueOps(MakeResult("open"), expected);

        // Calling with a different provider ID — the default impl must silently delegate to own tracker
        var result = await sut.ListClosedIssuesForProviderAsync("some-other-provider", 1, 50, null, since: null, CancellationToken.None);

        result.Should().Be(expected, "default ListClosedIssuesForProviderAsync must delegate to ListClosedIssuesAsync");
    }

    // ── Default throw paths ───────────────────────────────────────────────
    // A minimal stub that implements only the non-optional members, so that calling any of the
    // throw-by-default methods goes to the interface default implementation.

    private sealed class MinimalStub : IAgentIssueOperations
    {
        public Task<string?> PostCommentAsync(CodingAgent.Pipeline.Models.IssueIdentifier issueIdentifier, string body, CancellationToken ct)
            => Task.FromResult<string?>(null);
        public Task SwapLabelAsync(CodingAgent.Pipeline.Models.IssueIdentifier issueIdentifier, string newLabel, CancellationToken ct)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task CreateIssueAsync_DefaultImpl_ThrowsNotSupportedException()
    {
        IAgentIssueOperations sut = new MinimalStub();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            sut.CreateIssueAsync("T", "B", [], CancellationToken.None));
    }

    [Fact]
    public async Task CreateIssueForProviderAsync_DefaultImpl_DelegatesToCreateIssueAsync_ThrowsNotSupportedException()
    {
        IAgentIssueOperations sut = new MinimalStub();
        // CreateIssueForProviderAsync defaults to calling CreateIssueAsync, which throws
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            sut.CreateIssueForProviderAsync("p", "T", "B", [], CancellationToken.None));
    }

    [Fact]
    public async Task ListOpenIssuesAsync_DefaultImpl_ThrowsNotSupportedException()
    {
        IAgentIssueOperations sut = new MinimalStub();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            sut.ListOpenIssuesAsync(1, 50, null, CancellationToken.None));
    }

    [Fact]
    public async Task GetIssueAsync_DefaultImpl_ThrowsNotSupportedException()
    {
        IAgentIssueOperations sut = new MinimalStub();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            sut.GetIssueAsync(new CodingAgent.Pipeline.Models.IssueIdentifier("1"), CancellationToken.None));
    }

    [Fact]
    public async Task ListCommentsAsync_DefaultImpl_ThrowsNotSupportedException()
    {
        IAgentIssueOperations sut = new MinimalStub();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            sut.ListCommentsAsync(new CodingAgent.Pipeline.Models.IssueIdentifier("1"), CancellationToken.None));
    }

    [Fact]
    public async Task ListClosedIssuesAsync_DefaultImpl_ThrowsNotSupportedException()
    {
        IAgentIssueOperations sut = new MinimalStub();
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            sut.ListClosedIssuesAsync(1, 50, null, since: null, CancellationToken.None));
    }
}
