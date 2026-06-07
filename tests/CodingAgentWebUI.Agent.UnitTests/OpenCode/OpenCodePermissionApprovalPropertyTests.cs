using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Agent.OpenCode;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Agent.UnitTests.OpenCode;

/// <summary>
/// Property-based tests for permission auto-approval (Property 4).
/// Verifies that for any permission.updated SSE event with a session ID matching
/// the active session and a non-null permission ID, the provider sends
/// POST /session/:id/permissions/:permissionId with body { "response": "allow", "remember": true }.
/// </summary>
[Trait("Feature", "opencode-agent-executor")]
[Trait("Property", "4")]
public class OpenCodePermissionApprovalPropertyTests
{
    /// <summary>
    /// Property 4: For any permission.updated event with matching session ID and non-null permission ID,
    /// the provider SHALL send POST /session/:id/permissions/:permissionId with the correct body.
    /// **Validates: Requirements 1.9, 4.11**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(PermissionApprovalArbitraries)])]
    public void MatchingSession_PermissionApproved_WithCorrectBody(
        AlphanumericString sessionId,
        AlphanumericString permissionId)
    {
        // Arrange
        var activeSessionId = sessionId.Value;
        var permId = permissionId.Value;

        var sseEvent = new SseEvent
        {
            Type = "permission.updated",
            SessionId = activeSessionId,
            PermissionId = permId
        };

        var handler = new SseStreamMockHandler(BuildSseStream(sseEvent));
        var factory = new SseStreamClientFactory(handler);
        var logger = new Mock<ILogger>();
        var provider = new OpenCodeAgentProvider(factory, logger.Object);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = provider.ConnectAndProcessSseAsync(activeSessionId, null, cts.Token);
        task.Wait(TimeSpan.FromSeconds(5));

        // Assert — a POST to /session/{sessionId}/permissions/{permissionId} should have been made
        var permissionRequests = handler.Requests
            .Where(r => r.Method == HttpMethod.Post &&
                        r.Path.Contains($"/session/{activeSessionId}/permissions/{permId}"))
            .ToList();

        permissionRequests.Should().HaveCount(1,
            "exactly one permission approval POST should be sent for a matching permission.updated event");

        // Verify the request body contains { "response": "allow", "remember": true }
        var requestBody = permissionRequests[0].Body;
        requestBody.Should().NotBeNull("permission approval request should have a body");

        var parsed = JsonSerializer.Deserialize<PermissionResponse>(requestBody!, OpenCodeJson.JsonOptions);
        parsed.Should().NotBeNull();
        parsed!.Response.Should().Be("allow");
        parsed.Remember.Should().BeTrue();
    }

    /// <summary>
    /// Property 4: For any permission.updated event with matching session ID and non-null permission ID,
    /// the POST URL SHALL contain the correct session ID and permission ID path segments.
    /// **Validates: Requirements 1.9, 4.11**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(PermissionApprovalArbitraries)])]
    public void MatchingSession_PermissionApproval_CorrectUrl(
        AlphanumericString sessionId,
        AlphanumericString permissionId)
    {
        // Arrange
        var activeSessionId = sessionId.Value;
        var permId = permissionId.Value;

        var sseEvent = new SseEvent
        {
            Type = "permission.updated",
            SessionId = activeSessionId,
            PermissionId = permId
        };

        var handler = new SseStreamMockHandler(BuildSseStream(sseEvent));
        var factory = new SseStreamClientFactory(handler);
        var logger = new Mock<ILogger>();
        var provider = new OpenCodeAgentProvider(factory, logger.Object);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = provider.ConnectAndProcessSseAsync(activeSessionId, null, cts.Token);
        task.Wait(TimeSpan.FromSeconds(5));

        // Assert — verify the URL path is exactly /session/{sessionId}/permissions/{permissionId}
        var permissionRequests = handler.Requests
            .Where(r => r.Method == HttpMethod.Post && r.Path.Contains("/permissions/"))
            .ToList();

        permissionRequests.Should().HaveCount(1);
        permissionRequests[0].Path.Should().Be($"/session/{activeSessionId}/permissions/{permId}");
    }

    /// <summary>
    /// Property 4: Multiple permission.updated events with different permission IDs in the same session
    /// SHALL each trigger a separate POST with the correct permission ID.
    /// **Validates: Requirements 1.9, 4.11**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(PermissionApprovalArbitraries)])]
    public void MultiplePermissions_EachApprovedIndependently(
        AlphanumericString sessionId,
        AlphanumericString permId1,
        AlphanumericString permId2)
    {
        // Ensure permission IDs are different
        if (permId1.Value == permId2.Value) return;

        // Arrange
        var activeSessionId = sessionId.Value;

        var events = new[]
        {
            new SseEvent
            {
                Type = "permission.updated",
                SessionId = activeSessionId,
                PermissionId = permId1.Value
            },
            new SseEvent
            {
                Type = "permission.updated",
                SessionId = activeSessionId,
                PermissionId = permId2.Value
            }
        };

        var handler = new SseStreamMockHandler(BuildSseStream(events));
        var factory = new SseStreamClientFactory(handler);
        var logger = new Mock<ILogger>();
        var provider = new OpenCodeAgentProvider(factory, logger.Object);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = provider.ConnectAndProcessSseAsync(activeSessionId, null, cts.Token);
        task.Wait(TimeSpan.FromSeconds(5));

        // Assert — two separate permission approval POSTs should have been made
        var permissionRequests = handler.Requests
            .Where(r => r.Method == HttpMethod.Post && r.Path.Contains("/permissions/"))
            .ToList();

        permissionRequests.Should().HaveCount(2,
            "each permission.updated event should trigger a separate approval POST");

        permissionRequests.Should().Contain(r => r.Path.Contains(permId1.Value));
        permissionRequests.Should().Contain(r => r.Path.Contains(permId2.Value));

        // Verify both have correct body
        foreach (var req in permissionRequests)
        {
            var parsed = JsonSerializer.Deserialize<PermissionResponse>(req.Body!, OpenCodeJson.JsonOptions);
            parsed.Should().NotBeNull();
            parsed!.Response.Should().Be("allow");
            parsed.Remember.Should().BeTrue();
        }
    }

