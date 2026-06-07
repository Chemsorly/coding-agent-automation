using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Agent.OpenCode;
using FsCheck;
using FsCheck.Xunit;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Agent.UnitTests.OpenCode;

/// <summary>
/// Property-based tests for SSE event routing (Property 5).
/// Verifies that the OpenCodeAgentProvider correctly routes SSE events based on their type:
/// - message.part.updated → [assistant] prefix
/// - tool.execute.before → [tool_call] prefix
/// - tool.execute.after → [tool_result] prefix
/// - permission.updated → auto-approval POST
/// - other types → discarded without callback invocation
///
/// **Validates: Requirements 4.2, 4.3, 4.4, 4.6, 4.11**
/// </summary>
[Trait("Feature", "opencode-agent-executor")]
[Trait("Property", "5")]
public class OpenCodeSseRoutingPropertyTests
{
    private const string TestSessionId = "test-session-routing";

    /// <summary>
    /// Property 5a: message.part.updated events produce [assistant] prefix.
    /// For any text content, the callback receives a line starting with "[assistant] ".
    /// **Validates: Requirements 4.2**
    /// </summary>
    [Property(MaxTest = 20)]
    public void MessagePartUpdated_AppliesAssistantPrefix(NonEmptyString textContent)
    {
        // Arrange
        var text = textContent.Get.Replace("\r", "").Replace("\n", " "); // single-line for SSE
        var sseEvent = new SseEvent
        {
            Type = "message.part.updated",
            SessionId = TestSessionId,
            Part = new MessagePart { Type = "text", Text = text }
        };

        var handler = new MockOpenCodeHandler();
        var factory = new MockOpenCodeClientFactory(handler);
        var provider = OpenCodeTestHelpers.CreateProvider(factory);

        var outputLines = new List<string>();
        Action<string> callback = line => outputLines.Add(line);

        // Build SSE stream with the event
        var sseStream = BuildSseStream(sseEvent);
        handler.ForUrlPattern("/event", HttpStatusCode.OK, sseStream, "text/event-stream");

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var task = provider.ConnectAndProcessSseAsync(TestSessionId, callback, cts.Token);
        task.Wait(TimeSpan.FromSeconds(3));

        // Assert
        outputLines.Should().HaveCountGreaterThanOrEqualTo(1);
        outputLines[0].Should().StartWith("[assistant] ");
    }

    /// <summary>
    /// Property 5b: tool.execute.before events produce [tool_call] prefix.
    /// For any tool name and args, the callback receives a line starting with "[tool_call] ".
    /// **Validates: Requirements 4.3**
    /// </summary>
    [Property(MaxTest = 20)]
    public void ToolExecuteBefore_AppliesToolCallPrefix(NonEmptyString toolName, NonEmptyString toolArgs)
    {
        // Arrange
        var name = toolName.Get.Replace("\r", "").Replace("\n", " ");
        var args = toolArgs.Get.Replace("\r", "").Replace("\n", " ");
        var sseEvent = new SseEvent
        {
            Type = "tool.execute.before",
            SessionId = TestSessionId,
            ToolName = name,
            ToolArgs = args
        };

        var handler = new MockOpenCodeHandler();
        var factory = new MockOpenCodeClientFactory(handler);
        var provider = OpenCodeTestHelpers.CreateProvider(factory);

        var outputLines = new List<string>();
        Action<string> callback = line => outputLines.Add(line);

        var sseStream = BuildSseStream(sseEvent);
        handler.ForUrlPattern("/event", HttpStatusCode.OK, sseStream, "text/event-stream");

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var task = provider.ConnectAndProcessSseAsync(TestSessionId, callback, cts.Token);
        task.Wait(TimeSpan.FromSeconds(3));

        // Assert
        outputLines.Should().HaveCountGreaterThanOrEqualTo(1);
        outputLines[0].Should().StartWith("[tool_call] ");
    }

    /// <summary>
    /// Property 5c: tool.execute.after events produce [tool_result] prefix.
    /// For any tool result content, the callback receives a line starting with "[tool_result] ".
    /// **Validates: Requirements 4.4**
    /// </summary>
    [Property(MaxTest = 20)]
    public void ToolExecuteAfter_AppliesToolResultPrefix(NonEmptyString toolResult)
    {
        // Arrange
        var result = toolResult.Get.Replace("\r", "").Replace("\n", " ");
        var sseEvent = new SseEvent
        {
            Type = "tool.execute.after",
            SessionId = TestSessionId,
            ToolResult = result
        };

        var handler = new MockOpenCodeHandler();
        var factory = new MockOpenCodeClientFactory(handler);
        var provider = OpenCodeTestHelpers.CreateProvider(factory);

        var outputLines = new List<string>();
        Action<string> callback = line => outputLines.Add(line);

        var sseStream = BuildSseStream(sseEvent);
        handler.ForUrlPattern("/event", HttpStatusCode.OK, sseStream, "text/event-stream");

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var task = provider.ConnectAndProcessSseAsync(TestSessionId, callback, cts.Token);
        task.Wait(TimeSpan.FromSeconds(3));

        // Assert
        outputLines.Should().HaveCountGreaterThanOrEqualTo(1);
        outputLines[0].Should().StartWith("[tool_result] ");
    }

