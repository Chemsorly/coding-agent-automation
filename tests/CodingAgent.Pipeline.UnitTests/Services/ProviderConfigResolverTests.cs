using AwesomeAssertions;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for ProviderConfigResolver.ResolveAsync.
/// Covers: cache hit, cache miss → DB found, cache miss → DB not found (required/optional),
/// cache invalidation on backfill.
/// </summary>
public sealed class ProviderConfigResolverTests
{
    private readonly Mock<IConfigurationStore> _store = new();
    private readonly Mock<ILogger> _logger = new();

    private static ProviderConfig MakeConfig(string id = "cfg-1") =>
        new() { Id = id, Kind = ProviderKind.Issue, DisplayName = "T", ProviderType = "GitHub" };

    // ── Cache hit ─────────────────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_WhenFoundInCache_ReturnsCached()
    {
        var config = MakeConfig("cfg-1");
        var result = await ProviderConfigResolver.ResolveAsync(
            _store.Object, "cfg-1", ProviderKind.Issue,
            [config], required: true, _logger.Object, CancellationToken.None);

        result.Should().BeSameAs(config);
        _store.Verify(s => s.GetProviderConfigByIdAsync(
            It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // ── Cache miss → DB found ─────────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_WhenNotInCache_FallsBackToDb()
    {
        var config = MakeConfig("cfg-1");
        _store.Setup(s => s.GetProviderConfigByIdAsync("cfg-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var result = await ProviderConfigResolver.ResolveAsync(
            _store.Object, "cfg-1", ProviderKind.Issue,
            [], required: true, _logger.Object, CancellationToken.None);

        result.Should().BeSameAs(config);
        _store.Verify(s => s.GetProviderConfigByIdAsync(
            "cfg-1", ProviderKind.Issue, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_WhenDbBackfillSucceeds_InvalidatesCache()
    {
        var config = MakeConfig("cfg-1");
        _store.Setup(s => s.GetProviderConfigByIdAsync("cfg-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        await ProviderConfigResolver.ResolveAsync(
            _store.Object, "cfg-1", ProviderKind.Issue,
            [], required: false, _logger.Object, CancellationToken.None);

        _store.Verify(s => s.InvalidateCaches(), Times.Once);
    }

    // ── Cache miss → DB not found, required ───────────────────────────────

    [Fact]
    public async Task ResolveAsync_WhenNotFoundAndRequired_Throws()
    {
        _store.Setup(s => s.GetProviderConfigByIdAsync(
            It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var act = () => ProviderConfigResolver.ResolveAsync(
            _store.Object, "missing", ProviderKind.Issue,
            [], required: true, _logger.Object, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*missing*");
    }

    // ── Cache miss → DB not found, optional ──────────────────────────────

    [Fact]
    public async Task ResolveAsync_WhenNotFoundAndOptional_ReturnsNull()
    {
        _store.Setup(s => s.GetProviderConfigByIdAsync(
            It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var result = await ProviderConfigResolver.ResolveAsync(
            _store.Object, "missing", ProviderKind.Issue,
            [], required: false, _logger.Object, CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task ResolveAsync_WhenNotFoundAndOptional_DoesNotInvalidateCache()
    {
        _store.Setup(s => s.GetProviderConfigByIdAsync(
            It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        await ProviderConfigResolver.ResolveAsync(
            _store.Object, "missing", ProviderKind.Issue,
            [], required: false, _logger.Object, CancellationToken.None);

        _store.Verify(s => s.InvalidateCaches(), Times.Never);
    }

    // ── Second item in cache list ─────────────────────────────────────────

    [Fact]
    public async Task ResolveAsync_ReturnsCorrectItemFromMultipleInCache()
    {
        var cfg1 = MakeConfig("cfg-1");
        var cfg2 = MakeConfig("cfg-2");

        var result = await ProviderConfigResolver.ResolveAsync(
            _store.Object, "cfg-2", ProviderKind.Issue,
            [cfg1, cfg2], required: true, _logger.Object, CancellationToken.None);

        result.Should().BeSameAs(cfg2);
    }

    // ── TryResolveAsync (fetcher overload) ────────────────────────────────

    [Fact]
    public async Task TryResolveAsync_WhenFetcherReturnsConfig_ReturnsConfig()
    {
        var config = MakeConfig("cfg-1");

        var result = await ProviderConfigResolver.TryResolveAsync(
            () => Task.FromResult<ProviderConfig?>(config),
            "cfg-1", ProviderKind.Issue, _logger.Object);

        result.Should().BeSameAs(config);
        _logger.Verify(l => l.Warning(
            It.IsAny<string>(),
            It.Is<string>(id => id == "cfg-1"),
            It.IsAny<ProviderKind>()), Times.Never);
    }

    [Fact]
    public async Task TryResolveAsync_WhenFetcherReturnsNull_ReturnsNull()
    {
        // TODO [WARNING]: this test does not verify that the fetcher lambda was actually invoked.
        // If TryResolveAsync short-circuited to null without calling the fetcher, the assertion
        // would still pass. Use a Mock<Func<Task<ProviderConfig?>>> or an invocation flag to
        // explicitly assert the fetcher was called.
        var result = await ProviderConfigResolver.TryResolveAsync(
            () => Task.FromResult<ProviderConfig?>(null),
            "missing", ProviderKind.Issue, _logger.Object);

        result.Should().BeNull();
    }

    [Fact]
    public async Task TryResolveAsync_WhenFetcherReturnsNull_LogsWarning()
    {
        await ProviderConfigResolver.TryResolveAsync(
            () => Task.FromResult<ProviderConfig?>(null),
            "missing-cfg", ProviderKind.Repository, _logger.Object);

        _logger.Verify(l => l.Warning(
            It.IsAny<string>(),
            It.Is<string>(id => id == "missing-cfg"),
            It.Is<ProviderKind>(k => k == ProviderKind.Repository)), Times.Once);
    }

    // ── ResolveRequiredAsync (fetcher overload) ───────────────────────────

    [Fact]
    public async Task ResolveRequiredAsync_WhenFetcherReturnsConfig_ReturnsConfig()
    {
        // TODO [WARNING]: This test does not assert that no Error or Warning log was emitted on
        // the happy path. The contract of ResolveRequiredAsync is that the logger is silent on
        // success; without that assertion, a regression where the method logs an error before
        // returning (e.g., a stray logger.Error call added above the null check) would go
        // undetected. Add _logger.Verify(l => l.Error(...), Times.Never) and
        // _logger.Verify(l => l.Warning(...), Times.Never) to mirror the TryResolveAsync
        // happy-path test at line ~143.
        var config = MakeConfig("cfg-1");

        var result = await ProviderConfigResolver.ResolveRequiredAsync(
            () => Task.FromResult<ProviderConfig?>(config),
            "cfg-1", ProviderKind.Issue, _logger.Object);

        result.Should().BeSameAs(config);
    }

    [Fact]
    public async Task ResolveRequiredAsync_WhenFetcherReturnsNull_ThrowsInvalidOperationException()
    {
        var act = () => ProviderConfigResolver.ResolveRequiredAsync(
            () => Task.FromResult<ProviderConfig?>(null),
            "missing-cfg", ProviderKind.Issue, _logger.Object);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*missing-cfg*");
    }

    [Fact]
    public async Task ResolveRequiredAsync_WhenFetcherReturnsNull_LogsError()
    {
        try
        {
            await ProviderConfigResolver.ResolveRequiredAsync(
                () => Task.FromResult<ProviderConfig?>(null),
                "missing-cfg", ProviderKind.Repository, _logger.Object);
        }
        catch (InvalidOperationException) { }

        _logger.Verify(l => l.Error(
            It.IsAny<string>(),
            It.Is<string>(id => id == "missing-cfg"),
            It.Is<ProviderKind>(k => k == ProviderKind.Repository)), Times.Once);
    }
}