    /// <summary>
    /// Property 4: Permission events with null permission ID SHALL NOT trigger an approval POST.
    /// The AutoApprovePermissionAsync method guards against null/empty permission IDs.
    /// **Validates: Requirements 1.9, 4.11**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(PermissionApprovalArbitraries)])]
    public void NullPermissionId_NoApprovalSent(AlphanumericString sessionId)
    {
        // Arrange
        var activeSessionId = sessionId.Value;

        var sseEvent = new SseEvent
        {
            Type = "permission.updated",
            SessionId = activeSessionId,
            PermissionId = null
        };

        var handler = new SseStreamMockHandler(BuildSseStream(sseEvent));
        var factory = new SseStreamClientFactory(handler);
        var logger = new Mock<ILogger>();
        var provider = new OpenCodeAgentProvider(factory, logger.Object);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = provider.ConnectAndProcessSseAsync(activeSessionId, null, cts.Token);
        task.Wait(TimeSpan.FromSeconds(5));

        // Assert — no permission approval POST should have been made
        handler.Requests.Should().NotContain(r =>
            r.Method == HttpMethod.Post && r.Path.Contains("/permissions/"),
            "null permission ID should not trigger an approval POST");
    }

    /// <summary>
    /// Property 4: Permission events with non-matching session ID SHALL NOT trigger approval.
    /// This confirms the session filtering interacts correctly with permission approval.
    /// **Validates: Requirements 1.9, 4.11**
    /// </summary>
    [Property(MaxTest = 20, Arbitrary = [typeof(PermissionApprovalArbitraries)])]
    public void NonMatchingSession_PermissionNotApproved(
        AlphanumericString activeSession,
        AlphanumericString eventSession,
        AlphanumericString permissionId)
    {
        // Ensure session IDs are different
        if (activeSession.Value == eventSession.Value) return;

        // Arrange
        var sseEvent = new SseEvent
        {
            Type = "permission.updated",
            SessionId = eventSession.Value,
            PermissionId = permissionId.Value
        };

        var handler = new SseStreamMockHandler(BuildSseStream(sseEvent));
        var factory = new SseStreamClientFactory(handler);
        var logger = new Mock<ILogger>();
        var provider = new OpenCodeAgentProvider(factory, logger.Object);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = provider.ConnectAndProcessSseAsync(activeSession.Value, null, cts.Token);
        task.Wait(TimeSpan.FromSeconds(5));

        // Assert — no permission approval POST should have been made
        handler.Requests.Should().NotContain(r =>
            r.Method == HttpMethod.Post && r.Path.Contains("/permissions/"),
            "permission events for non-matching sessions should not trigger approval");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Builds an SSE-formatted stream string from one or more SseEvent objects.
    /// Each event is serialized as a "data: {json}\n\n" line per SSE protocol.
    /// </summary>
    private static string BuildSseStream(params SseEvent[] events)
    {
        var sb = new StringBuilder();
        foreach (var evt in events)
        {
            var json = JsonSerializer.Serialize(evt, OpenCodeJson.JsonOptions);
            sb.AppendLine($"data: {json}");
            sb.AppendLine(); // blank line separates SSE events
        }
        return sb.ToString();
    }
}

/// <summary>
/// Wrapper type for alphanumeric strings used in property tests.
/// Ensures generated values are safe for URL paths and JSON serialization.
/// </summary>
public sealed class AlphanumericString
{
    public string Value { get; }

    public AlphanumericString(string value)
    {
        Value = value;
    }

    public override string ToString() => $"AlphanumericString \"{Value}\"";
}

/// <summary>
/// FsCheck Arbitrary provider for permission approval property tests.
/// Generates alphanumeric strings that are safe for URL path segments and JSON.
/// </summary>
public static class PermissionApprovalArbitraries
{
    private static readonly string[] IdPool =
    [
        "sess-001", "sess-002", "sess-abc", "session-xyz-123",
        "perm-001", "perm-002", "perm-abc", "permission-xyz-456",
        "a1b2c3d4", "e5f6g7h8", "test-id-99", "uuid-like-id",
        "abc123", "def456", "ghi789", "jkl012", "mno345", "pqr678",
        "simple", "another", "third-one", "fourth-id", "fifth-val",
        "alpha", "beta", "gamma", "delta", "epsilon", "zeta"
    ];

    public static Arbitrary<AlphanumericString> AlphanumericStringArbitrary()
    {
        return FsCheck.Fluent.Gen.Elements(IdPool)
            .Select(s => new AlphanumericString(s))
            .ToArbitrary();
    }
}
