using System.Net;
using System.Text;
using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Agent.OpenCode;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent.UnitTests.OpenCode;

/// <summary>
/// Property-based tests for exit code mapping (Property 1).
/// Verifies that for any execution outcome — successful HTTP 2xx response, HTTP 4xx/5xx error,
/// timeout expiration, malformed JSON response, or session creation failure — the AgentResult.ExitCode
/// SHALL be 0 for success, 124 for timeout, and 1 for all other failures.
/// Feature: opencode-agent-executor
/// </summary>
[Trait("Feature", "opencode-agent-executor")]
[Trait("Property", "1")]
public class OpenCodeExitCodePropertyTests
{
    /// <summary>
    /// Property 1 (success case): For any successful HTTP 2xx response with valid JSON,
    /// the ExitCode SHALL be 0.
    /// **Validates: Requirements 1.3, 3.5**
    /// </summary>
    [Property(Arbitrary = [typeof(ExitCodeArbitrary)], MaxTest = 100)]
    public async void SuccessfulResponse_ReturnsExitCodeZero(SuccessOutcome outcome)
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();

        // Enqueue session creation response
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler);

        // Enqueue successful message response with valid JSON
        var messageResponse = new SendMessageResponse { Parts = outcome.Parts };
        ctx.Handler.ForUrlPattern("/session/.+/message", messageResponse);

        var request = OpenCodeTestHelpers.CreateRequest(prompt: outcome.Prompt);

        // Act
        var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(ExitCodes.Success, result.ExitCode);
    }

    /// <summary>
    /// Property 1 (HTTP error case): For any HTTP 4xx/5xx error response,
    /// the ExitCode SHALL be 1 (GeneralFailure).
    /// **Validates: Requirements 3.6, 10.2**
    /// </summary>
    [Property(Arbitrary = [typeof(ExitCodeArbitrary)], MaxTest = 100)]
    public async void HttpErrorResponse_ReturnsExitCodeOne(HttpErrorOutcome outcome)
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();

        // Enqueue session creation response
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler);

        // Enqueue HTTP error response for the message endpoint
        ctx.Handler.ForUrlPattern(
            "/session/.+/message",
            outcome.StatusCode,
            outcome.ResponseBody);

        var request = OpenCodeTestHelpers.CreateRequest(prompt: "test prompt");

        // Act
        var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(ExitCodes.GeneralFailure, result.ExitCode);
        Assert.NotEmpty(result.OutputLines);
    }

    /// <summary>
    /// Property 1 (timeout case): When the AgentRequest.Timeout elapses during execution,
    /// the ExitCode SHALL be 124 (Timeout).
    /// **Validates: Requirements 3.9**
    /// </summary>
    [Property(Arbitrary = [typeof(ExitCodeArbitrary)], MaxTest = 20)]
    public async void TimeoutExpiration_ReturnsExitCode124(TimeoutOutcome outcome)
    {
        // Arrange
        // Use a custom handler that delays the message endpoint response
        // longer than the configured timeout, causing the timeout to fire.
        var delayHandler = new DelayingHandler(outcome.DelayMs);
        var delayFactory = new DelayingClientFactory(delayHandler);
        var provider = new OpenCodeAgentProvider(delayFactory, null);

        // Enqueue session creation (fast)
        delayHandler.EnqueueSessionCreation("test-session-001");

        // The message endpoint will delay, causing timeout
        var request = OpenCodeTestHelpers.CreateRequest(
            prompt: "test prompt",
            timeout: TimeSpan.FromMilliseconds(outcome.TimeoutMs));

        // Act
        var result = await provider.ExecuteAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(ExitCodes.Timeout, result.ExitCode);
    }

    /// <summary>
    /// Property 1 (malformed JSON case): When the server returns a 200 response with
    /// malformed JSON, the ExitCode SHALL be 1 (GeneralFailure).
    /// **Validates: Requirements 10.2**
    /// </summary>
    [Property(Arbitrary = [typeof(ExitCodeArbitrary)], MaxTest = 100)]
    public async void MalformedJsonResponse_ReturnsExitCodeOne(MalformedJsonOutcome outcome)
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();

        // Enqueue session creation response
        OpenCodeTestHelpers.EnqueueSessionCreated(ctx.Handler);

        // Enqueue a 200 OK response with malformed JSON for the message endpoint
        ctx.Handler.ForUrlPattern(
            "/session/.+/message",
            HttpStatusCode.OK,
            outcome.MalformedJson);

        var request = OpenCodeTestHelpers.CreateRequest(prompt: "test prompt");

        // Act
        var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(ExitCodes.GeneralFailure, result.ExitCode);
        Assert.NotEmpty(result.OutputLines);
    }

    /// <summary>
    /// Property 1 (session failure case): When session creation fails (HTTP error from POST /session),
    /// the ExitCode SHALL be 1 (GeneralFailure).
    /// **Validates: Requirements 3.3**
    /// </summary>
    [Property(Arbitrary = [typeof(ExitCodeArbitrary)], MaxTest = 100)]
    public async void SessionCreationFailure_ReturnsExitCodeOne(SessionFailureOutcome outcome)
    {
        // Arrange
        var ctx = OpenCodeTestHelpers.CreateTestContext();

        // Enqueue a failure response for session creation (POST /session)
        ctx.Handler.EnqueueResponse(outcome.StatusCode, outcome.ResponseBody);

        var request = OpenCodeTestHelpers.CreateRequest(prompt: "test prompt");

        // Act
        var result = await ctx.Provider.ExecuteAsync(request, CancellationToken.None);

        // Assert
        Assert.Equal(ExitCodes.GeneralFailure, result.ExitCode);
    }
}

