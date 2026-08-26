using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="DependencyChecker.CheckAsync"/>.
///
/// Key behaviors under test:
/// — no-op on null/empty body
/// — all-satisfied → IsReady=true
/// — any open dep → IsReady=false, blocked list populated
/// — API failure treated as unresolved (does not throw, does not block other deps)
/// — cache hit avoids redundant API call
/// — self-reference is excluded from the dependency list
/// — cancellation propagates
/// — multiple mixed deps (some closed, some open)
/// </summary>
public sealed class DependencyCheckerTests
{
    private static ILogger Logger => Mock.Of<ILogger>();

    private static Mock<IIssueProvider> ProviderReturning(
        string issueNumber, bool isClosed) =>
        ProviderWith((issueNumber, isClosed));

    private static Mock<IIssueProvider> ProviderWith(
        params (string issueNumber, bool isClosed)[] mappings)
    {
        var mock = new Mock<IIssueProvider>();
        mock.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        foreach (var (number, closed) in mappings)
        {
            var captured = closed;
            mock.Setup(p => p.IsIssueClosedAsync(
                    It.Is<IssueIdentifier>(id => id.Value == number),
                    It.IsAny<CancellationToken>()))
                .ReturnsAsync(captured);
        }
        return mock;
    }

    // ── No dependencies ─────────────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_NullBody_ReturnsNoDependencies()
    {
        var checker = new DependencyChecker(Logger);

        var result = await checker.CheckAsync(
            "42", null, Mock.Of<IIssueProvider>(), [], CancellationToken.None);

        result.IsReady.Should().BeTrue();
        result.TotalDependencies.Should().Be(0);
        result.BlockedBy.Should().BeEmpty();
    }

    [Fact]
    public async Task CheckAsync_EmptyBody_ReturnsNoDependencies()
    {
        var checker = new DependencyChecker(Logger);

        var result = await checker.CheckAsync(
            "42", "", Mock.Of<IIssueProvider>(), [], CancellationToken.None);

        result.IsReady.Should().BeTrue();
        result.TotalDependencies.Should().Be(0);
    }

    [Fact]
    public async Task CheckAsync_BodyWithNoDependencyKeywords_ReturnsNoDependencies()
    {
        var checker = new DependencyChecker(Logger);
        const string body = "This is a standalone issue with no blockers.";

        var result = await checker.CheckAsync(
            "42", body, Mock.Of<IIssueProvider>(), [], CancellationToken.None);

        result.IsReady.Should().BeTrue();
        result.TotalDependencies.Should().Be(0);
    }

    // ── All dependencies satisfied ───────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_SingleClosedDependency_IsReady()
    {
        var checker = new DependencyChecker(Logger);
        var provider = ProviderReturning("100", isClosed: true);

        var result = await checker.CheckAsync(
            "42", "Blocked by #100", provider.Object, [], CancellationToken.None);

        result.IsReady.Should().BeTrue(because: "issue #100 is closed");
        result.BlockedBy.Should().BeEmpty();
        result.TotalDependencies.Should().Be(1);
    }

    [Fact]
    public async Task CheckAsync_MultipleClosedDependencies_IsReady()
    {
        var checker = new DependencyChecker(Logger);
        var provider = ProviderWith(("10", true), ("20", true), ("30", true));

        var result = await checker.CheckAsync(
            "99", "Blocked by #10\nDepends on #20\nRequires #30",
            provider.Object, [], CancellationToken.None);

        result.IsReady.Should().BeTrue();
        result.BlockedBy.Should().BeEmpty();
        result.TotalDependencies.Should().Be(3);
    }

    // ── Open dependencies block dispatch ────────────────────────────────────

    [Fact]
    public async Task CheckAsync_OpenDependency_IsNotReady_BlockedByPopulated()
    {
        var checker = new DependencyChecker(Logger);
        var provider = ProviderReturning("55", isClosed: false);

        var result = await checker.CheckAsync(
            "42", "Blocked by #55", provider.Object, [], CancellationToken.None);

        result.IsReady.Should().BeFalse(because: "issue #55 is still open");
        result.BlockedBy.Should().ContainSingle().Which.Should().Be(55);
        result.TotalDependencies.Should().Be(1);
    }

    [Fact]
    public async Task CheckAsync_MixedDependencies_BlockedByContainsOnlyOpenOnes()
    {
        var checker = new DependencyChecker(Logger);
        // #10 closed, #20 open, #30 closed
        var provider = ProviderWith(("10", true), ("20", false), ("30", true));

        var result = await checker.CheckAsync(
            "99", "Blocked by #10\nDepends on #20\nRequires #30",
            provider.Object, [], CancellationToken.None);

        result.IsReady.Should().BeFalse();
        result.BlockedBy.Should().ContainSingle().Which.Should().Be(20);
        result.TotalDependencies.Should().Be(3);
    }

