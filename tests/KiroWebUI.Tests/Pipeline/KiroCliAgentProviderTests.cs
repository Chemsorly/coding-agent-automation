using FluentAssertions;
using Moq;
using KiroCliLib.Core;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Providers;

namespace KiroWebUI.Tests.Pipeline;

public class KiroCliAgentProviderTests
{
    private readonly Mock<IKiroCliOrchestrator> _mockOrchestrator;
    private readonly KiroCliAgentProvider _provider;

    public KiroCliAgentProviderTests()
    {
        _mockOrchestrator = new Mock<IKiroCliOrchestrator>();
        _provider = new KiroCliAgentProvider(_mockOrchestrator.Object);
    }

    [Fact]
    public async Task ExecuteAsync_CallsOrchestratorWithUseResumeFalse()
    {
        _mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                "test prompt", "/workspace", false,
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(0);

        var request = new AgentRequest { Prompt = "test prompt", WorkspacePath = "/workspace" };
        var result = await _provider.ExecuteAsync(request, CancellationToken.None);

        result.ExitCode.Should().Be(0);
        result.Success.Should().BeTrue();
        _mockOrchestrator.Verify(o => o.ExecutePromptAsync(
            "test prompt", "/workspace", false,
            It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteWithResumeAsync_CallsOrchestratorWithUseResumeTrue()
    {
        _mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                "follow up", "/workspace", true,
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(0);

        var result = await _provider.ExecuteWithResumeAsync(
            "follow up", "/workspace", TimeSpan.FromMinutes(5), CancellationToken.None);

        result.ExitCode.Should().Be(0);
        _mockOrchestrator.Verify(o => o.ExecutePromptAsync(
            "follow up", "/workspace", true,
            It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_CapturesOutputLines()
    {
        _mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .Callback<string, string, bool, CancellationToken, Action<string>?>(
                (_, _, _, _, onOutput) =>
                {
                    onOutput?.Invoke("line 1");
                    onOutput?.Invoke("line 2");
                    onOutput?.Invoke("line 3");
                })
            .ReturnsAsync(0);

        var externalLines = new List<string>();
        var request = new AgentRequest { Prompt = "test", WorkspacePath = "/ws" };
        var result = await _provider.ExecuteAsync(request, CancellationToken.None, line => externalLines.Add(line));

        result.OutputLines.Should().BeEquivalentTo(["line 1", "line 2", "line 3"]);
        externalLines.Should().BeEquivalentTo(["line 1", "line 2", "line 3"]);
    }

    [Fact]
    public async Task ExecuteAsync_NonZeroExitCode_ReportsCorrectly()
    {
        _mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(1);

        var request = new AgentRequest { Prompt = "fail", WorkspacePath = "/ws" };
        var result = await _provider.ExecuteAsync(request, CancellationToken.None);

        result.ExitCode.Should().Be(1);
        result.Success.Should().BeFalse();
    }
}
