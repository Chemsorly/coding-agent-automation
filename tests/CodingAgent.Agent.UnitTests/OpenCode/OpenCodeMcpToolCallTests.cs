using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Agent.OpenCode;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Moq;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Agent.UnitTests.OpenCode;

/// <summary>
/// Tests for the MCP tool-call counting feature: <see cref="OpenCodeAgentProvider.ToolKey"/>,
/// SSE tool-part tracking in <see cref="SseCallState"/>, and end-to-end
/// <see cref="AgentResult.ToolCalls"/> / <see cref="AgentResult.UnavailableMcpServers"/> population.
/// </summary>
public class OpenCodeMcpToolCallTests
{
    private const string Session = "ses_mcp";

    // ── ToolKey unit tests ───────────────────────────────────────────────

    [Theory]
    [InlineData("sonarqube_get_issues", new[] { "sonarqube" }, "sonarqube/get_issues")]
    [InlineData("context_7_resolve_library_id", new[] { "context 7" }, "context 7/resolve_library_id")]
    [InlineData("grafana_query_loki_logs", new[] { "grafana", "grafana_cloud" }, "grafana/query_loki_logs")]
    [InlineData("grafana_cloud_list", new[] { "grafana", "grafana_cloud" }, "grafana_cloud/list")]
    [InlineData("bash", new[] { "sonarqube" }, null)]
    [InlineData("edit", new[] { "sonarqube" }, null)]
    [InlineData("read", new[] { "sonarqube" }, null)]
    [InlineData("webfetch", new[] { "sonarqube" }, "web/fetch")]
    [InlineData("websearch", new[] { "sonarqube" }, "web/search")]
    [InlineData("sonarqube_", new[] { "sonarqube" }, null)]
    public void ToolKey_ReturnsExpectedKey(string tool, string[] serverNames, string? expected)
    {
        OpenCodeAgentProvider.ToolKey(tool, serverNames).Should().Be(expected);
    }

    // ── SSE tool-part tracking tests ─────────────────────────────────────

    private static string Frame(string type, object properties) =>
        $"data: {JsonSerializer.Serialize(new { id = "evt", type, properties })}\n\n";

    private static string ToolPart(string callId, string tool, object state) =>
        Frame("message.part.updated", new
        {
            sessionID = Session,
            time = 1,
            part = new { id = "prt_" + callId, sessionID = Session, messageID = "msg_a", type = "tool", callID = callId, tool, state }
        });

    private static async Task<SseCallState> ProcessSseAsync(string sse, Action<string>? onOutputLine = null)
    {
        var handler = new SseStreamMockHandler(sse);
        var provider = new OpenCodeAgentProvider(new SseStreamClientFactory(handler), new Mock<ILogger>().Object);
        var state = new SseCallState();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await provider.ConnectAndProcessSseAsync(Session, onOutputLine, cts.Token, "/ws", state);
        return state;
    }

    [Fact]
    public async Task SseToolPart_RunningCompletedCompleted_CountedOnce_NullOnOutputLine()
    {
        var sse = ToolPart("c1", "bash", new { status = "running" })
            + ToolPart("c1", "bash", new { status = "completed" })
            + ToolPart("c1", "bash", new { status = "completed" });

        var state = await ProcessSseAsync(sse, onOutputLine: null);

        state.ToolCallNames.Should().HaveCount(1);
        state.ToolCallNames["c1"].Should().Be("bash");
        state.ToolCallErrors.Should().BeEmpty();
    }

    [Fact]
    public async Task SseToolPart_ErrorStatus_PopulatesToolCallErrors()
    {
        var sse = ToolPart("c2", "sonarqube_get_issues",
            new { status = "error", error = "Error: 401 Unauthorized" });

        var state = await ProcessSseAsync(sse);

        state.ToolCallNames.Should().ContainKey("c2").WhoseValue.Should().Be("sonarqube_get_issues");
        state.ToolCallErrors.Should().ContainKey("c2").WhoseValue.Should().Be("Error: 401 Unauthorized");
    }

