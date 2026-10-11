using AwesomeAssertions;
using Moq;
using KiroCliLib.Core;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Agent;
using CodingAgent.Agent.KiroCli;
using System.Diagnostics;

namespace CodingAgent.Agent.UnitTests;

public class KiroCliAgentProviderTests
{
    private readonly Mock<IKiroCliOrchestrator> _mockOrchestrator;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly Mock<IProcessStarter> _mockProcessStarter;
    private readonly KiroCliAgentProvider _provider;

    public KiroCliAgentProviderTests()
    {
        _mockOrchestrator = new Mock<IKiroCliOrchestrator>();
        _mockLogger = new Mock<Serilog.ILogger>();
        _mockProcessStarter = new Mock<IProcessStarter>();
        // Mock the process starter to return null (simulates "process didn't start" gracefully handled)
        _mockProcessStarter.Setup(p => p.Start(It.IsAny<ProcessStartInfo>())).Returns((Process?)null);
        _provider = new KiroCliAgentProvider(
            _mockOrchestrator.Object, _mockLogger.Object, null,
            "/usr/bin/fake-kiro-cli", AgentEffortLevel.High, _mockProcessStarter.Object);
    }

    // --- EnsureSessionAsync tests ---

    [Fact]
    public async Task EnsureSessionAsync_FirstCall_SendsWarmUpPrompt()
    {
        _mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                KiroCliAgentProvider.WarmUpPrompt,
                It.IsAny<string>(), false,
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .ReturnsAsync(0);

        await _provider.EnsureSessionAsync("/workspace", CancellationToken.None);

        _mockOrchestrator.Verify(o => o.ExecutePromptAsync(
            KiroCliAgentProvider.WarmUpPrompt,
            "/workspace", false,
            It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task EnsureSessionAsync_SubsequentCallSamePath_NoOps()
    {
        _mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .ReturnsAsync(0);

        await _provider.EnsureSessionAsync("/workspace", CancellationToken.None);
        await _provider.EnsureSessionAsync("/workspace", CancellationToken.None);

        _mockOrchestrator.Verify(o => o.ExecutePromptAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()), Times.Once);
    }

    [Fact]
    public async Task EnsureSessionAsync_DifferentPaths_CallsForEach()
    {
        _mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .ReturnsAsync(0);

        await _provider.EnsureSessionAsync("/workspace-a", CancellationToken.None);
        await _provider.EnsureSessionAsync("/workspace-b", CancellationToken.None);

        _mockOrchestrator.Verify(o => o.ExecutePromptAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()), Times.Exactly(2));
    }

    [Fact]
    public async Task EnsureSessionAsync_Failure_LogsWarningAndDoesNotThrow()
    {
        _mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
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
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .ThrowsAsync(new InvalidOperationException("agent crashed"))
            .ReturnsAsync(0);

        await _provider.EnsureSessionAsync("/workspace", CancellationToken.None);
        await _provider.EnsureSessionAsync("/workspace", CancellationToken.None);

        _mockOrchestrator.Verify(o => o.ExecutePromptAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()), Times.Exactly(2));
    }

