using System.Net;
using CodingAgentWebUI.Agent.OpenCode;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Agent.UnitTests.OpenCode;

/// <summary>
/// Example-based tests for session lifecycle management (Property 8).
/// Covers all 5 paths through EnsureSessionAsync as a state transition table:
/// | Initial State | Server Response | Result |
/// |--------------|-----------------|--------|
/// | No session | POST /session succeeds | Store new session ID |
/// | No session | POST /session fails | Log warning, no session stored |
/// | Valid session | GET /session/:id returns 200 | Keep existing session |
/// | Valid session | GET /session/:id returns 404 | Create new session |
/// | Valid session | GET /session/:id network error | Log warning, keep existing |
/// Feature: opencode-agent-executor
/// </summary>
[Trait("Feature", "opencode-agent-executor")]
[Trait("Property", "8")]
public class OpenCodeSessionLifecycleTests
{
    private const string WorkspacePath = "/tmp/test-workspace";
    private const string OriginalSessionId = "original-session-001";
    private const string NewSessionId = "new-session-002";

    /// <summary>
    /// Path 1: No session exists + POST /session succeeds → Store new session ID.
    /// **Validates: Requirements 5.2**
    /// </summary>
    [Fact]
    public async Task NoSession_PostSessionSucceeds_StoresNewSessionId()
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();

        // Enqueue successful session creation response
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, NewSessionId);

        // Act
        await ctx.Provider.EnsureSessionAsync(WorkspacePath, CancellationToken.None);

        // Assert
        var storedSessionId = await ctx.Provider.GetLatestSessionIdAsync(WorkspacePath, CancellationToken.None);
        Assert.Equal(NewSessionId, storedSessionId);

        // Verify POST /session was called
        Assert.Single(ctx.Handler.Requests);
        Assert.Equal(HttpMethod.Post, ctx.Handler.Requests[0].Method);
        Assert.Equal("/session", ctx.Handler.Requests[0].Path);
    }

    /// <summary>
    /// Path 2: No session exists + POST /session fails → Log warning, no session stored.
    /// **Validates: Requirements 5.7**
    /// </summary>
    [Fact]
    public async Task NoSession_PostSessionFails_LogsWarningAndNoSessionStored()
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();

        // Enqueue a failure response for session creation
        ctx.Handler.EnqueueResponse(HttpStatusCode.InternalServerError, "{\"error\":\"server error\"}");

        // Act
        await ctx.Provider.EnsureSessionAsync(WorkspacePath, CancellationToken.None);

        // Assert — no session should be stored (exception was caught, not rethrown)
        var storedSessionId = await ctx.Provider.GetLatestSessionIdAsync(WorkspacePath, CancellationToken.None);
        Assert.Null(storedSessionId);
    }

    /// <summary>
    /// Path 3: Valid session exists + GET /session/:id returns 200 → Keep existing session.
    /// **Validates: Requirements 5.3**
    /// </summary>
    [Fact]
    public async Task ValidSession_GetReturns200_KeepsExistingSession()
    {
        // Arrange — first establish a session
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, OriginalSessionId);
        await ctx.Provider.EnsureSessionAsync(WorkspacePath, CancellationToken.None);

        // Verify session was created
        var sessionBefore = await ctx.Provider.GetLatestSessionIdAsync(WorkspacePath, CancellationToken.None);
        Assert.Equal(OriginalSessionId, sessionBefore);

        // Now enqueue a 200 OK for session validation (GET /session/:id)
        OpenCodeTestHelpers.EnqueueSessionValid(ctx.Handler);

        // Act — call EnsureSessionAsync again with existing session
        await ctx.Provider.EnsureSessionAsync(WorkspacePath, CancellationToken.None);

        // Assert — session ID should remain unchanged
        var sessionAfter = await ctx.Provider.GetLatestSessionIdAsync(WorkspacePath, CancellationToken.None);
        Assert.Equal(OriginalSessionId, sessionAfter);

        // Verify GET /session/:id was called (second request after initial POST /session)
        var validationRequest = ctx.Handler.Requests[1]; // [0] = POST /session, [1] = GET /session/:id
        Assert.Equal(HttpMethod.Get, validationRequest.Method);
        Assert.Contains(OriginalSessionId, validationRequest.Path);
    }

    /// <summary>
    /// Path 4: Valid session exists + GET /session/:id returns 404 → Create new session.
    /// **Validates: Requirements 5.3**
    /// </summary>
    [Fact]
    public async Task ValidSession_GetReturns404_CreatesNewSession()
    {
        // Arrange — first establish a session
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, OriginalSessionId);
        await ctx.Provider.EnsureSessionAsync(WorkspacePath, CancellationToken.None);

        // Verify session was created
        var sessionBefore = await ctx.Provider.GetLatestSessionIdAsync(WorkspacePath, CancellationToken.None);
        Assert.Equal(OriginalSessionId, sessionBefore);

        // Enqueue 404 for session validation, then success for new session creation
        OpenCodeTestHelpers.EnqueueSessionNotFound(ctx.Handler);
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, NewSessionId);

        // Act — call EnsureSessionAsync again; validation returns 404, so new session is created
        await ctx.Provider.EnsureSessionAsync(WorkspacePath, CancellationToken.None);

        // Assert — session ID should be the new one
        var sessionAfter = await ctx.Provider.GetLatestSessionIdAsync(WorkspacePath, CancellationToken.None);
        Assert.Equal(NewSessionId, sessionAfter);

        // Verify request sequence: [0] POST /session (initial), [1] GET /session/:id (validation), [2] POST /session (new)
        Assert.Equal(3, ctx.Handler.Requests.Count);
        Assert.Equal(HttpMethod.Get, ctx.Handler.Requests[1].Method);
        Assert.Contains(OriginalSessionId, ctx.Handler.Requests[1].Path);
        Assert.Equal(HttpMethod.Post, ctx.Handler.Requests[2].Method);
        Assert.Equal("/session", ctx.Handler.Requests[2].Path);
    }

    /// <summary>
    /// Path 5: Valid session exists + GET /session/:id network error → Log warning, keep existing session.
    /// **Validates: Requirements 5.7**
    /// </summary>
    [Fact]
    public async Task ValidSession_NetworkError_LogsWarningAndKeepsExistingSession()
    {
        // Arrange — use a custom handler that throws on GET /session/:id
        var handler = new NetworkErrorOnValidationHandler(OriginalSessionId);
        var factory = new NetworkErrorClientFactory(handler);
        var loggerMock = new Mock<ILogger>();
        var provider = new OpenCodeAgentProvider(factory, loggerMock.Object);

        // First establish a session via the custom handler
        await provider.EnsureSessionAsync(WorkspacePath, CancellationToken.None);

        // Verify session was created
        var sessionBefore = await provider.GetLatestSessionIdAsync(WorkspacePath, CancellationToken.None);
        Assert.Equal(OriginalSessionId, sessionBefore);

        // Act — call EnsureSessionAsync again; validation will throw network error
        await provider.EnsureSessionAsync(WorkspacePath, CancellationToken.None);

        // Assert — session ID should remain unchanged (kept existing)
        var sessionAfter = await provider.GetLatestSessionIdAsync(WorkspacePath, CancellationToken.None);
        Assert.Equal(OriginalSessionId, sessionAfter);
    }
}

