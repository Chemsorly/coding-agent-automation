using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using CodingAgent.Agent.OpenCode;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Agent;
using KiroCliLib.Core;

namespace CodingAgent.Agent.UnitTests.OpenCode;

/// <summary>
/// Property-based tests for exception containment (Property 11).
/// Verifies that for any exception type thrown during ExecuteAsync (other than
/// OperationCanceledException from caller cancellation), the exception SHALL be caught
/// and returned as an AgentResult with a non-zero exit code and descriptive error in OutputLines.
/// OperationCanceledException from caller cancellation SHALL propagate after best-effort abort.
/// Feature: opencode-agent-executor
/// </summary>
[Trait("Feature", "opencode-agent-executor")]
[Trait("Property", "11")]
public class OpenCodeExceptionContainmentPropertyTests
{
    /// <summary>
    /// Property 11 (containment case): For any non-OperationCanceledException thrown during
    /// ExecuteAsync, the exception SHALL be caught and returned as an AgentResult with
    /// ExitCode != 0 and descriptive error in OutputLines.
    /// **Validates: Requirements 10.3**
    /// </summary>
    [Property(Arbitrary = [typeof(ExceptionContainmentArbitrary)], MaxTest = 20)]
    public async Task AnyNonCancellationException_IsCaughtAndReturnedAsAgentResult(ThrowingExceptionOutcome outcome)
    {
        // Arrange
        var handler = new ThrowingHandler(outcome.ExceptionToThrow, throwOnSessionCreate: false);
        var factory = new ThrowingClientFactory(handler);
        var provider = new OpenCodeAgentProvider(factory, null);

        var request = new AgentRequest
        {
            Prompt = "test prompt",
            WorkspacePath = Path.GetTempPath(),
            Timeout = TimeSpan.FromSeconds(30)
        };

        // Act
        var result = await provider.ExecuteAsync(request, CancellationToken.None);

        // Assert — exception is contained, not propagated
        Assert.NotEqual(ExitCodes.Success, result.ExitCode);
        Assert.NotEmpty(result.OutputLines);
    }

    /// <summary>
    /// Property 11 (containment on session create): For any non-OperationCanceledException
    /// thrown during session creation, the exception SHALL be caught and returned as an
    /// AgentResult with ExitCode != 0.
    /// **Validates: Requirements 10.3**
    /// </summary>
    [Property(Arbitrary = [typeof(ExceptionContainmentArbitrary)], MaxTest = 20)]
    public async Task AnyNonCancellationException_DuringSessionCreate_IsCaughtAndReturnedAsAgentResult(ThrowingExceptionOutcome outcome)
    {
        // Arrange
        var handler = new ThrowingHandler(outcome.ExceptionToThrow, throwOnSessionCreate: true);
        var factory = new ThrowingClientFactory(handler);
        var provider = new OpenCodeAgentProvider(factory, null);

        var request = new AgentRequest
        {
            Prompt = "test prompt",
            WorkspacePath = Path.GetTempPath(),
            Timeout = TimeSpan.FromSeconds(30)
        };

        // Act
        var result = await provider.ExecuteAsync(request, CancellationToken.None);

        // Assert — exception is contained, not propagated
        Assert.NotEqual(ExitCodes.Success, result.ExitCode);
    }