    [Fact]
    public async Task EnsureSessionAsync_OperationCanceled_Rethrows()
    {
        _mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .ThrowsAsync(new OperationCanceledException());

        var act = () => _provider.EnsureSessionAsync("/workspace", CancellationToken.None);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // --- ExecuteAsync tests ---

    [Fact]
    public async Task ExecuteAsync_DefaultUseResume_UsesEphemeralOrchestrator()
    {
        // When UseResume=false (default), an ephemeral orchestrator is used for parallel safety.
        // The shared orchestrator should NOT be called.
        // The ephemeral orchestrator may fail (no kiro-cli installed in test env) — that's fine,
        // we're only verifying the routing decision (shared mock must NOT be invoked).
        var tempWorkspace = Path.Combine(Path.GetTempPath(), $"kiro-ephemeral-test-{Guid.NewGuid():N}");
        try
        {
            var request = new AgentRequest { Prompt = "test prompt", WorkspacePath = tempWorkspace };
            try
            {
                await _provider.ExecuteAsync(request, CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
                // Expected in CI: ephemeral orchestrator can't find kiro-cli binary
            }

            // Ephemeral orchestrator runs independently — shared mock not invoked
            _mockOrchestrator.Verify(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()), Times.Never);
        }
        finally
        {
            try { if (Directory.Exists(tempWorkspace)) Directory.Delete(tempWorkspace, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ExecuteAsync_UseResumeWithoutAMainSession_StartsAFreshSessionOnTheSharedOrchestrator()
    {
        _mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .ReturnsAsync(0);

        var request = new AgentRequest { Prompt = "follow up", WorkspacePath = "/workspace", UseResume = true };
        var result = await _provider.ExecuteAsync(request, CancellationToken.None);

        result.ExitCode.Should().Be(0);
        _mockOrchestrator.Verify(o => o.ExecutePromptAsync(
            "follow up", "/workspace", false,
            It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), null), Times.Once);
    }

    [Fact]
    public async Task EnsureSessionAsync_NonZeroWarmUpExit_DoesNotMarkSessionEstablished()
    {
        _mockOrchestrator
            .SetupSequence(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .ReturnsAsync(1)
            .ReturnsAsync(0);

        await _provider.EnsureSessionAsync("/workspace", CancellationToken.None);
        await _provider.EnsureSessionAsync("/workspace", CancellationToken.None);

        _mockOrchestrator.Verify(o => o.ExecutePromptAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
            It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()), Times.Exactly(2));
    }

    [Fact]
    public async Task IsolatedCall_IsCoveredByHealthAndKill_WhileItRuns()
    {
        var ephemeral = new Mock<IKiroCliOrchestrator>();
        var started = new TaskCompletionSource();
        var lastOutput = new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        ephemeral.Setup(o => o.IsExecuting).Returns(true);
        ephemeral.Setup(o => o.LastOutputTime).Returns(lastOutput);
        ephemeral.Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .Returns<string, string, bool, CancellationToken, Func<string, Task>?, string?, IReadOnlyDictionary<string, string>?>(
                async (_, _, _, ct, _, _, _) =>
                {
                    started.SetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                    return 0;
                });
        var provider = new KiroCliAgentProvider(
            _mockOrchestrator.Object, _mockLogger.Object, null, "/usr/bin/fake-kiro-cli", AgentEffortLevel.High,
            _mockProcessStarter.Object) { CreateEphemeralOrchestratorWith = _ => ephemeral.Object };
        using var cts = new CancellationTokenSource();

        var run = provider.ExecuteAsync(
            new AgentRequest { Prompt = "review", WorkspacePath = "/workspace", Timeout = TimeSpan.FromMinutes(1) }, cts.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10));

        provider.GetHealthStatus().LastOutputTime.Should().Be(lastOutput);
        await provider.KillAsync();
        ephemeral.Verify(o => o.Kill(), Times.Once);

        await cts.CancelAsync();
        await run.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetHealthStatus_ParallelCalls_ReportsALiveProcessOverOneThatJustExited()
    {
        // The finishing reviewer still counts as executing for a moment, with the newest output.
        var alive = HangingEphemeral(alive: true, lastOutput: new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc), out var aliveStarted);
        var exiting = HangingEphemeral(alive: false, lastOutput: new DateTime(2026, 10, 10, 12, 5, 0, DateTimeKind.Utc), out var exitingStarted);
        var ephemerals = new Queue<IKiroCliOrchestrator>([alive.Object, exiting.Object]);
        var provider = new KiroCliAgentProvider(
            _mockOrchestrator.Object, _mockLogger.Object, null, "/usr/bin/fake-kiro-cli", AgentEffortLevel.High,
            _mockProcessStarter.Object) { CreateEphemeralOrchestratorWith = _ => ephemerals.Dequeue() };
        using var cts = new CancellationTokenSource();
        var review = new AgentRequest { Prompt = "review", WorkspacePath = "/workspace", Timeout = TimeSpan.FromMinutes(1) };

        var runs = new[] { provider.ExecuteAsync(review, cts.Token), provider.ExecuteAsync(review, cts.Token) };
        await Task.WhenAll(aliveStarted, exitingStarted).WaitAsync(TimeSpan.FromSeconds(10));

        var health = provider.GetHealthStatus();
        health.IsProcessAlive.Should().BeTrue();
        health.LastOutputTime.Should().Be(new DateTime(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc));

        await cts.CancelAsync();
        foreach (var run in runs)
            await run.Invoking(t => t).Should().ThrowAsync<OperationCanceledException>();
    }

    private static Mock<IKiroCliOrchestrator> HangingEphemeral(bool alive, DateTime lastOutput, out Task started)
    {
        var orchestrator = new Mock<IKiroCliOrchestrator>();
        var startedSource = new TaskCompletionSource();
        orchestrator.Setup(o => o.IsExecuting).Returns(true);
        orchestrator.Setup(o => o.IsActiveProcessAlive).Returns(alive);
        orchestrator.Setup(o => o.LastOutputTime).Returns(lastOutput);
        orchestrator.Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .Returns<string, string, bool, CancellationToken, Func<string, Task>?, string?, IReadOnlyDictionary<string, string>?>(
                async (_, _, _, ct, _, _, _) =>
                {
                    startedSource.TrySetResult();
                    await Task.Delay(Timeout.Infinite, ct);
                    return 0;
                });
        started = startedSource.Task;
        return orchestrator;
    }

    [Fact]
    public async Task ExecuteAsync_WithResumeSessionId_PassesToOrchestrator()
    {
        _mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .ReturnsAsync(0);

        var request = new AgentRequest { Prompt = "fix", WorkspacePath = "/workspace", UseResume = false, ResumeSessionId = "session-abc" };
        await _provider.ExecuteAsync(request, CancellationToken.None);

        _mockOrchestrator.Verify(o => o.ExecutePromptAsync(
            "fix", "/workspace", false,
            It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), "session-abc"), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_CapturesOutputLines()
    {
        _mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .Callback<string, string, bool, CancellationToken, Func<string, Task>?, string?, IReadOnlyDictionary<string, string>?>(
                (_, _, _, _, onOutput, _, _) =>
                {
                    onOutput?.Invoke("line 1");
                    onOutput?.Invoke("line 2");
                    onOutput?.Invoke("line 3");
                })
            .ReturnsAsync(0);

        var externalLines = new List<string>();
        var request = new AgentRequest { Prompt = "test", WorkspacePath = "/ws", UseResume = true };
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
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .ReturnsAsync(1);

        var request = new AgentRequest { Prompt = "fail", WorkspacePath = "/ws", UseResume = true };
        var result = await _provider.ExecuteAsync(request, CancellationToken.None);

        result.ExitCode.Should().Be(1);
        result.Success.Should().BeFalse();
    }

    // --- KillAsync tests ---

    [Fact]
    public async Task KillAsync_DelegatesToOrchestrator()
    {
        await _provider.KillAsync();

        _mockOrchestrator.Verify(o => o.Kill(), Times.Once);
    }

    [Fact]
    public async Task KillAsync_NoOpWhenNoProcess()
    {
        var act = () => _provider.KillAsync();
        await act.Should().NotThrowAsync();
    }

    // --- Model configuration tests ---

    [Fact]
    public void Model_WhenNotProvided_ReturnsNull()
    {
        _provider.Model.Should().BeNull();
    }

    [Fact]
    public void Model_WhenProvided_ReturnsConfiguredValue()
    {
        var provider = new KiroCliAgentProvider(
            _mockOrchestrator.Object, _mockLogger.Object, model: "claude-sonnet-4.6",
            "/usr/bin/fake-kiro-cli", AgentEffortLevel.High, _mockProcessStarter.Object);
        provider.Model.Should().Be("claude-sonnet-4.6");
    }

    [Theory]
    [InlineData(AgentEffortLevel.Auto, null)]
    [InlineData(AgentEffortLevel.Low, "low")]
    [InlineData(AgentEffortLevel.Medium, "medium")]
    [InlineData(AgentEffortLevel.High, "high")]
    [InlineData(AgentEffortLevel.XHigh, "high")] // kiro-cli 2.29 has no xhigh
    [InlineData(AgentEffortLevel.Max, "max")]
    public void CreateRunConfiguration_PassesTheEffortKiroAccepts(AgentEffortLevel effort, string? expected)
    {
        KiroCliAgentProvider.CreateRunConfiguration("/usr/bin/fake-kiro-cli", "claude-sonnet-4.6", null, effort)
            .Effort.Should().Be(expected);
    }

    // --- ExecuteAsync timeout tests ---

    [Fact]
    public async Task ExecuteAsync_Timeout_ReturnsExitCode124()
    {
        _mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .Returns<string, string, bool, CancellationToken, Func<string, Task>?, string?, IReadOnlyDictionary<string, string>?>(
                async (_, _, _, ct, _, _, _) =>
                {
                    // Simulate a long-running operation that exceeds the timeout
                    await Task.Delay(Timeout.Infinite, ct);
                    return 0; // Never reached
                });

        var request = new AgentRequest
        {
            Prompt = "slow prompt",
            WorkspacePath = "/ws",
            Timeout = TimeSpan.FromMilliseconds(50),
            UseResume = true // Use shared orchestrator path so mock handles the call
        };

        var result = await _provider.ExecuteAsync(request, CancellationToken.None);

        result.ExitCode.Should().Be(124);
        result.Success.Should().BeFalse();
    }

    // --- ExecuteAsync ANSI stripping tests ---

    [Fact]
    public async Task ExecuteAsync_StripsAnsiEscapeSequences_FromOutputLines()
    {
        _mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .Callback<string, string, bool, CancellationToken, Func<string, Task>?, string?, IReadOnlyDictionary<string, string>?>(
                (_, _, _, _, onOutput, _, _) =>
                {
                    onOutput?.Invoke("\x1b[31mError:\x1b[0m something failed");
                    onOutput?.Invoke("\x1b[1;32mSuccess\x1b[0m");
                    onOutput?.Invoke("plain text no ansi");
                })
            .ReturnsAsync(0);

        var externalLines = new List<string>();
        var request = new AgentRequest { Prompt = "test", WorkspacePath = "/ws", UseResume = true };
        var result = await _provider.ExecuteAsync(request, CancellationToken.None, line => externalLines.Add(line));

        result.OutputLines.Should().BeEquivalentTo(["Error: something failed", "Success", "plain text no ansi"]);
        externalLines.Should().BeEquivalentTo(["Error: something failed", "Success", "plain text no ansi"]);
        // Verify no ANSI sequences remain
        foreach (var line in result.OutputLines)
        {
            line.Should().NotContain("\x1b[");
        }
    }
}