// ── Input models ────────────────────────────────────────────────────────────

/// <summary>
/// Represents a successful execution outcome with valid response parts.
/// </summary>
public sealed class SuccessOutcome
{
    public required string Prompt { get; init; }
    public required IReadOnlyList<MessagePart> Parts { get; init; }

    public override string ToString() =>
        $"SuccessOutcome(Prompt.Length={Prompt.Length}, Parts={Parts.Count})";
}

/// <summary>
/// Represents an HTTP error outcome (4xx/5xx status code).
/// </summary>
public sealed class HttpErrorOutcome
{
    public required HttpStatusCode StatusCode { get; init; }
    public required string ResponseBody { get; init; }

    public override string ToString() =>
        $"HttpErrorOutcome(Status={(int)StatusCode}, Body.Length={ResponseBody.Length})";
}

/// <summary>
/// Represents a timeout outcome where the request exceeds the configured timeout.
/// </summary>
public sealed class TimeoutOutcome
{
    /// <summary>Timeout in milliseconds (very short to trigger timeout).</summary>
    public required int TimeoutMs { get; init; }

    /// <summary>Delay in milliseconds (longer than timeout to ensure timeout fires).</summary>
    public required int DelayMs { get; init; }

    public override string ToString() =>
        $"TimeoutOutcome(TimeoutMs={TimeoutMs}, DelayMs={DelayMs})";
}

/// <summary>
/// Represents a malformed JSON response outcome.
/// </summary>
public sealed class MalformedJsonOutcome
{
    public required string MalformedJson { get; init; }

    public override string ToString() =>
        $"MalformedJsonOutcome(Json.Length={MalformedJson.Length})";
}

/// <summary>
/// Represents a session creation failure outcome.
/// </summary>
public sealed class SessionFailureOutcome
{
    public required HttpStatusCode StatusCode { get; init; }
    public required string ResponseBody { get; init; }

    public override string ToString() =>
        $"SessionFailureOutcome(Status={(int)StatusCode}, Body.Length={ResponseBody.Length})";
}

// ── Arbitrary generators ────────────────────────────────────────────────────

/// <summary>
/// FsCheck arbitrary generators for exit code property tests.
/// Generates realistic execution outcomes for each failure category.
/// </summary>
public static class ExitCodeArbitrary
{
    private static readonly char[] SafeChars =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 .,;:!?-_=+()[]{}/<>@#$%^&*~"
            .ToCharArray();

    private static Gen<string> SafeTextGen()
    {
        return
            from len in Gen.Choose(1, 60)
            from chars in Gen.ArrayOf(Gen.Elements(SafeChars), len)
            select new string(chars);
    }

    /// <summary>
    /// Generates a successful outcome with valid response parts.
    /// </summary>
    public static Arbitrary<SuccessOutcome> SuccessOutcomeArb()
    {
        var gen =
            from prompt in SafeTextGen()
            from partCount in Gen.Choose(1, 5)
            from texts in Gen.ArrayOf(SafeTextGen(), partCount)
            let parts = texts.Select(t => new MessagePart { Type = "text", Text = t }).ToList()
            select new SuccessOutcome { Prompt = prompt, Parts = parts };

        return gen.ToArbitrary();
    }

    /// <summary>
    /// Generates HTTP error outcomes with 4xx/5xx status codes.
    /// </summary>
    public static Arbitrary<HttpErrorOutcome> HttpErrorOutcomeArb()
    {
        var errorCodes = new[]
        {
            HttpStatusCode.BadRequest,           // 400
            HttpStatusCode.Unauthorized,         // 401
            HttpStatusCode.Forbidden,            // 403
            HttpStatusCode.NotFound,             // 404
            HttpStatusCode.MethodNotAllowed,     // 405
            HttpStatusCode.Conflict,             // 409
            HttpStatusCode.UnprocessableEntity,  // 422
            HttpStatusCode.TooManyRequests,      // 429
            HttpStatusCode.InternalServerError,  // 500
            HttpStatusCode.BadGateway,           // 502
            HttpStatusCode.ServiceUnavailable,   // 503
            HttpStatusCode.GatewayTimeout        // 504
        };

        var gen =
            from statusCode in Gen.Elements(errorCodes)
            from body in SafeTextGen()
            select new HttpErrorOutcome { StatusCode = statusCode, ResponseBody = body };

        return gen.ToArbitrary();
    }