    /// <summary>
    /// Property 11 (caller cancellation): OperationCanceledException from caller cancellation
    /// SHALL propagate after best-effort abort.
    /// **Validates: Requirements 3.10**
    /// </summary>
    [Property(Arbitrary = [typeof(ExceptionContainmentArbitrary)], MaxTest = 20)]
    public async Task CallerCancellation_PropagatesOperationCanceledException(CallerCancellationOutcome outcome)
    {
        // Arrange — the handler holds the message request open until the caller cancels
        var handler = new CancellationTestHandler();
        var factory = new ThrowingClientFactory(handler);
        var provider = new OpenCodeAgentProvider(factory, null);

        using var cts = new CancellationTokenSource();

        var request = new AgentRequest
        {
            Prompt = "test prompt",
            WorkspacePath = Path.GetTempPath(),
            Timeout = TimeSpan.FromMinutes(5) // long timeout so it doesn't interfere
        };

        var execution = provider.ExecuteAsync(request, cts.Token);

        // Start the cancel delay only once the provider is blocked in the message request, so the
        // cancel lands mid-request however slowly the runner schedules session setup. A fixed delay
        // from the start raced a message request that completed on its own 5s later: on a starved
        // CI runner the cancel callback ran after it (FsCheck: "No exception was thrown", DelayMs=2206).
        var first = await Task.WhenAny(handler.MessageRequestStarted, execution).WaitAsync(TimeSpan.FromSeconds(60));
        Assert.True(first == handler.MessageRequestStarted,
            "ExecuteAsync finished before its message request reached the handler");
        cts.CancelAfter(TimeSpan.FromMilliseconds(outcome.DelayBeforeCancelMs));

        // Act & Assert — OperationCanceledException should propagate
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution);
    }
}

// ── Input models ────────────────────────────────────────────────────────────

/// <summary>
/// Represents an outcome where a specific exception type is thrown during ExecuteAsync.
/// </summary>
public sealed class ThrowingExceptionOutcome
{
    public required Exception ExceptionToThrow { get; init; }

    public override string ToString() =>
        $"ThrowingExceptionOutcome({ExceptionToThrow.GetType().Name}: {ExceptionToThrow.Message})";
}

/// <summary>
/// Represents a caller cancellation scenario with a delay before cancellation fires.
/// </summary>
public sealed class CallerCancellationOutcome
{
    /// <summary>Delay in milliseconds between the message request starting and the caller cancelling the token.</summary>
    public required int DelayBeforeCancelMs { get; init; }

    public override string ToString() =>
        $"CallerCancellationOutcome(DelayMs={DelayBeforeCancelMs})";
}

// ── Arbitrary generators ────────────────────────────────────────────────────

/// <summary>
/// FsCheck arbitrary generators for exception containment property tests.
/// Generates various exception types that could be thrown during HTTP operations.
/// </summary>
public static class ExceptionContainmentArbitrary
{
    /// <summary>
    /// Pool of exception instances that could occur during HTTP operations.
    /// Excludes OperationCanceledException (tested separately).
    /// </summary>
    private static readonly Exception[] ExceptionPool =
    [
        new HttpRequestException("Connection refused"),
        new HttpRequestException("Name resolution failed"),
        new HttpRequestException("SSL connection error"),
        new IOException("Network stream closed unexpectedly"),
        new IOException("Unable to read data from the transport connection"),
        new TimeoutException("The operation has timed out"),
        new InvalidOperationException("Invalid state"),
        new InvalidOperationException("The request was already sent"),
        new JsonException("Unexpected character"),
        new JsonException("Invalid JSON token"),
        new ArgumentException("Invalid URI format"),
        new NotSupportedException("Protocol not supported"),
        new ObjectDisposedException("HttpClient"),
        new TaskCanceledException("A task was canceled.", new TimeoutException()),
        new FormatException("Input string was not in a correct format"),
        new UriFormatException("Invalid URI: The hostname could not be parsed"),
        new OutOfMemoryException("Insufficient memory"),
        new SocketException(10061), // Connection refused
        new NullReferenceException("Object reference not set"),
        new IndexOutOfRangeException("Index was outside the bounds")
    ];

    /// <summary>
    /// Generates various exception types that could occur during ExecuteAsync.
    /// Excludes OperationCanceledException (tested separately).
    /// </summary>
    public static Arbitrary<ThrowingExceptionOutcome> ThrowingExceptionOutcomeArb()
    {
        var gen =
            from idx in FsCheck.Fluent.Gen.Choose(0, ExceptionPool.Length - 1)
            select new ThrowingExceptionOutcome { ExceptionToThrow = ExceptionPool[idx] };

        return gen.ToArbitrary();
    }

