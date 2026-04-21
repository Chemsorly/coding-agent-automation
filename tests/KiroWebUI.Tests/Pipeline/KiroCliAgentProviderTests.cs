using AwesomeAssertions;
using Moq;
using KiroCliLib.Core;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Pipeline.Providers;

namespace KiroWebUI.Tests.Pipeline;

public class KiroCliAgentProviderTests
{
    private readonly Mock<IKiroCliOrchestrator> _mockOrchestrator;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly KiroCliAgentProvider _provider;

    public KiroCliAgentProviderTests()
    {
        _mockOrchestrator = new Mock<IKiroCliOrchestrator>();
        _mockLogger = new Mock<Serilog.ILogger>();
        _provider = new KiroCliAgentProvider(_mockOrchestrator.Object, _mockLogger.Object);
    }

    // --- EnsureSessionAsync tests ---

    [Fact]
    public async Task EnsureSessionAsync_FirstCall_SendsWarmUpPrompt()
    {
        _mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                KiroCliAgentProvider.WarmUpPrompt,
                It.IsAny<string>(), false,
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(0);

        await _provider.EnsureSessionAsync("/workspace", CancellationToken.None);

        _mockOrchestrator.Verify(o => o.ExecutePromptAsync(
            KiroCliAgentProvider.WarmUpPrompt,
            "/workspace", false,
            It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Once);
    }

    [Fact]
    public async Task EnsureSessionAsync_SubsequentCallSamePath_NoOps()
    {
        _mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(0);

        await _provider.EnsureSessionAsync("/workspace", CancellationToken.None);
        await _provider.EnsureSessionAsync("/workspace", CancellationToken.None);

        _mockOrchestrator.Verify(o => o.ExecutePromptAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Once);
    }

    [Fact]
    public async Task EnsureSessionAsync_DifferentPaths_CallsForEach()
    {
        _mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(0);

        await _provider.EnsureSessionAsync("/workspace-a", CancellationToken.None);
        await _provider.EnsureSessionAsync("/workspace-b", CancellationToken.None);

        _mockOrchestrator.Verify(o => o.ExecutePromptAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Exactly(2));
    }

    [Fact]
    public async Task EnsureSessionAsync_Failure_LogsWarningAndDoesNotThrow()
    {
        _mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ThrowsAsync(new InvalidOperationException("agent crashed"));

        var act = () => _provider.EnsureSessionAsync("/workspace", CancellationToken.None);

        await act.Should().NotThrowAsync();
        _mockLogger.Verify(l => l.Warning(
            It.IsAny<Exception>(),
            It.IsAny<string>(),
            It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task EnsureSessionAsync_Failure_DoesNotMarkSessionEstablished()
    {
        _mockOrchestrator
            .SetupSequence(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ThrowsAsync(new InvalidOperationException("agent crashed"))
            .ReturnsAsync(0);

        // First call fails
        await _provider.EnsureSessionAsync("/workspace", CancellationToken.None);

        // Second call should retry (not no-op) because session was not established
        await _provider.EnsureSessionAsync("/workspace", CancellationToken.None);

        _mockOrchestrator.Verify(o => o.ExecutePromptAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()), Times.Exactly(2));
    }

    [Fact]
    public async Task EnsureSessionAsync_OperationCanceled_Rethrows()
    {
        _mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => _provider.EnsureSessionAsync("/workspace", CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // --- ExecuteAsync tests ---

    [Fact]
    public async Task ExecuteAsync_DefaultUseResume_CallsOrchestratorWithUseResumeFalse()
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
    public async Task ExecuteAsync_UseResumeTrue_CallsOrchestratorWithUseResumeTrue()
    {
        _mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                "follow up", "/workspace", true,
                It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
            .ReturnsAsync(0);

        var request = new AgentRequest { Prompt = "follow up", WorkspacePath = "/workspace", UseResume = true };
        var result = await _provider.ExecuteAsync(request, CancellationToken.None);

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

    // --- Model configuration tests ---

    [Fact]
    public void Model_WhenNotProvided_ReturnsNull()
    {
        var provider = new KiroCliAgentProvider(_mockOrchestrator.Object, _mockLogger.Object);
        provider.Model.Should().BeNull();
    }

    [Fact]
    public void Model_WhenProvided_ReturnsConfiguredValue()
    {
        var provider = new KiroCliAgentProvider(_mockOrchestrator.Object, _mockLogger.Object, model: "claude-sonnet-4.6");
        provider.Model.Should().Be("claude-sonnet-4.6");
    }

    [Fact]
    public async Task ApplyModelSettingAsync_WhenModelIsNull_DoesNothing()
    {
        var provider = new KiroCliAgentProvider(_mockOrchestrator.Object, _mockLogger.Object, model: null);
        // Should not throw — no process started
        await provider.ApplyModelSettingAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ApplyModelSettingAsync_WhenModelIsAuto_DoesNothing()
    {
        var provider = new KiroCliAgentProvider(_mockOrchestrator.Object, _mockLogger.Object, model: "auto");
        // Should not throw — no process started
        await provider.ApplyModelSettingAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ApplyModelSettingAsync_WhenModelIsAutoUpperCase_DoesNothing()
    {
        var provider = new KiroCliAgentProvider(_mockOrchestrator.Object, _mockLogger.Object, model: "Auto");
        await provider.ApplyModelSettingAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ApplyModelSettingAsync_WhenModelContainsInvalidChars_RejectsAndLogs()
    {
        var provider = new KiroCliAgentProvider(_mockOrchestrator.Object, _mockLogger.Object, model: "foo\" && rm -rf /");
        await provider.ApplyModelSettingAsync(CancellationToken.None);

        _mockLogger.Verify(l => l.Warning(
            "Invalid model name rejected: {Model}",
            "foo\" && rm -rf /"), Times.Once);
    }

    [Fact]
    public async Task ApplyModelSettingAsync_WhenModelContainsSpaces_RejectsAndLogs()
    {
        var provider = new KiroCliAgentProvider(_mockOrchestrator.Object, _mockLogger.Object, model: "model with spaces");
        await provider.ApplyModelSettingAsync(CancellationToken.None);

        _mockLogger.Verify(l => l.Warning(
            "Invalid model name rejected: {Model}",
            "model with spaces"), Times.Once);
    }
}
