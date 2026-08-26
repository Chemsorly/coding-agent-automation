using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for LabelService.
/// Covers: SwapLabelAsync (Issue/PR/Unknown/not-found config), SwapLabelStrictAsync (throws on failure),
/// EnsureAgentLabelsAsync (Issue/PR/not-found/exception), constructor guards.
/// </summary>
public sealed class LabelServiceTests
{
    private readonly Mock<IProviderConfigStore> _configStore = new();
    private readonly Mock<IProviderFactory> _providerFactory = new();
    private readonly Mock<ILogger> _logger = new();
    private readonly LabelService _sut;

    public LabelServiceTests()
    {
        _sut = new LabelService(_configStore.Object, _providerFactory.Object, _logger.Object);
    }

    private static ProviderConfig MakeConfig(string id = "cfg-1", ProviderKind kind = ProviderKind.Issue) =>
        new() { Id = id, Kind = kind, DisplayName = "T", ProviderType = "GitHub" };

    private static PipelineRun MakeRun() =>
        PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "r1", IssueIdentifier = "GH-1", IssueTitle = "T",
            IssueProviderConfigId = "github", RepoProviderConfigId = "repo",
            AgentId = "a1", AgentProviderConfigId = "kiro",
            InitiatedBy = "test", StartedAt = DateTimeOffset.UtcNow
        });

    // ── Constructor guards ────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullConfigStore_Throws()
    {
        var act = () => new LabelService(null!, _providerFactory.Object, _logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullProviderFactory_Throws()
    {
        var act = () => new LabelService(_configStore.Object, null!, _logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── SwapLabelAsync — issue path ───────────────────────────────────────

    [Fact]
    public async Task SwapLabelAsync_IssuePath_CallsIssueProvider()
    {
        var config = MakeConfig("github", ProviderKind.Issue);
        _configStore.Setup(s => s.GetProviderConfigByIdAsync("github", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.RemoveLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockProvider.Setup(p => p.AddLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _providerFactory.Setup(f => f.CreateIssueProvider(config)).Returns(mockProvider.Object);

        await _sut.SwapLabelAsync(
            new ProviderConfigId("github"), new IssueIdentifier("GH-1"),
            AgentLabels.Done, LabelTargetKind.Issue, CancellationToken.None);

        mockProvider.Verify(p => p.AddLabelAsync(new IssueIdentifier("GH-1"), AgentLabels.Done, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SwapLabelAsync_WhenIssueConfigNotFound_DoesNotThrow()
    {
        _configStore.Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var act = () => _sut.SwapLabelAsync(
            new ProviderConfigId("github"), new IssueIdentifier("GH-1"),
            AgentLabels.Done, LabelTargetKind.Issue, CancellationToken.None);

        await act.Should().NotThrowAsync(); // failure swallowed
    }

    [Fact]
    public async Task SwapLabelAsync_WhenProviderThrows_DoesNotPropagate()
    {
        var config = MakeConfig("github", ProviderKind.Issue);
        _configStore.Setup(s => s.GetProviderConfigByIdAsync("github", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.RemoveLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider down"));
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _providerFactory.Setup(f => f.CreateIssueProvider(config)).Returns(mockProvider.Object);

        var act = () => _sut.SwapLabelAsync(
            new ProviderConfigId("github"), new IssueIdentifier("GH-1"),
            AgentLabels.Done, LabelTargetKind.Issue, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── SwapLabelAsync — PR path ──────────────────────────────────────────

    [Fact]
    public async Task SwapLabelAsync_PRPath_CallsRepoProvider()
    {
        var config = MakeConfig("github-repo", ProviderKind.Repository);
        _configStore.Setup(s => s.GetProviderConfigByIdAsync("github-repo", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var mockProvider = new Mock<IRepositoryProvider>();
        mockProvider.Setup(p => p.RemovePrLabelAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockProvider.Setup(p => p.AddPrLabelAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _providerFactory.Setup(f => f.CreateRepositoryProvider(config)).Returns(mockProvider.Object);

        await _sut.SwapLabelAsync(
            new ProviderConfigId("github-repo"), new IssueIdentifier("42"),
            AgentLabels.Done, LabelTargetKind.PullRequest, CancellationToken.None);

        mockProvider.Verify(p => p.AddPrLabelAsync(42, AgentLabels.Done, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SwapLabelAsync_PRPath_NonNumericIdentifier_DoesNotThrow()
    {
        var config = MakeConfig("github-repo", ProviderKind.Repository);
        _configStore.Setup(s => s.GetProviderConfigByIdAsync("github-repo", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var mockProvider = new Mock<IRepositoryProvider>();
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _providerFactory.Setup(f => f.CreateRepositoryProvider(config)).Returns(mockProvider.Object);

        // Non-numeric PR identifier — should log warning and return without throwing
        var act = () => _sut.SwapLabelAsync(
            new ProviderConfigId("github-repo"), new IssueIdentifier("not-a-number"),
            AgentLabels.Done, LabelTargetKind.PullRequest, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── SwapLabelAsync — Unknown target kind ──────────────────────────────

    [Fact]
    public async Task SwapLabelAsync_UnknownTargetKind_DoesNotThrow()
    {
        var act = () => _sut.SwapLabelAsync(
            new ProviderConfigId("cfg"), new IssueIdentifier("GH-1"),
            "label", (LabelTargetKind)999, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── SwapLabelAsync with expectedCurrentLabel ──────────────────────────

    [Fact]
    public async Task SwapLabelAsync_WithExpectedLabel_ValidTransition_Proceeds()
    {
        _configStore.Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        // Valid transition: Next → InProgress
        var act = () => _sut.SwapLabelAsync(
            new ProviderConfigId("cfg"), new IssueIdentifier("GH-1"),
            AgentLabels.InProgress, LabelTargetKind.Issue,
            expectedCurrentLabel: AgentLabels.Next, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── SwapLabelStrictAsync ──────────────────────────────────────────────

    [Fact]
    public async Task SwapLabelStrictAsync_WhenProviderThrows_Propagates()
    {
        var config = MakeConfig("github", ProviderKind.Issue);
        _configStore.Setup(s => s.GetProviderConfigByIdAsync("github", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.RemoveLabelAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("strict failure"));
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _providerFactory.Setup(f => f.CreateIssueProvider(config)).Returns(mockProvider.Object);

        var act = () => _sut.SwapLabelStrictAsync(
            new ProviderConfigId("github"), new IssueIdentifier("GH-1"),
            AgentLabels.Done, LabelTargetKind.Issue, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task SwapLabelStrictAsync_UnknownTargetKind_DoesNotThrow()
    {
        var act = () => _sut.SwapLabelStrictAsync(
            new ProviderConfigId("cfg"), new IssueIdentifier("GH-1"),
            "label", (LabelTargetKind)999, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    // ── EnsureAgentLabelsAsync ────────────────────────────────────────────

    [Fact]
    public async Task EnsureAgentLabelsAsync_IssueTargetNotFound_ReturnsFalse()
    {
        _configStore.Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var result = await _sut.EnsureAgentLabelsAsync(
            new ProviderConfigId("github"), LabelTargetKind.Issue, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task EnsureAgentLabelsAsync_IssueTargetFound_ReturnsProviderResult()
    {
        var config = MakeConfig("github", ProviderKind.Issue);
        _configStore.Setup(s => s.GetProviderConfigByIdAsync("github", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.EnsureAgentLabelsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _providerFactory.Setup(f => f.CreateIssueProvider(config)).Returns(mockProvider.Object);

        var result = await _sut.EnsureAgentLabelsAsync(
            new ProviderConfigId("github"), LabelTargetKind.Issue, CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task EnsureAgentLabelsAsync_PRTargetNotFound_ReturnsFalse()
    {
        _configStore.Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var result = await _sut.EnsureAgentLabelsAsync(
            new ProviderConfigId("github-repo"), LabelTargetKind.PullRequest, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task EnsureAgentLabelsAsync_PRTargetFound_ReturnsProviderResult()
    {
        var config = MakeConfig("github-repo", ProviderKind.Repository);
        _configStore.Setup(s => s.GetProviderConfigByIdAsync("github-repo", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var mockProvider = new Mock<IRepositoryProvider>();
        mockProvider.Setup(p => p.EnsureAgentLabelsForPullRequestsAsync(It.IsAny<CancellationToken>())).ReturnsAsync(true);
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _providerFactory.Setup(f => f.CreateRepositoryProvider(config)).Returns(mockProvider.Object);

        var result = await _sut.EnsureAgentLabelsAsync(
            new ProviderConfigId("github-repo"), LabelTargetKind.PullRequest, CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task EnsureAgentLabelsAsync_UnknownTargetKind_ReturnsFalse()
    {
        var result = await _sut.EnsureAgentLabelsAsync(
            new ProviderConfigId("cfg"), (LabelTargetKind)999, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task EnsureAgentLabelsAsync_WhenExceptionThrown_ReturnsFalse()
    {
        _configStore.Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("store error"));

        var result = await _sut.EnsureAgentLabelsAsync(
            new ProviderConfigId("github"), LabelTargetKind.Issue, CancellationToken.None);

        result.Should().BeFalse();
    }
}
