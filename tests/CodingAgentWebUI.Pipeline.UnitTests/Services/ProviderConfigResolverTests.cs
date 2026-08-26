using AwesomeAssertions;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

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
}
