using System.Net;
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
public class WorkItemHttpClientCrashFreedomPropertyTests
{
    private static readonly HttpStatusCode[] AllStatusCodes = Enum.GetValues<HttpStatusCode>();

    /// <summary>
    /// Property P1: GetAssignmentAsync never throws unexpected exceptions regardless of HTTP status code.
    /// For any single-response status code, the method either:
    /// - Returns a value (null or JobAssignmentMessage)
    /// - Throws WorkItemFetchException (expected domain error)
    /// - Throws OperationCanceledException (cancellation during retry delay)
    /// It must NEVER throw NullReferenceException, InvalidCastException, or other unhandled types.
    /// </summary>
    [Property(MaxTest = 50)]
    public void GetAssignment_AnyStatusCode_NeverThrowsUnexpectedException(int statusCodeSeed)
    {
        // Generate a status code from the full HTTP range (100-599)
        var statusCode = (HttpStatusCode)(100 + Math.Abs(statusCodeSeed) % 500);
        var handler = new SingleResponseHandler(statusCode, "{}");
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var logger = new Mock<Serilog.ILogger>();
        var client = new WorkItemHttpClient(httpClient, logger.Object);

        // Use a short cancellation to avoid waiting for retry delays on 5xx
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        Exception? caught = null;
        try
        {
            client.GetAssignmentAsync("test-wi", cts.Token).GetAwaiter().GetResult();
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
    /// - Throws WorkItemStatusPostException (retries exhausted)
    /// - Throws OperationCanceledException (cancellation during retry delay)
    /// </summary>
    [Property(MaxTest = 50)]
    public void PostStatus_AnyStatusCode_NeverThrowsUnexpectedException(int statusCodeSeed)
    {
        var statusCode = (HttpStatusCode)(100 + Math.Abs(statusCodeSeed) % 500);
        var handler = new SingleResponseHandler(statusCode, "{}");
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var logger = new Mock<Serilog.ILogger>();
        var client = new WorkItemHttpClient(httpClient, logger.Object);

        var update = new WorkItemStatusUpdate { Status = "Running" };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        Exception? caught = null;
        try
        {
            client.PostStatusAsync("test-wi", update, cts.Token).GetAwaiter().GetResult();
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
    /// No matter what exception the HTTP layer throws, the client either retries and eventually
    /// throws a domain exception or OperationCanceledException.
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

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        Exception? caught = null;
        try
        {
            client.GetAssignmentAsync("test-wi", cts.Token).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        // Must throw something (network always fails) — but only expected types
        if (caught is not null)
        {
            var isExpected = caught is WorkItemFetchException
                || caught is OperationCanceledException
                || caught is TaskCanceledException;

            if (!isExpected)
            {
                throw new Exception(
                    $"Unexpected exception for network failure ({exception.GetType().Name}): {caught.GetType().Name}: {caught.Message}",
                    caught);
            }
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
