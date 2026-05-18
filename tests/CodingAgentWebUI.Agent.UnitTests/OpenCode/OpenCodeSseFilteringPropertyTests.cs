using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Agent.OpenCode;
using FsCheck;
using FsCheck.Xunit;
using Moq;
using ILogger = Serilog.ILogger;
using CodingAgentWebUI.Agent;

namespace CodingAgentWebUI.Agent.UnitTests.OpenCode;

/// <summary>
/// Property-based tests for SSE session ID filtering (Property 7).
/// Verifies that only SSE events whose session ID matches the active session
/// are processed; events with non-matching or null session IDs are discarded
/// without invoking callbacks or triggering permission approval.
/// </summary>
[Trait("Feature", "opencode-agent-executor")]
[Trait("Property", "7")]
public class OpenCodeSseFilteringPropertyTests
{
    /// <summary>
    /// Property 7: Events with matching session ID are processed (callback invoked).
    /// For any SSE event with a session ID that matches the active session,
    /// the event SHALL be processed and the callback SHALL be invoked.
    /// **Validates: Requirements 4.12**
    /// </summary>
    [Property(MaxTest = 100)]
    public void MatchingSessionId_EventIsProcessed(NonEmptyString sessionId, NonEmptyString textContent)
    {
        // Arrange
        var activeSessionId = sessionId.Get;
        var text = textContent.Get;

        var sseEvent = new SseEvent
        {
            Type = "message.part.updated",
            SessionId = activeSessionId,
            Part = new MessagePart { Type = "text", Text = text }
        };

        var handler = new SseStreamMockHandler(BuildSseStream(sseEvent));
        var factory = new SseStreamClientFactory(handler);
        var logger = new Mock<ILogger>();
        var provider = new OpenCodeAgentProvider(factory, logger.Object);

        var callbackInvocations = new List<string>();
        Action<string> callback = line => callbackInvocations.Add(line);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = provider.ConnectAndProcessSseAsync(activeSessionId, callback, cts.Token);
        task.Wait(TimeSpan.FromSeconds(5));

        // Assert
        callbackInvocations.Should().NotBeEmpty("matching session ID events should invoke callback");
    }

    /// <summary>
    /// Property 7: Events with non-matching session ID are discarded (callback NOT invoked).
    /// For any SSE event with a session ID that does NOT match the active session,
    /// the event SHALL be discarded without invoking the callback.
    /// **Validates: Requirements 4.12**
    /// </summary>
    [Property(MaxTest = 100)]
    public void NonMatchingSessionId_EventIsDiscarded(
        NonEmptyString activeSession,
        NonEmptyString eventSession,
        NonEmptyString textContent)
    {
        // Ensure session IDs are different
        if (activeSession.Get == eventSession.Get) return;

        // Arrange
        var activeSessionId = activeSession.Get;
        var eventSessionId = eventSession.Get;
        var text = textContent.Get;

        var sseEvent = new SseEvent
        {
            Type = "message.part.updated",
            SessionId = eventSessionId,
            Part = new MessagePart { Type = "text", Text = text }
        };

        var handler = new SseStreamMockHandler(BuildSseStream(sseEvent));
        var factory = new SseStreamClientFactory(handler);
        var logger = new Mock<ILogger>();
        var provider = new OpenCodeAgentProvider(factory, logger.Object);

        var callbackInvocations = new List<string>();
        Action<string> callback = line => callbackInvocations.Add(line);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = provider.ConnectAndProcessSseAsync(activeSessionId, callback, cts.Token);
        task.Wait(TimeSpan.FromSeconds(5));

        // Assert
        callbackInvocations.Should().BeEmpty("non-matching session ID events should be discarded");
    }

    /// <summary>
    /// Property 7: Events with null session ID are discarded (callback NOT invoked).
    /// For any SSE event with a null session ID, the event SHALL be discarded
    /// without invoking the callback.
    /// **Validates: Requirements 4.12**
    /// </summary>
    [Property(MaxTest = 100)]
    public void NullSessionId_EventIsDiscarded(NonEmptyString activeSession, NonEmptyString textContent)
    {
        // Arrange
        var activeSessionId = activeSession.Get;
        var text = textContent.Get;

        var sseEvent = new SseEvent
        {
            Type = "message.part.updated",
            SessionId = null,
            Part = new MessagePart { Type = "text", Text = text }
        };

        var handler = new SseStreamMockHandler(BuildSseStream(sseEvent));
        var factory = new SseStreamClientFactory(handler);
        var logger = new Mock<ILogger>();
        var provider = new OpenCodeAgentProvider(factory, logger.Object);

        var callbackInvocations = new List<string>();
        Action<string> callback = line => callbackInvocations.Add(line);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = provider.ConnectAndProcessSseAsync(activeSessionId, callback, cts.Token);
        task.Wait(TimeSpan.FromSeconds(5));

        // Assert
        callbackInvocations.Should().BeEmpty("null session ID events should be discarded");
    }