// ── Custom handler for network error testing ────────────────────────────────

/// <summary>
/// A custom HttpMessageHandler that succeeds on the first POST /session call
/// (to establish a session), then throws HttpRequestException on subsequent
/// GET /session/:id calls (to simulate network errors during validation).
/// </summary>
internal sealed class NetworkErrorOnValidationHandler : HttpMessageHandler
{
    private readonly string _sessionId;
    private bool _sessionCreated;

    public NetworkErrorOnValidationHandler(string sessionId)
    {
        _sessionId = sessionId;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.PathAndQuery ?? string.Empty;

        // After session is created, throw on GET /session/:id (validation)
        if (_sessionCreated && request.Method == HttpMethod.Get && path.Contains($"/session/{_sessionId}"))
        {
            throw new HttpRequestException("Simulated network error: connection refused");
        }

        // Session creation — respond with success
        if (path == "/session" && request.Method == HttpMethod.Post)
        {
            _sessionCreated = true;
            var json = System.Text.Json.JsonSerializer.Serialize(
                new CreateSessionResponse { Id = _sessionId },
                OpenCodeJson.JsonOptions);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(json, System.Text.Encoding.UTF8, "application/json")
            });
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>
/// A factory that creates HttpClients backed by a NetworkErrorOnValidationHandler.
/// </summary>
internal sealed class NetworkErrorClientFactory : IHttpClientFactory
{
    private readonly NetworkErrorOnValidationHandler _handler;

    public NetworkErrorClientFactory(NetworkErrorOnValidationHandler handler)
    {
        _handler = handler;
    }

    public HttpClient CreateClient(string name)
    {
        return new HttpClient(_handler, disposeHandler: false)
        {
            BaseAddress = new Uri("http://127.0.0.1:4096")
        };
    }
}