    /// <summary>
    /// Property 5d: permission.updated events trigger auto-approval POST.
    /// For any permission ID, a POST to /session/:id/permissions/:permissionId is sent.
    /// **Validates: Requirements 4.6, 4.11**
    /// </summary>
    [Property(MaxTest = 20)]
    public void PermissionUpdated_TriggersAutoApproval(NonEmptyString permissionId)
    {
        // Arrange - sanitize permission ID (no newlines, no slashes that break URL)
        var permId = permissionId.Get
            .Replace("\r", "").Replace("\n", "")
            .Replace("/", "").Replace("\\", "")
            .Replace(" ", "").Replace("?", "").Replace("#", "");
        if (string.IsNullOrEmpty(permId)) permId = "perm-fallback";

        var sseEvent = new SseEvent
        {
            Type = "permission.updated",
            SessionId = TestSessionId,
            PermissionId = permId
        };

        var handler = new MockOpenCodeHandler();
        var factory = new MockOpenCodeClientFactory(handler);
        var provider = OpenCodeTestHelpers.CreateProvider(factory);

        var outputLines = new List<string>();
        Action<string> callback = line => outputLines.Add(line);

        var sseStream = BuildSseStream(sseEvent);
        handler.ForUrlPattern("/event", HttpStatusCode.OK, sseStream, "text/event-stream");
        // Enqueue response for the permission approval POST
        handler.ForUrlPattern($"/session/{TestSessionId}/permissions/", HttpStatusCode.OK, "{}");

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var task = provider.ConnectAndProcessSseAsync(TestSessionId, callback, cts.Token);
        task.Wait(TimeSpan.FromSeconds(3));

        // Assert - callback should NOT be invoked for permission events
        outputLines.Should().BeEmpty("permission.updated should not invoke the output callback");

        // Verify a POST was made to the permissions endpoint
        var permissionRequests = handler.Requests
            .Where(r => r.Method == HttpMethod.Post &&
                        r.Path.Contains("/permissions/"))
            .ToList();
        permissionRequests.Should().HaveCountGreaterThanOrEqualTo(1);

        // Verify the body contains allow + remember
        var body = permissionRequests[0].Body;
        body.Should().NotBeNull();
        body.Should().Contain("allow");
        body.Should().Contain("true");
    }

    /// <summary>
    /// Property 5e: Events with unrecognized types are discarded without invoking the callback.
    /// For any type string that is not one of the known types, the callback is never invoked.
    /// **Validates: Requirements 4.6**
    /// </summary>
    [Property(MaxTest = 20)]
    public void UnrecognizedEventType_IsDiscarded(NonEmptyString randomType)
    {
        // Arrange - ensure the type is not one of the known types
        var knownTypes = new[]
        {
            "message.part.updated", "tool.execute.before", "tool.execute.after",
            "permission.updated", "session.idle"
        };

        var eventType = randomType.Get.Replace("\r", "").Replace("\n", " ");
        if (knownTypes.Contains(eventType, StringComparer.Ordinal))
            eventType = "unknown.custom.event." + eventType;

        var sseEvent = new SseEvent
        {
            Type = eventType,
            SessionId = TestSessionId,
            Part = new MessagePart { Type = "text", Text = "should be discarded" }
        };

        var handler = new MockOpenCodeHandler();
        var factory = new MockOpenCodeClientFactory(handler);
        var provider = OpenCodeTestHelpers.CreateProvider(factory);

        var outputLines = new List<string>();
        Action<string> callback = line => outputLines.Add(line);

        var sseStream = BuildSseStream(sseEvent);
        handler.ForUrlPattern("/event", HttpStatusCode.OK, sseStream, "text/event-stream");

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var task = provider.ConnectAndProcessSseAsync(TestSessionId, callback, cts.Token);
        task.Wait(TimeSpan.FromSeconds(3));

        // Assert - callback should never be invoked for unrecognized types
        outputLines.Should().BeEmpty("unrecognized event types should be discarded without invoking callback");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds an SSE-formatted stream string from an SseEvent.
    /// SSE format: "data: {json}\n\n" (double newline terminates the event).
    /// The stream ends after the event so the reader will see EOF and stop.
    /// </summary>
    private static string BuildSseStream(SseEvent sseEvent)
    {
        var json = JsonSerializer.Serialize(sseEvent, OpenCodeJson.JsonOptions);
        return $"data: {json}\n\n";
    }
}