    /// <summary>
    /// Generates the delay between the message request reaching the handler and the caller's
    /// cancel. The test starts the delay only after the request arrives, so every value cancels
    /// mid-request.
    /// </summary>
    public static Arbitrary<CallerCancellationOutcome> CallerCancellationOutcomeArb()
    {
        var gen =
            from delayMs in FsCheck.Fluent.Gen.Choose(0, 500)
            select new CallerCancellationOutcome { DelayBeforeCancelMs = delayMs };

        return gen.ToArbitrary();
    }
}

// ── Custom handlers for exception testing ───────────────────────────────────

/// <summary>
/// A custom HttpMessageHandler that throws a specified exception when the message
/// endpoint is called. Used for testing exception containment in ExecuteAsync.
/// </summary>
internal sealed class ThrowingHandler : HttpMessageHandler
{
    private readonly Exception _exceptionToThrow;
    private readonly bool _throwOnSessionCreate;

    public ThrowingHandler(Exception exceptionToThrow, bool throwOnSessionCreate)
    {
        _exceptionToThrow = exceptionToThrow;
        _throwOnSessionCreate = throwOnSessionCreate;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.PathAndQuery ?? string.Empty;

        // Session creation
        if (path == "/session" && request.Method == HttpMethod.Post)
        {
            if (_throwOnSessionCreate)
            {
                throw _exceptionToThrow;
            }

            var json = JsonSerializer.Serialize(
                new CreateSessionResponse { Id = "test-session-001" },
                OpenCodeJson.JsonOptions);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }

        // SSE endpoint — return empty response (won't be read since message throws)
        if (path == "/event")
        {
            // Return a response that will be cancelled when SSE CTS fires
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty)
            });
        }

        // Message endpoint — throw the exception
        if (path.Contains("/message"))
        {
            throw _exceptionToThrow;
        }

        // Abort endpoint — respond OK (best-effort abort)
        if (path.Contains("/abort"))
        {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }

        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

/// <summary>
/// A custom HttpMessageHandler that holds the message request open until the caller cancels.
/// Used for testing OperationCanceledException propagation.
/// </summary>
internal sealed class CancellationTestHandler : HttpMessageHandler
{
    private readonly TaskCompletionSource _messageRequestStarted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes when the provider's message request reaches this handler.</summary>
    public Task MessageRequestStarted => _messageRequestStarted.Task;

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri?.PathAndQuery ?? string.Empty;

        // Session creation — respond immediately
        if (path == "/session" && request.Method == HttpMethod.Post)
        {
            var json = JsonSerializer.Serialize(
                new CreateSessionResponse { Id = "test-session-001" },
                OpenCodeJson.JsonOptions);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }

        // SSE endpoint — stay open until cancelled
        if (path == "/event")
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Expected
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        // Message endpoint — only the caller's cancellation ends this request
        if (path.Contains("/message"))
        {
            _messageRequestStarted.TrySetResult();
            await Task.Delay(TimeSpan.FromMinutes(1), cancellationToken);
            // Bounded so a regression fails the test instead of hanging the run.
            throw new InvalidOperationException("Caller cancellation never reached the message request");
        }

        // Abort endpoint — respond immediately
        if (path.Contains("/abort"))
        {
            return new HttpResponseMessage(HttpStatusCode.OK);
        }

        return new HttpResponseMessage(HttpStatusCode.NotFound);
    }
}

/// <summary>
/// A factory that creates HttpClients backed by a ThrowingHandler or CancellationTestHandler.
/// </summary>
internal sealed class ThrowingClientFactory : IHttpClientFactory
{
    private readonly HttpMessageHandler _handler;

    public ThrowingClientFactory(HttpMessageHandler handler)
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
