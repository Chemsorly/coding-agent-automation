using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Infrastructure;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.Hosting;
using Moq;

namespace CodingAgent.Agent.UnitTests.ClaudeCode;

/// <summary>
/// Chat prompts on a Claude Code pod: they run on <see cref="IAgentProvider"/> (no warm-up), the
/// provider lives as long as the conversation, steering goes to the user rules directory and the
/// MCP config is written in the CLI's format.
/// </summary>
[Collection("EnvironmentVariables")]
public class ChatJobExecutorClaudeCodeTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"claude-chat-{Guid.NewGuid():N}");
    private readonly List<(ChatPromptMessage Message, Mock<IAgentProvider> Provider)> _created = [];
    private readonly List<AgentRequest> _requests = [];

    public ChatJobExecutorClaudeCodeTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        foreach (var dir in new[] { _tempDir, AgentDefaults.ChatWorkspacesRoot })
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
            catch { /* best effort */ }
        }
        GC.SuppressFinalize(this);
    }

    private string RulesDirectory => Path.Combine(_tempDir, "rules");

    private ChatJobExecutor CreateExecutor()
    {
        var logger = new Mock<Serilog.ILogger>().Object;
        var lifetime = Mock.Of<IHostApplicationLifetime>();
        var slotManager = new ChatSlotManager();
        var lifecycle = new AgentConnectionLifecycle(
            TestAgentWorkerServiceFactory.CreateTestHubManager(logger),
            TestAgentWorkerServiceFactory.CreateTestHubManagerFactory(logger),
            new AgentId("test-chat"), lifetime, logger);

        return new ChatJobExecutor(new ChatJobExecutorDependencies(
            lifecycle, slotManager, new Mock<KiroCliLib.Core.IKiroCliOrchestrator>(MockBehavior.Strict).Object,
            Mock.Of<System.Net.Http.IHttpClientFactory>(), lifetime,
            SignalAgentReady: () => Task.CompletedTask,
            IsOpenCodeProvider: false,
            IsChatMode: true,
            Logger: logger)
        {
            ProviderType = AgentProviderType.ClaudeCode,
            ClaudeRulesDirectory = RulesDirectory,
            ClaudeCodeProviderFactory = message =>
            {
                var provider = new Mock<IAgentProvider>();
                provider.SetupGet(p => p.ProviderType).Returns(AgentProviderType.ClaudeCode);
                provider
                    .Setup(p => p.ExecuteAsync(It.IsAny<AgentRequest>(), It.IsAny<CancellationToken>(), It.IsAny<Action<string>?>()))
                    .Returns((AgentRequest request, CancellationToken _, Action<string>? onLine) =>
                    {
                        _requests.Add(request);
                        onLine?.Invoke("answer");
                        return Task.FromResult(new AgentResult { ExitCode = 0, OutputLines = ["answer"] });
                    });
                _created.Add((message, provider));
                return provider.Object;
            }
        });
    }

    private ChatPromptMessage Prompt(string text, bool useResume, string? steering = null, IReadOnlyList<McpServerConfig>? mcp = null) => new()
    {
        SessionId = "session",
        Prompt = text,
        UseResume = useResume,
        ChatWindowId = "window-1",
        AgentAuthMode = ClaudeCodeAuthModes.Subscription,
        ProjectSteeringContent = steering,
        ProjectSecrets = new Dictionary<string, string> { ["TOKEN"] = "value" },
        McpServers = mcp ?? [],
        McpConfigPath = Path.Combine(_tempDir, "pipeline-mcp.json")
    };

    [Fact]
    public async Task Conversation_RunsOnOneProvider_UntilANewConversationStarts()
    {
        var executor = CreateExecutor();
        await using var batcher = new OutputBatcher();

        var first = await executor.ExecuteChatWithOutputAsync(Prompt("hi", useResume: false), batcher, CancellationToken.None);
        await executor.ExecuteChatWithOutputAsync(Prompt("more", useResume: true), batcher, CancellationToken.None);
        await executor.ExecuteChatWithOutputAsync(Prompt("new chat", useResume: false), batcher, CancellationToken.None);

        first.exitCode.Should().Be(0);
        _created.Should().HaveCount(2, "the follow-up prompt reuses the provider that knows the session");
        _created[0].Message.AgentAuthMode.Should().Be(ClaudeCodeAuthModes.Subscription);
        _created[0].Provider.Verify(p => p.DisposeAsync(), Times.Once);
        _requests.Select(r => (r.Prompt, r.UseResume)).Should().Equal(("hi", false), ("more", true), ("new chat", false));
        _requests[0].WorkspacePath.Should().Be(Path.Combine(AgentDefaults.ChatWorkspacesRoot, "window-1"));
        _requests[0].EnvironmentVariables.Should().ContainKey("TOKEN");
    }

    [Fact]
    public async Task DisposeAsync_DisposesTheConversationsProvider()
    {
        var executor = CreateExecutor();
        await using var batcher = new OutputBatcher();
        await executor.ExecuteChatWithOutputAsync(Prompt("hi", useResume: false), batcher, CancellationToken.None);

        await executor.DisposeAsync();
        await executor.DisposeAsync();

        _created.Should().ContainSingle().Which.Provider.Verify(p => p.DisposeAsync(), Times.Once);
    }

    [Fact]
    public async Task FirstPrompt_WritesSteeringAsUserRule_AndMcpConfigInClaudeFormat()
    {
        var executor = CreateExecutor();
        await using var batcher = new OutputBatcher();
        McpServerConfig[] servers =
        [
            new() { Name = "docs", Type = "http", Url = "https://mcp.example/docs" },
            new() { Name = "off", Command = "x", Disabled = true }
        ];

        await executor.ExecuteChatWithOutputAsync(
            Prompt("hi", useResume: false, steering: "Always write tests.", mcp: servers), batcher, CancellationToken.None);

        File.ReadAllText(Path.Combine(RulesDirectory, ClaudeSteeringFiles.ProjectFileName)).Should().Contain("Always write tests.");
        Directory.Exists(Path.Combine(AgentDefaults.ChatWorkspacesRoot, "window-1", ".kiro")).Should().BeFalse();

        using var mcp = JsonDocument.Parse(File.ReadAllText(Path.Combine(_tempDir, "pipeline-mcp.json")));
        var mcpServers = mcp.RootElement.GetProperty("mcpServers");
        mcpServers.TryGetProperty("off", out _).Should().BeFalse();
        mcpServers.GetProperty("docs").GetProperty("type").GetString().Should().Be("http");
        mcpServers.GetProperty("docs").TryGetProperty("disabled", out _).Should().BeFalse();
    }
}