    // ── Full ExecuteAsync tests ───────────────────────────────────────────

    // Helper: build MCP status JSON
    private static string McpStatusJson(object statusObject) =>
        JsonSerializer.Serialize(statusObject, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });

    // Helper: create a provider backed by a CaptureSink logger
    private static (MockOpenCodeHandler Handler, OpenCodeAgentProvider Provider, OpenCodeCaptureSink Sink) CreateContextWithCaptureSink()
    {
        var sink = new OpenCodeCaptureSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var handler = new MockOpenCodeHandler();
        var factory = new MockOpenCodeClientFactory(handler);
        var provider = new OpenCodeAgentProvider(factory, logger);
        return (handler, provider, sink);
    }

    // Helper: queue the standard set of responses for a test run
    // (session creation via FIFO, SSE + message + /mcp via URL patterns)
    private static void SetupRun(
        MockOpenCodeHandler handler,
        string sessionId,
        string sseContent,
        string mcpResponseJson,
        HttpStatusCode mcpStatusCode = HttpStatusCode.OK)
    {
        // Session creation (FIFO — before any pattern, and POST /session$ won't match "/event", "/mcp", etc.)
        handler.EnqueueJsonResponse(new CreateSessionResponse { Id = sessionId });

        // SSE stream
        handler.ForUrlPattern("/event", HttpStatusCode.OK, sseContent, "text/event-stream");

        // Message response (minimal success)
        handler.ForUrlPattern("/session/.+/message",
            new SendMessageResponse { Parts = [] });

        // MCP status endpoint
        handler.ForUrlPattern("^/mcp(\\?.*)?$", mcpStatusCode, mcpResponseJson);
    }

    [Fact]
    public async Task ExecuteAsync_McpStatus_MapsFailedAndNeedsAuth_ToUnavailableServers()
    {
        var (handler, provider, sink) = CreateContextWithCaptureSink();
        var mcpJson = McpStatusJson(new
        {
            sonarqube = new { status = "connected" },
            grafana = new { status = "failed", error = "x" },
            github = new { status = "needs_auth" },
            gitlab = new { status = "needs_client_registration" },
            old = new { status = "disabled" }
        });
        SetupRun(handler, Session, "", mcpJson);

        var result = await provider.ExecuteAsync(
            OpenCodeTestHelpers.CreateRequest("p"), CancellationToken.None);

        result.UnavailableMcpServers.Should().NotBeNull();
        result.UnavailableMcpServers!.Should().HaveCount(3);
        result.UnavailableMcpServers.Should().ContainKey("grafana").WhoseValue.Should().Be("failed");
        result.UnavailableMcpServers.Should().ContainKey("github").WhoseValue.Should().Be("needs-auth");
        result.UnavailableMcpServers.Should().ContainKey("gitlab").WhoseValue.Should().Be("needs-auth");
        result.UnavailableMcpServers.Should().NotContainKey("sonarqube");
        result.UnavailableMcpServers.Should().NotContainKey("old");

        // Three Unavailable warnings (one per unavailable server)
        sink.Events.Count(e => e.MessageTemplate.Text.Contains("MCP server unavailable")).Should().Be(3);
    }

    [Fact]
    public async Task ExecuteAsync_ToolErrorAuth_GivesFailedAuth1_AndLogsMcpCallFailedWarning()
    {
        var (handler, provider, sink) = CreateContextWithCaptureSink();
        var sseContent =
            ToolPart("c1", "sonarqube_get_issues", new { status = "error", error = "Error: 401 Unauthorized" });
        var mcpJson = McpStatusJson(new { sonarqube = new { status = "connected" } });
        SetupRun(handler, Session, sseContent, mcpJson);

        var result = await provider.ExecuteAsync(
            OpenCodeTestHelpers.CreateRequest("p"), CancellationToken.None);

        result.ToolCalls.Should().NotBeNull();
        result.ToolCalls!.Should().ContainKey("sonarqube/get_issues");
        var counts = result.ToolCalls["sonarqube/get_issues"];
        counts.Calls.Should().Be(1);
        counts.FailedAuth.Should().Be(1);
        counts.FailedConnection.Should().Be(0);
        counts.FailedOther.Should().Be(0);

        sink.Events.Count(e => e.MessageTemplate.Text.Contains("MCP call failed")).Should().Be(1);
    }

    [Fact]
    public async Task ExecuteAsync_Mcp404_ConfigFileFallback_SplitsToolKey()
    {
        var (handler, provider, _) = CreateContextWithCaptureSink();
        var sseContent = ToolPart("c1", "sonarqube_get_issues", new { status = "completed" });
        SetupRun(handler, Session, sseContent, "{}", HttpStatusCode.NotFound);

        // Write a config file with sonarqube as an MCP server
        var configPath = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            await File.WriteAllTextAsync(configPath, """{"mcp":{"sonarqube":{}}}""");
            provider.McpConfigPathOverride = configPath;

            var result = await provider.ExecuteAsync(
                OpenCodeTestHelpers.CreateRequest("p"), CancellationToken.None);

            result.ToolCalls.Should().NotBeNull();
            result.ToolCalls!.Should().ContainKey("sonarqube/get_issues");
            result.ToolCalls["sonarqube/get_issues"].Calls.Should().Be(1);
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public async Task ExecuteAsync_Mcp404_NoConfigFile_WarningLoggedOnce_NothingCounted()
    {
        var (handler, provider, sink) = CreateContextWithCaptureSink();
        var sseContent = ToolPart("c1", "sonarqube_get_issues", new { status = "completed" });
        SetupRun(handler, Session, sseContent, "{}", HttpStatusCode.NotFound);

        // Point to a nonexistent config file
        provider.McpConfigPathOverride = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");

        var result = await provider.ExecuteAsync(
            OpenCodeTestHelpers.CreateRequest("p"), CancellationToken.None);

        result.ToolCalls.Should().BeNull("no server names → no counts");

        sink.Events
            .Count(e => e.MessageTemplate.Text.Contains("OpenCode MCP server names unavailable"))
            .Should().Be(1, "warning must be logged exactly once per invocation");
    }

    [Fact]
    public async Task ExecuteAsync_EnvironmentVariables_MaskedInMcpCallFailedLog()
    {
        var (handler, provider, sink) = CreateContextWithCaptureSink();
        const string secret = "s3cr3t_token";
        var sseContent = ToolPart("c1", "sonarqube_get_issues",
            new { status = "error", error = $"Token {secret} failed" });
        var mcpJson = McpStatusJson(new { sonarqube = new { status = "connected" } });
        SetupRun(handler, Session, sseContent, mcpJson);

        var request = new AgentRequest
        {
            Prompt = "test",
            WorkspacePath = Path.GetTempPath(),
            Timeout = TimeSpan.FromMinutes(5),
            EnvironmentVariables = new Dictionary<string, string> { ["API_KEY"] = secret }
        };

        await provider.ExecuteAsync(request, CancellationToken.None);

        var failedEvent = sink.Events.Single(e => e.MessageTemplate.Text.Contains("MCP call failed"));
        var loggedError = failedEvent.Properties["McpError"].ToString().Trim('"');
        loggedError.Should().NotContain(secret, "secret must be replaced with ***");
        loggedError.Should().Contain("***");
    }
}

/// <summary>
/// Simple in-memory Serilog sink for capturing log events in OpenCode MCP tests.
/// </summary>
internal sealed class OpenCodeCaptureSink : ILogEventSink
{
    private readonly ConcurrentQueue<LogEvent> _events = new();
    public IReadOnlyCollection<LogEvent> Events => _events;
    public void Emit(LogEvent logEvent) => _events.Enqueue(logEvent);
}