    // ── API failure treated as unresolved ────────────────────────────────────

    [Fact]
    public async Task CheckAsync_ApiFailureForDependency_TreatsAsUnresolved_DoesNotThrow()
    {
        var checker = new DependencyChecker(Logger);
        var provider = new Mock<IIssueProvider>();
        provider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        provider
            .Setup(p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("connection refused"));

        var result = await checker.CheckAsync(
            "42", "Blocked by #77", provider.Object, [], CancellationToken.None);

        result.IsReady.Should().BeFalse(
            because: "API failure means we cannot confirm the dep is closed, so treat as unresolved");
        result.BlockedBy.Should().ContainSingle().Which.Should().Be(77);
    }

    [Fact]
    public async Task CheckAsync_ApiFailureForOneDep_OtherDepsStillChecked()
    {
        var checker = new DependencyChecker(Logger);
        var provider = new Mock<IIssueProvider>();
        provider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        // #10 throws, #20 closed
        provider
            .Setup(p => p.IsIssueClosedAsync(
                It.Is<IssueIdentifier>(id => id.Value == "10"),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("network blip"));
        provider
            .Setup(p => p.IsIssueClosedAsync(
                It.Is<IssueIdentifier>(id => id.Value == "20"),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await checker.CheckAsync(
            "99", "Blocked by #10\nDepends on #20", provider.Object, [], CancellationToken.None);

        // #10 failed → unresolved → blocks; #20 closed → OK
        result.IsReady.Should().BeFalse();
        result.BlockedBy.Should().ContainSingle().Which.Should().Be(10);
        result.TotalDependencies.Should().Be(2);
    }

    // ── Cache hit avoids redundant API call ──────────────────────────────────

    [Fact]
    public async Task CheckAsync_CacheHit_DoesNotCallProviderAgain()
    {
        var checker = new DependencyChecker(Logger);
        var provider = new Mock<IIssueProvider>();
        provider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        provider
            .Setup(p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var cache = new Dictionary<int, bool> { [100] = true };

        await checker.CheckAsync("42", "Blocked by #100", provider.Object, cache, CancellationToken.None);

        // Provider should NOT have been called — cache already had the answer
        provider.Verify(
            p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "stateCache already contained the result for issue #100");
    }

    [Fact]
    public async Task CheckAsync_PopulatesCache_SubsequentCallsForSameDepSkipApi()
    {
        var checker = new DependencyChecker(Logger);
        var provider = ProviderReturning("200", isClosed: true);
        var cache = new Dictionary<int, bool>();

        // First call — cache miss, hits API
        await checker.CheckAsync("42", "Blocked by #200", provider.Object, cache, CancellationToken.None);

        // Second call — cache should be populated
        await checker.CheckAsync("43", "Blocked by #200", provider.Object, cache, CancellationToken.None);

        provider.Verify(
            p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "second call should use cached value, not call API again");
    }

    // ── Self-reference exclusion ─────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_SelfReferencedDependency_IsExcluded()
    {
        var checker = new DependencyChecker(Logger);
        var provider = new Mock<IIssueProvider>();
        provider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        // Issue #42 references itself — should be excluded from dependency check
        var result = await checker.CheckAsync(
            "42", "Blocked by #42", provider.Object, [], CancellationToken.None);

        result.IsReady.Should().BeTrue(because: "self-reference should be ignored");
        result.TotalDependencies.Should().Be(0);

        provider.Verify(
            p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "self-reference must not trigger an API call");
    }

    // ── Cancellation propagates ──────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_CancelledToken_ThrowsOperationCancelledException()
    {
        var checker = new DependencyChecker(Logger);
        var provider = new Mock<IIssueProvider>();
        provider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        provider
            .Setup(p => p.IsIssueClosedAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());

        var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = () => checker.CheckAsync(
            "42", "Blocked by #99", provider.Object, [], cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>(
            because: "OperationCanceledException must propagate, not be swallowed by the error handler");
    }

    // ── Guard clauses ────────────────────────────────────────────────────────

    [Fact]
    public async Task CheckAsync_NullProvider_ThrowsArgumentNullException()
    {
        var checker = new DependencyChecker(Logger);

        var act = () => checker.CheckAsync(
            "42", "Blocked by #1", null!, [], CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task CheckAsync_NullCache_ThrowsArgumentNullException()
    {
        var checker = new DependencyChecker(Logger);

        var act = () => checker.CheckAsync(
            "42", "Blocked by #1", Mock.Of<IIssueProvider>(), null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
