using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Unit tests for <see cref="IssueProviderLabelSwapper"/>.
/// </summary>
public class IssueProviderLabelSwapperTests
{
    private readonly Mock<IProviderConfigStore> _mockConfigStore = new();
    private readonly Mock<IProviderFactory> _mockProviderFactory = new();
    private readonly Mock<IIssueProvider> _mockIssueProvider = new();
    private readonly Mock<ILogger> _mockLogger = new();

    private IssueProviderLabelSwapper CreateSwapper() =>
        new(_mockConfigStore.Object, _mockProviderFactory.Object, _mockLogger.Object);

    [Fact]
    public async Task SwapLabelAsync_HappyPath_RemovesAllLabelsAndAddsNew()
    {
        var config = new ProviderConfig { Id = "cfg-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { config });
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(config)).Returns(_mockIssueProvider.Object);
        _mockIssueProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var swapper = CreateSwapper();
        await swapper.SwapLabelAsync("cfg-1", "org/repo#42", AgentLabels.Done, CancellationToken.None);

        foreach (var label in AgentLabels.All)
        {
            _mockIssueProvider.Verify(
                p => p.RemoveLabelAsync("org/repo#42", label, CancellationToken.None), Times.Once);
        }
        _mockIssueProvider.Verify(
            p => p.AddLabelAsync("org/repo#42", AgentLabels.Done, CancellationToken.None), Times.Once);
    }

    [Fact]
    public async Task SwapLabelAsync_ConfigNotFound_LogsWarningAndReturns()
    {
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        var swapper = CreateSwapper();
        await swapper.SwapLabelAsync("missing-cfg", "org/repo#1", AgentLabels.Error, CancellationToken.None);

        _mockProviderFactory.Verify(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()), Times.Never);
    }

    [Fact]
    public async Task SwapLabelAsync_EmptyNewLabel_SkipsAddLabel()
    {
        var config = new ProviderConfig { Id = "cfg-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { config });
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(config)).Returns(_mockIssueProvider.Object);
        _mockIssueProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var swapper = CreateSwapper();
        await swapper.SwapLabelAsync("cfg-1", "org/repo#42", "", CancellationToken.None);

        _mockIssueProvider.Verify(
            p => p.RemoveLabelAsync("org/repo#42", It.IsAny<string>(), CancellationToken.None),
            Times.Exactly(AgentLabels.All.Count));
        _mockIssueProvider.Verify(
            p => p.AddLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task SwapLabelAsync_ProviderThrows_CatchesAndDoesNotPropagate()
    {
        var config = new ProviderConfig { Id = "cfg-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { config });
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(config)).Returns(_mockIssueProvider.Object);
        _mockIssueProvider.Setup(p => p.RemoveLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API unreachable"));
        _mockIssueProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var swapper = CreateSwapper();

        // Should not throw
        await swapper.SwapLabelAsync("cfg-1", "org/repo#42", AgentLabels.Error, CancellationToken.None);
    }
}
