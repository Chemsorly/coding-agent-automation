using System.Net;
using System.Text.Json;
using CodingAgentWebUI.Agent.OpenCode;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent.UnitTests.OpenCode;

/// <summary>
/// Tests for output deduplication between SSE stream and HTTP response.
/// Verifies that when SSE successfully streams assistant content via message.part.updated
/// events, the HTTP response lines are NOT re-emitted via the onOutputLine callback,
/// preventing duplicate content in the output panel.
/// </summary>
[Trait("Feature", "opencode-agent-executor")]
[Trait("Property", "8")]
public class OpenCodeOutputDeduplicationTests
{
    private const string TestSessionId = "test-session-dedup";

    /// <summary>
    /// When SSE emits message.part.updated events, the HTTP response lines
    /// should NOT be emitted via onOutputLine (SSE already provided them).
    /// AgentResult.OutputLines is still populated from the HTTP response.
    /// </summary>
    [Fact]
    public async Task SseEmitsAssistantContent_HttpResponseLinesNotEmittedViaCallback()
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, TestSessionId);

        var responseText = "Hello from the agent";
        var messageResponse = new SendMessageResponse
        {
            Parts = [new MessagePart { Type = "text", Text = responseText }]
        };
        ctx.Handler.ForUrlPattern("/session/.+/message", messageResponse);

        // Set up SSE stream with a message.part.updated event
        var sseEvent = new SseEvent
        {
            Type = "message.part.updated",
            SessionId = TestSessionId,
            Part = new MessagePart { Type = "text", Text = responseText }
        };
        var sseStream = $"data: {JsonSerializer.Serialize(sseEvent, OpenCodeJson.JsonOptions)}\n\n";
        ctx.Handler.ForUrlPattern("/event", HttpStatusCode.OK, sseStream, "text/event-stream");

        var callbackLines = new List<string>();
        var request = OpenCodeTestHelpers.CreateRequest(prompt: "test");

        // Act
        var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None, line => callbackLines.Add(line));

        // Assert: AgentResult.OutputLines always populated from HTTP response
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains(responseText, result.OutputLines);

        // Assert: callback received SSE [assistant] line but NOT the raw HTTP response line
        Assert.Contains(callbackLines, l => l.StartsWith("[assistant] "));
        Assert.DoesNotContain(callbackLines, l => l == responseText);
    }

    /// <summary>
    /// When SSE fails to connect (no assistant content streamed), the HTTP response
    /// lines SHOULD be emitted via onOutputLine as a fallback.
    /// </summary>
    [Fact]
    public async Task SseFailsToConnect_HttpResponseLinesEmittedViaCallback()
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, TestSessionId);

        var responseText = "Hello from the agent";
        var messageResponse = new SendMessageResponse
        {
            Parts = [new MessagePart { Type = "text", Text = responseText }]
        };
        ctx.Handler.ForUrlPattern("/session/.+/message", messageResponse);

        // No SSE stream configured — /event will return 500, SSE fails silently

        var callbackLines = new List<string>();
        var request = OpenCodeTestHelpers.CreateRequest(prompt: "test");

        // Act
        var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None, line => callbackLines.Add(line));

        // Assert: HTTP response lines emitted as fallback
        Assert.Equal(ExitCodes.Success, result.ExitCode);
        Assert.Contains(callbackLines, l => l == responseText);
    }

    /// <summary>
    /// Tool call SSE events are always emitted regardless of the dedup flag.
    /// They don't overlap with HTTP response text parts.
    /// </summary>
    [Fact]
    public async Task ToolCallEvents_AlwaysEmittedRegardlessOfDedupFlag()
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, TestSessionId);

        var responseText = "Done";
        var messageResponse = new SendMessageResponse
        {
            Parts = [new MessagePart { Type = "text", Text = responseText }]
        };
        ctx.Handler.ForUrlPattern("/session/.+/message", messageResponse);

        // SSE stream with both assistant content and tool calls
        var events = new[]
        {
            new SseEvent { Type = "message.part.updated", SessionId = TestSessionId, Part = new MessagePart { Type = "text", Text = "thinking" } },
            new SseEvent { Type = "tool.execute.before", SessionId = TestSessionId, ToolName = "read_file", ToolArgs = "test.cs" },
            new SseEvent { Type = "tool.execute.after", SessionId = TestSessionId, ToolResult = "file content" }
        };
        var sseStream = string.Join("", events.Select(e => $"data: {JsonSerializer.Serialize(e, OpenCodeJson.JsonOptions)}\n\n"));
        ctx.Handler.ForUrlPattern("/event", HttpStatusCode.OK, sseStream, "text/event-stream");

        var callbackLines = new List<string>();
        var request = OpenCodeTestHelpers.CreateRequest(prompt: "test");

        // Act
        var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None, line => callbackLines.Add(line));

        // Assert: tool calls always emitted
        Assert.Contains(callbackLines, l => l.StartsWith("[tool_call] "));
        Assert.Contains(callbackLines, l => l.StartsWith("[tool_result] "));
        // Assert: assistant content from SSE present
        Assert.Contains(callbackLines, l => l.StartsWith("[assistant] "));
        // Assert: HTTP response NOT duplicated (SSE already streamed assistant content)
        Assert.DoesNotContain(callbackLines, l => l == responseText);
    }
}