    /// <summary>
    /// Generates timeout outcomes with realistic timeout values.
    /// The delay is infinite (handler blocks forever) — the CancellationTokenSource in
    /// production code fires deterministically after TimeoutMs.
    /// </summary>
    public static Arbitrary<TimeoutOutcome> TimeoutOutcomeArb()
    {
        var gen =
            from timeoutMs in Gen.Choose(50, 500)
            select new TimeoutOutcome
            {
                TimeoutMs = timeoutMs,
                DelayMs = Timeout.Infinite // Handler blocks forever; timeout CTS cancels it
            };

        return gen.ToArbitrary();
    }

    /// <summary>
    /// Generates malformed JSON strings that will fail deserialization.
    /// </summary>
    public static Arbitrary<MalformedJsonOutcome> MalformedJsonOutcomeArb()
    {
        var malformedJsons = new[]
        {
            "not json at all",
            "{invalid}",
            "{ \"parts\": \"not-an-array\" }",
            "{ \"parts\": [{ \"type\": 123 }] }",
            "<html>not json</html>",
            "undefined",
            "{ broken: json, }",
            "{'single': 'quotes'}",
            "[[[",
            "{ \"parts\": [{ \"type\": \"text\", \"text\": }] }"
        };

        var gen =
            from json in Gen.Elements(malformedJsons)
            select new MalformedJsonOutcome { MalformedJson = json };

        return gen.ToArbitrary();
    }

    /// <summary>
    /// Generates session creation failure outcomes with error status codes.
    /// </summary>
    public static Arbitrary<SessionFailureOutcome> SessionFailureOutcomeArb()
    {
        var errorCodes = new[]
        {
            HttpStatusCode.BadRequest,
            HttpStatusCode.Unauthorized,
            HttpStatusCode.Forbidden,
            HttpStatusCode.InternalServerError,
            HttpStatusCode.ServiceUnavailable,
            HttpStatusCode.GatewayTimeout
        };

        var gen =
            from statusCode in Gen.Elements(errorCodes)
            from body in SafeTextGen()
            select new SessionFailureOutcome { StatusCode = statusCode, ResponseBody = body };

        return gen.ToArbitrary();
    }
}

// ── Custom handler for timeout testing ──────────────────────────────────────

/// <summary>
/// A custom HttpMessageHandler that delays responses to the message endpoint
/// while responding immediately to session creation. Used for timeout testing.
/// </summary>
internal sealed class DelayingHandler : HttpMessageHandler
{
    private readonly int _delayMs;

    public DelayingHandler(int delayMs)
    {
        _delayMs = delayMs;
    }

    private string? _sessionId;

    /// <summary>
    /// Configures the handler to return a successful session creation response.
    /// </summary>
    public void EnqueueSessionCreation(string sessionId)
    {
        _sessionId = sessionId;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.PathAndQuery ?? string.Empty;

        // Session creation — respond immediately
        if (path == "/session" && request.Method == HttpMethod.Post)
        {
            var json = JsonSerializer.Serialize(
                new CreateSessionResponse { Id = _sessionId ?? "test-session" },
                OpenCodeJson.JsonOptions);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }

        // SSE endpoint — return empty stream that stays open until cancelled
        if (path == "/event")
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Expected when SSE is cancelled
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        // Message endpoint — delay to trigger timeout
        if (path.Contains("/message"))
        {
            await Task.Delay(_delayMs, cancellationToken);
            // If we get here, the delay completed without cancellation (shouldn't happen in timeout tests)
            var responseJson = JsonSerializer.Serialize(
                new SendMessageResponse { Parts = [new MessagePart { Type = "text", Text = "response" }] },
                OpenCodeJson.JsonOptions);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responseJson, Encoding.UTF8, "application/json")
            };
        }

        // Abort endpoint — respond immediately (best-effort abort after timeout)
        if (path.Contains("/abort"))
        {
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

/// <summary>
/// A factory that creates HttpClients backed by a DelayingHandler.
/// Used for timeout testing where we need the message endpoint to delay.
/// </summary>
internal sealed class DelayingClientFactory : IHttpClientFactory
{
    private readonly DelayingHandler _handler;

    public DelayingClientFactory(DelayingHandler handler)
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