    /// <summary>
    /// Property 7: Mixed stream — only matching events processed, non-matching discarded.
    /// For any mix of SSE events with matching and non-matching session IDs,
    /// only the matching events SHALL be processed.
    /// **Validates: Requirements 4.12**
    /// </summary>
    [Property(MaxTest = 100)]
    public void MixedSessionIds_OnlyMatchingEventsProcessed(NonEmptyString activeSession, PositiveInt matchCount, PositiveInt nonMatchCount)
    {
        // Constrain to reasonable sizes
        var numMatching = Math.Min(matchCount.Get, 10);
        var numNonMatching = Math.Min(nonMatchCount.Get, 10);

        var activeSessionId = activeSession.Get;
        var nonMatchingSessionId = activeSessionId + "-other";

        // Build a stream with interleaved matching and non-matching events
        var events = new List<SseEvent>();
        for (var i = 0; i < numMatching; i++)
        {
            events.Add(new SseEvent
            {
                Type = "message.part.updated",
                SessionId = activeSessionId,
                Part = new MessagePart { Type = "text", Text = $"match-{i}" }
            });
        }
        for (var i = 0; i < numNonMatching; i++)
        {
            events.Add(new SseEvent
            {
                Type = "message.part.updated",
                SessionId = nonMatchingSessionId,
                Part = new MessagePart { Type = "text", Text = $"nomatch-{i}" }
            });
        }

        var handler = new SseStreamMockHandler(BuildSseStream(events.ToArray()));
        var factory = new SseStreamClientFactory(handler);
        var logger = new Mock<ILogger>();
        var provider = new OpenCodeAgentProvider(factory, logger.Object);

        var callbackInvocations = new List<string>();
        Action<string> callback = line => callbackInvocations.Add(line);

        // Act
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var task = provider.ConnectAndProcessSseAsync(activeSessionId, callback, cts.Token);
        task.Wait(TimeSpan.FromSeconds(5));

        // Assert
        callbackInvocations.Should().HaveCount(numMatching,
            "only matching session ID events should invoke callback");
        callbackInvocations.Should().AllSatisfy(line =>
            line.Should().Contain("match-"));
    }

    /// <summary>
    /// Property 7: Permission auto-approval is NOT triggered for non-matching session IDs.
    /// For any permission.updated event with a non-matching session ID,
    /// no permission approval request SHALL be sent.
    /// **Validates: Requirements 4.12**
    /// </summary>
    [Property(MaxTest = 100)]
    public void NonMatchingSessionId_PermissionNotApproved(
        NonEmptyString activeSession,
        NonEmptyString eventSession,
        NonEmptyString permissionId)
    {
        // Ensure session IDs are different
        if (activeSession.Get == eventSession.Get) return;

        // Arrange
        var activeSessionId = activeSession.Get;
        var eventSessionId = eventSession.Get;

        var sseEvent = new SseEvent
        {
            Type = "permission.updated",
            SessionId = eventSessionId,
            PermissionId = permissionId.Get
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
            "permission approval should not be triggered for non-matching session IDs");
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
/// A mock HTTP handler that returns an SSE stream for GET /event requests
/// and records all other requests (e.g., permission approval POSTs).
/// </summary>
internal sealed class SseStreamMockHandler : HttpMessageHandler
{
    private readonly string _sseContent;
    private readonly List<RecordedRequest> _requests = [];

    /// <summary>All requests sent through this handler, in order.</summary>
    public IReadOnlyList<RecordedRequest> Requests => _requests;

    public SseStreamMockHandler(string sseContent)
    {
        _sseContent = sseContent;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Record the request
        string? body = null;
        if (request.Content is not null)
        {
            body = await request.Content.ReadAsStringAsync(cancellationToken);
        }
        _requests.Add(new RecordedRequest(
            request.Method,
            request.RequestUri,
            body,
            request.Headers.ToDictionary(h => h.Key, h => h.Value.ToList() as IReadOnlyList<string>)));

        var path = request.RequestUri?.PathAndQuery ?? string.Empty;

        if (request.Method == HttpMethod.Get && path.Contains("/event"))
        {
            // Return SSE stream
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(_sseContent));
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(stream)
            };
            response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
            return response;
        }

        // For all other requests (e.g., permission approval POSTs), return 200 OK
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        };
    }
}

/// <summary>
/// A simple IHttpClientFactory that creates HttpClients backed by a SseStreamMockHandler.
/// </summary>
internal sealed class SseStreamClientFactory : IHttpClientFactory
{
    private readonly SseStreamMockHandler _handler;

    public SseStreamClientFactory(SseStreamMockHandler handler)
    {
        _handler = handler;
    }

    public HttpClient CreateClient(string name)
    {
        return new HttpClient(_handler, disposeHandler: false)
        {
            BaseAddress = new Uri(AgentDefaults.OpenCodeBaseUrl)
        };
    }
}
