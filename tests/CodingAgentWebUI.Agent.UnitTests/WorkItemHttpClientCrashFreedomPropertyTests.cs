using System.Net;
using AwesomeAssertions;
using FsCheck;
using FsCheck.Xunit;
using CodingAgentWebUI.Pipeline;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Property-based crash-freedom test for <see cref="WorkItemHttpClient"/>.
/// Verifies that no combination of HTTP status codes causes an unhandled exception.
/// The client should only throw well-defined domain exceptions or OperationCanceledException.
/// </summary>
/// <remarks>
/// After the resilience migration, the client no longer retries internally.
/// All transient failures either throw domain exceptions (wrapping handler exhaustion)
/// or return immediately for non-retryable status codes.
/// </remarks>
public class WorkItemHttpClientCrashFreedomPropertyTests
{
    private static readonly HttpStatusCode[] AllStatusCodes = Enum.GetValues<HttpStatusCode>();

    /// <summary>
    /// Property P1: GetAssignmentAsync never throws unexpected exceptions regardless of HTTP status code.
    /// For any single-response status code, the method either:
    /// - Returns a value (null or JobAssignmentMessage)
    /// - Throws WorkItemFetchException (expected domain error)
    /// - Throws OperationCanceledException (cancellation)
    /// It must NEVER throw NullReferenceException, InvalidCastException, or other unhandled types.
    /// </summary>
    [Property(MaxTest = 20)]
    public void GetAssignment_AnyStatusCode_NeverThrowsUnexpectedException(int statusCodeSeed)
    {
        // Generate a status code from the full HTTP range (100-599)
        var statusCode = (HttpStatusCode)(100 + Math.Abs(statusCodeSeed) % 500);
        var handler = new SingleResponseHandler(statusCode, "{}");
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var logger = new Mock<Serilog.ILogger>();
        var client = new WorkItemHttpClient(httpClient, logger.Object);

        Exception? caught = null;
        try
        {
            client.GetAssignmentAsync("test-wi", CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        // Verify: only expected exception types are thrown
        if (caught is not null)
        {
            var isExpected = caught is WorkItemFetchException
                || caught is OperationCanceledException
                || caught is TaskCanceledException
                || caught is System.Text.Json.JsonException; // malformed response body

            if (!isExpected)
            {
                throw new Exception(
                    $"Unexpected exception for status {(int)statusCode}: {caught.GetType().Name}: {caught.Message}",
                    caught);
            }
        }
    }

    /// <summary>
    /// Property P2: PostStatusAsync never throws unexpected exceptions regardless of HTTP status code.
    /// For any single-response status code, the method either:
    /// - Returns bool (true/false)
    /// - Throws WorkItemStatusPostException (domain error for 5xx after exhaustion)
    /// - Throws OperationCanceledException (cancellation)
    /// </summary>
    [Property(MaxTest = 20)]
    public void PostStatus_AnyStatusCode_NeverThrowsUnexpectedException(int statusCodeSeed)
    {
        var statusCode = (HttpStatusCode)(100 + Math.Abs(statusCodeSeed) % 500);
        var handler = new SingleResponseHandler(statusCode, "{}");
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var logger = new Mock<Serilog.ILogger>();
        var client = new WorkItemHttpClient(httpClient, logger.Object);

        var update = new WorkItemStatusUpdate { Status = "Running" };

        Exception? caught = null;
        try
        {
            client.PostStatusAsync("test-wi", update, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        if (caught is not null)
        {
            var isExpected = caught is WorkItemStatusPostException
                || caught is OperationCanceledException
                || caught is TaskCanceledException;

            if (!isExpected)
            {
                throw new Exception(
                    $"Unexpected exception for status {(int)statusCode}: {caught.GetType().Name}: {caught.Message}",
                    caught);
            }
        }
    }

    /// <summary>
    /// Property P3: GetAssignmentAsync handles network failures gracefully.
    /// When the HTTP layer throws, the client wraps in WorkItemFetchException.
    /// </summary>
    [Property(MaxTest = 20)]
    public void GetAssignment_NetworkFailure_NeverThrowsUnexpectedException(int exceptionTypeSeed)
    {
        var exceptions = new Exception[]
        {
            new HttpRequestException("Connection refused"),
            new HttpRequestException("DNS resolution failed"),
            new HttpRequestException("Network stream broken", new IOException("broken pipe")),
        };
        var exception = exceptions[Math.Abs(exceptionTypeSeed) % exceptions.Length];
        var handler = new ThrowingResponseHandler(exception);
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var logger = new Mock<Serilog.ILogger>();
        var client = new WorkItemHttpClient(httpClient, logger.Object);

        Exception? caught = null;
        try
        {
            client.GetAssignmentAsync("test-wi", CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        // Must throw something (network always fails) — but only expected types
        caught.Should().NotBeNull("network failure should always throw");

        var isExpected = caught is WorkItemFetchException
            || caught is OperationCanceledException
            || caught is TaskCanceledException;

        if (!isExpected)
        {
            throw new Exception(
                $"Unexpected exception for network failure ({exception.GetType().Name}): {caught!.GetType().Name}: {caught.Message}",
                caught);
        }
    }

    // ── Test Handlers ────────────────────────────────────────────────────

    private sealed class SingleResponseHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _content;

        public SingleResponseHandler(HttpStatusCode statusCode, string content)
        {
            _statusCode = statusCode;
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ThrowingResponseHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingResponseHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            throw _exception;
        }
    }
}
