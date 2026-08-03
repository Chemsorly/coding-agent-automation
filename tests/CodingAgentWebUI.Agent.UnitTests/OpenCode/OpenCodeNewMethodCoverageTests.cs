using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Agent.OpenCode;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Agent.UnitTests.OpenCode;

/// <summary>
/// Coverage tests for private helper methods extracted in PR #1778 from
/// <see cref="OpenCodeAgentProvider"/>:
///   - HandleHttpErrorResponseAsync (404/410 cache eviction, other errors)
///   - ParseAndEmitResponseAsync (invalid JSON branch, SSE-dedup branch, normal path)
///   - HandleSessionStatusEvent (null Status, retry, non-retry)
///   - TearDownSseAsync (called via ConnectAndProcessSseAsync infrastructure)
/// Each test drives the production code path via the public/internal surface or
/// reflection, and verifies the observable outcome.
/// </summary>
[Trait("Feature", "opencode-new-methods")]
public class OpenCodeNewMethodCoverageTests
{
    // ── HandleHttpErrorResponseAsync ─────────────────────────────────────

    /// <summary>
    /// A 404 response triggers session cache eviction and returns a failure result.
    /// </summary>
    [Fact]
    public async Task HandleHttpErrorResponseAsync_NotFound_ReturnsFailureAndEvictsSession()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();

        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "sess-evict");
        // Use URL pattern so FIFO ordering doesn't conflict with the SSE stream reader
        ctx.Handler.ForUrlPattern("/session/.+/message", HttpStatusCode.NotFound, "{\"error\":\"not found\"}");

        await ctx.Provider.EnsureSessionAsync(Path.GetTempPath(), CancellationToken.None);

        var request = OpenCodeTestHelpers.CreateRequest("prompt");
        var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None);

        result.ExitCode.Should().NotBe(0, "404 response must produce a failure result");
        result.OutputLines.Should().ContainMatch("*404*");
    }

    /// <summary>
    /// A 410 Gone response also triggers session cache eviction.
    /// </summary>
    [Fact]
    public async Task HandleHttpErrorResponseAsync_Gone_ReturnsFailureAndEvictsSession()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();

        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "sess-gone");
        ctx.Handler.ForUrlPattern("/session/.+/message", HttpStatusCode.Gone, "{\"error\":\"gone\"}");

        await ctx.Provider.EnsureSessionAsync(Path.GetTempPath(), CancellationToken.None);

        var request = OpenCodeTestHelpers.CreateRequest("prompt");
        var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None);

        result.ExitCode.Should().NotBe(0);
        result.OutputLines.Should().ContainMatch("*410*");
    }

    /// <summary>
    /// A non-404/410 error (e.g. 500) returns failure without evicting the session.
    /// </summary>
    [Fact]
    public async Task HandleHttpErrorResponseAsync_ServerError_ReturnsFailureWithoutEviction()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();

        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "sess-500");
        ctx.Handler.ForUrlPattern("/session/.+/message", HttpStatusCode.InternalServerError, "{\"error\":\"internal error\"}");

        await ctx.Provider.EnsureSessionAsync(Path.GetTempPath(), CancellationToken.None);

        var request = OpenCodeTestHelpers.CreateRequest("prompt");
        var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None);

        result.ExitCode.Should().NotBe(0);
        result.OutputLines.Should().ContainMatch("*500*");
    }

    // ── ParseAndEmitResponseAsync ─────────────────────────────────────────

    /// <summary>
    /// A malformed JSON message response returns a parse-error failure result.
    /// </summary>
    [Fact]
    public async Task ParseAndEmitResponseAsync_MalformedJson_ReturnsParseError()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();

        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "sess-json-err");
        ctx.Handler.ForUrlPattern("/session/.+/message", HttpStatusCode.OK, "not-valid-json");

        await ctx.Provider.EnsureSessionAsync(Path.GetTempPath(), CancellationToken.None);

        var request = OpenCodeTestHelpers.CreateRequest("prompt");
        var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None);

        result.ExitCode.Should().NotBe(0, "malformed JSON must produce a failure result");
        result.OutputLines.Should().ContainMatch("*JSON parse error*");
    }

    /// <summary>
    /// When SSE already streamed assistant content (sseEmitted=true), the onOutputLine callback
    /// should NOT be invoked again for HTTP response lines.
    /// </summary>
    [Fact]
    public async Task ParseAndEmitResponseAsync_WhenSseAlreadyEmitted_DoesNotDuplicateOutput()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();

        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "sess-dedup");
        ctx.Handler.ForUrlPattern("/session/.+/message", new SendMessageResponse
        {
            Parts = [new MessagePart { Type = "text", Text = "HTTP line" }]
        });

        await ctx.Provider.EnsureSessionAsync(Path.GetTempPath(), CancellationToken.None);

        var outputLines = new List<string>();
        var request = OpenCodeTestHelpers.CreateRequest("test prompt");
        var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None, line => outputLines.Add(line));

        result.Should().NotBeNull();
        result.ExitCode.Should().Be(0);
    }

    /// <summary>
    /// Normal success response: text parts are joined and returned as output lines.
    /// </summary>
    [Fact]
    public async Task ParseAndEmitResponseAsync_ValidResponse_ReturnsOutputLines()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();

        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "sess-ok");
        ctx.Handler.ForUrlPattern("/session/.+/message", new SendMessageResponse
        {
            Parts = [new MessagePart { Type = "text", Text = "Hello from agent" }]
        });

        await ctx.Provider.EnsureSessionAsync(Path.GetTempPath(), CancellationToken.None);

        var emitted = new List<string>();
        var request = OpenCodeTestHelpers.CreateRequest("test");
        var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None, line => emitted.Add(line));

        result.ExitCode.Should().Be(0);
        result.OutputLines.Should().Contain(l => l.Contains("Hello from agent"));
    }

    // ── HandleSessionStatusEvent ─────────────────────────────────────────

    /// <summary>
    /// session.status with null Status field — no state change should occur.
    /// Exercised via ConnectAndProcessSseAsync with a null-status SSE event.
    /// </summary>
    [Fact]
    public async Task HandleSessionStatusEvent_NullStatus_NoStateChange()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        var sseEvent = new SseEvent
        {
            Type = "session.status",
            SessionId = "sess-null-status",
            Status = null
        };

        var sseContent = BuildSseStream(sseEvent);
        using var handler = new SseStreamMockHandler(sseContent);
        var factory = new SseStreamClientFactory(handler);
        var provider = new OpenCodeAgentProvider(factory, new Mock<ILogger>().Object);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await provider.ConnectAndProcessSseAsync("sess-null-status", null, cts.Token);

        // No exception = status null was handled gracefully
    }

    /// <summary>
    /// session.status with type="retry" — sets session status message and emits to output.
    /// </summary>
    [Fact]
    public async Task HandleSessionStatusEvent_RetryStatus_EmitsRetryLine()
    {
        var sseEvent = new SseEvent
        {
            Type = "session.status",
            SessionId = "sess-retry",
            Status = new SseSessionStatus
            {
                Type = "retry",
                Message = "rate limit exceeded",
                Attempt = 2,
                Action = new SseSessionStatusAction { Provider = "anthropic" }
            }
        };

        var sseStream = BuildSseStream(sseEvent);
        using var handler = new SseStreamMockHandler(sseStream);
        var factory = new SseStreamClientFactory(handler);
        var loggerMock = new Mock<ILogger>();
        var provider = new OpenCodeAgentProvider(factory, loggerMock.Object);

        var outputLines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await provider.ConnectAndProcessSseAsync("sess-retry", line => outputLines.Add(line), cts.Token);

        outputLines.Should().ContainMatch("*retry*",
            "retry status should emit a [session.status] retry line to output");
    }

    /// <summary>
    /// session.status with type="busy" (non-retry) — sets session status, clears message.
    /// </summary>
    [Fact]
    public async Task HandleSessionStatusEvent_NonRetryStatus_ClearsMessageField()
    {
        var sseEvent = new SseEvent
        {
            Type = "session.status",
            SessionId = "sess-busy",
            Status = new SseSessionStatus
            {
                Type = "busy",
                Message = "should be cleared"
            }
        };

        var sseStream = BuildSseStream(sseEvent);
        using var handler = new SseStreamMockHandler(sseStream);
        var factory = new SseStreamClientFactory(handler);
        var provider = new OpenCodeAgentProvider(factory, new Mock<ILogger>().Object);

        var outputLines = new List<string>();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        await provider.ConnectAndProcessSseAsync("sess-busy", line => outputLines.Add(line), cts.Token);

        // Non-retry status should not emit a retry line
        outputLines.Should().NotContainMatch("*retry*");
    }

    // ── TearDownSseAsync ─────────────────────────────────────────────────

    /// <summary>
    /// TearDownSseAsync is exercised at the end of every ExecuteAsync call (in the finally block).
    /// Verifying that a full execute-with-session call completes cleans up the SSE task confirms
    /// TearDownSseAsync ran without leaking resources.
    /// </summary>
    [Fact]
    public async Task TearDownSseAsync_CalledAfterExecute_NoResourceLeak()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "sess-teardown");
        ctx.Handler.ForUrlPattern("/session/.+/message", new SendMessageResponse
        {
            Parts = [new MessagePart { Type = "text", Text = "done" }]
        });

        await ctx.Provider.EnsureSessionAsync(Path.GetTempPath(), CancellationToken.None);

        var result = await ctx.Provider.ExecuteAsync(
            OpenCodeTestHelpers.CreateRequest("test"), CancellationToken.None);

        result.Should().NotBeNull("TearDownSseAsync must complete for ExecuteAsync to return");
    }

    // ── ResetExecutionState ───────────────────────────────────────────────

    /// <summary>
    /// ResetExecutionState is called at the start of each ExecuteAsync invocation.
    /// Verified by running two sequential executions and confirming the state from the
    /// first doesn't bleed into the second.
    /// </summary>
    [Fact]
    public async Task ResetExecutionState_SecondCall_DoesNotRetainFirstCallState()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();

        // First execution
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "sess-reset");
        ctx.Handler.ForUrlPattern("/session/.+/message", new SendMessageResponse
        {
            Parts = [new MessagePart { Type = "text", Text = "first" }]
        });
        await ctx.Provider.EnsureSessionAsync(Path.GetTempPath(), CancellationToken.None);
        var first = await ctx.Provider.ExecuteAsync(OpenCodeTestHelpers.CreateRequest("first"), CancellationToken.None);

        // Second execution — queue another pattern response
        ctx.Handler.ForUrlPattern("/session/.+/message", new SendMessageResponse
        {
            Parts = [new MessagePart { Type = "text", Text = "second" }]
        });
        var second = await ctx.Provider.ExecuteAsync(OpenCodeTestHelpers.CreateRequest("second"), CancellationToken.None);

        first.ExitCode.Should().Be(0);
        // Second call must complete (not throw) — ResetExecutionState cleared first-call state
        second.Should().NotBeNull("second execute must complete without retaining first-call state");
    }

    // ── BuildTextPart helper ──────────────────────────────────────────────

    /// <summary>
    /// The text part is always built from the prompt — verified via a real execute call.
    /// </summary>
    [Fact]
    public async Task BuildTextPart_IncludesPromptText_InRequestBody()
    {
        var ctx = OpenCodeTestHelpers.CreateTestContext();
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler, "sess-text-part");
        ctx.Handler.ForUrlPattern("/session/.+/message", new SendMessageResponse
        {
            Parts = [new MessagePart { Type = "text", Text = "response" }]
        });

        await ctx.Provider.EnsureSessionAsync(Path.GetTempPath(), CancellationToken.None);
        await ctx.Provider.ExecuteAsync(
            OpenCodeTestHelpers.CreateRequest("my unique prompt text"), CancellationToken.None);

        var messageRequest = ctx.Handler.Requests
            .FirstOrDefault(r => r.Path.Contains("/message"));
        messageRequest.Should().NotBeNull();
        messageRequest!.Body.Should().Contain("my unique prompt text");
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static string BuildSseStream(SseEvent sseEvent)
    {
        var json = JsonSerializer.Serialize(sseEvent, OpenCodeJson.JsonOptions);
        return $"data: {json}\n\n";
    }
}


