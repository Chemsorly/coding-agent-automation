using AwesomeAssertions;
using Moq;
using KiroCliLib.Core;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Agent.KiroCli;
using System.Diagnostics;

namespace CodingAgentWebUI.Agent.UnitTests;

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
        var request = new AgentRequest { Prompt = "test prompt", WorkspacePath = "/workspace" };
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

    [Fact]
    public async Task ExecuteAsync_UseResumeTrue_CallsOrchestratorWithUseResumeTrue()
    {
        _mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                "follow up", "/workspace", true,
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .ReturnsAsync(0);

        var request = new AgentRequest { Prompt = "follow up", WorkspacePath = "/workspace", UseResume = true };
        var result = await _provider.ExecuteAsync(request, CancellationToken.None);

        result.ExitCode.Should().Be(0);
        _mockOrchestrator.Verify(o => o.ExecutePromptAsync(
            "follow up", "/workspace", true,
            It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()), Times.Once);
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
            .Callback<string, string, bool, CancellationToken, Func<string, Task>?, string?>(
                (_, _, _, _, onOutput, _) =>
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

    [Fact]
    public async Task ApplyCliSettingsAsync_WhenModelIsNull_DoesNothing()
    {
        // _provider already has model=null
        await _provider.ApplyCliSettingsAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ApplyCliSettingsAsync_WhenModelIsAuto_DoesNothing()
    {
        var provider = new KiroCliAgentProvider(
            _mockOrchestrator.Object, _mockLogger.Object, model: "auto",
            "/usr/bin/fake-kiro-cli", AgentEffortLevel.High, _mockProcessStarter.Object);
        await provider.ApplyCliSettingsAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ApplyCliSettingsAsync_WhenModelIsAutoUpperCase_DoesNothing()
    {
        var provider = new KiroCliAgentProvider(
            _mockOrchestrator.Object, _mockLogger.Object, model: "Auto",
            "/usr/bin/fake-kiro-cli", AgentEffortLevel.High, _mockProcessStarter.Object);
        await provider.ApplyCliSettingsAsync(CancellationToken.None);
    }

    [Fact]
    public async Task ApplyCliSettingsAsync_WhenModelContainsInvalidChars_RejectsAndLogs()
    {
        var provider = new KiroCliAgentProvider(
            _mockOrchestrator.Object, _mockLogger.Object, model: "foo\" && rm -rf /",
            "/usr/bin/fake-kiro-cli", AgentEffortLevel.High, _mockProcessStarter.Object);
        await provider.ApplyCliSettingsAsync(CancellationToken.None);

        _mockLogger.Verify(l => l.Warning(
            "Invalid model name rejected: {Model}",
            "foo\" && rm -rf /"), Times.Once);
    }

    [Fact]
    public async Task ApplyCliSettingsAsync_WhenModelContainsSpaces_RejectsAndLogs()
    {
        var provider = new KiroCliAgentProvider(
            _mockOrchestrator.Object, _mockLogger.Object, model: "model with spaces",
            "/usr/bin/fake-kiro-cli", AgentEffortLevel.High, _mockProcessStarter.Object);
        await provider.ApplyCliSettingsAsync(CancellationToken.None);

        _mockLogger.Verify(l => l.Warning(
            "Invalid model name rejected: {Model}",
            "model with spaces"), Times.Once);
    }

    [Fact]
    public async Task ApplyCliSettingsAsync_WritesModelAndEffort_ToCliJson()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"kiro-test-{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(tempDir, "cli.json");
        try
        {
            var provider = new KiroCliAgentProvider(
                _mockOrchestrator.Object, _mockLogger.Object, model: "claude-opus-4.6",
                "/usr/bin/fake-kiro-cli", AgentEffortLevel.Max, _mockProcessStarter.Object);

            await provider.ApplyCliSettingsAsync(CancellationToken.None, settingsPath);

            File.Exists(settingsPath).Should().BeTrue();

            var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(settingsPath));
            json!["chat.defaultModel"]!.GetValue<string>().Should().Be("claude-opus-4.6");
            json["chat.modelDefaults"]!["claude-opus-4.6"]!["output_config"]!["effort"]!.GetValue<string>().Should().Be("max");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task ApplyCliSettingsAsync_WithEffortHigh_WritesCorrectValue()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"kiro-test-{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(tempDir, "cli.json");
        try
        {
            var provider = new KiroCliAgentProvider(
                _mockOrchestrator.Object, _mockLogger.Object, model: "claude-sonnet-4.6",
                "/usr/bin/fake-kiro-cli", AgentEffortLevel.High, _mockProcessStarter.Object);

            await provider.ApplyCliSettingsAsync(CancellationToken.None, settingsPath);

            var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(settingsPath));
            json!["chat.defaultModel"]!.GetValue<string>().Should().Be("claude-sonnet-4.6");
            json["chat.modelDefaults"]!["claude-sonnet-4.6"]!["output_config"]!["effort"]!.GetValue<string>().Should().Be("high");
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task ApplyCliSettingsAsync_WithEffortAuto_OmitsModelDefaults()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"kiro-test-{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(tempDir, "cli.json");
        try
        {
            var provider = new KiroCliAgentProvider(
                _mockOrchestrator.Object, _mockLogger.Object, model: "claude-opus-4.6",
                "/usr/bin/fake-kiro-cli", AgentEffortLevel.Auto, _mockProcessStarter.Object);

            await provider.ApplyCliSettingsAsync(CancellationToken.None, settingsPath);

            var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(settingsPath));
            json!["chat.defaultModel"]!.GetValue<string>().Should().Be("claude-opus-4.6");
            json["chat.modelDefaults"].Should().BeNull();
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public async Task ApplyCliSettingsAsync_PreservesExistingSettings()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"kiro-test-{Guid.NewGuid():N}");
        var settingsPath = Path.Combine(tempDir, "cli.json");
        try
        {
            // Pre-populate with existing settings
            Directory.CreateDirectory(tempDir);
            await File.WriteAllTextAsync(settingsPath,
                """{"mcp.loadedBefore": true, "mcp.initTimeout": 30}""");

            var provider = new KiroCliAgentProvider(
                _mockOrchestrator.Object, _mockLogger.Object, model: "claude-opus-4.6",
                "/usr/bin/fake-kiro-cli", AgentEffortLevel.Max, _mockProcessStarter.Object);

            await provider.ApplyCliSettingsAsync(CancellationToken.None, settingsPath);

            var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(settingsPath));
            // New settings present
            json!["chat.defaultModel"]!.GetValue<string>().Should().Be("claude-opus-4.6");
            json["chat.modelDefaults"]!["claude-opus-4.6"]!["output_config"]!["effort"]!.GetValue<string>().Should().Be("max");
            // Existing settings preserved
            json["mcp.loadedBefore"]!.GetValue<bool>().Should().BeTrue();
            json["mcp.initTimeout"]!.GetValue<int>().Should().Be(30);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    // --- ExecuteAsync timeout tests ---

    [Fact]
    public async Task ExecuteAsync_Timeout_ReturnsExitCode124()
    {
        _mockOrchestrator
            .Setup(o => o.ExecutePromptAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<bool>(),
                It.IsAny<CancellationToken>(), It.IsAny<Func<string, Task>?>(), It.IsAny<string?>()))
            .Returns<string, string, bool, CancellationToken, Func<string, Task>?, string?>(
                async (_, _, _, ct, _, _) =>
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
            .Callback<string, string, bool, CancellationToken, Func<string, Task>?, string?>(
                (_, _, _, _, onOutput, _) =>
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
