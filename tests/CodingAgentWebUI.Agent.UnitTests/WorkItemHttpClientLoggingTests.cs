using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using Serilog;
using Serilog.Events;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Verifies that <see cref="WorkItemHttpClient"/> logs error-level messages
/// before throwing exceptions on failure paths.
/// </summary>
public class WorkItemHttpClientLoggingTests
{
    private readonly Mock<ILogger> _mockLogger = new();

    private WorkItemHttpClient CreateClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        return new WorkItemHttpClient(httpClient, _mockLogger.Object);
    }

    [Fact]
    public async Task GetAssignment_RetriesExhausted_LogsErrorBeforeThrowing()
    {
        var handler = new ThrowingHandler(new HttpRequestException("Connection refused"));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<WorkItemFetchException>(
            () => client.GetAssignmentAsync("wi-net", CancellationToken.None));

        // Serilog uses generic overload Error<T>(Exception, string, T)
        _mockLogger.Verify(l => l.Error(
            It.IsAny<Exception>(),
            It.Is<string>(s => s.Contains("retries exhausted", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task GetAssignment_NullDeserialization_LogsErrorBeforeThrowing()
    {
        // Return valid JSON that deserializes to null
        var handler = new FakeHandler(HttpStatusCode.OK, "null");
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<WorkItemFetchException>(
            () => client.GetAssignmentAsync("wi-null", CancellationToken.None));

        // Serilog uses generic overload Error<T>(string, T)
        _mockLogger.Verify(l => l.Error(
            It.Is<string>(s => s.Contains("null", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task GetAssignment_404NotFound_LogsErrorBeforeThrowing()
    {
        var handler = new FakeHandler(HttpStatusCode.NotFound);
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<WorkItemFetchException>(
            () => client.GetAssignmentAsync("wi-missing", CancellationToken.None));

        _mockLogger.Verify(l => l.Error(
            It.Is<string>(s => s.Contains("not found", StringComparison.OrdinalIgnoreCase) || s.Contains("404")),
            It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task GetAssignment_UnexpectedStatus_LogsErrorBeforeThrowing()
    {
        var handler = new FakeHandler(HttpStatusCode.Forbidden);
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<WorkItemFetchException>(
            () => client.GetAssignmentAsync("wi-forbidden", CancellationToken.None));

        _mockLogger.Verify(l => l.Error(
            It.Is<string>(s => s.Contains("Unexpected", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<int>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task PostStatus_RetriesExhausted_LogsErrorBeforeThrowing()
    {
        var handler = new ThrowingHandler(new HttpRequestException("Connection reset"));
        var client = CreateClient(handler);
        var update = new WorkItemStatusUpdate { Status = "Completed" };

        await Assert.ThrowsAsync<WorkItemStatusPostException>(
            () => client.PostStatusAsync("wi-net", update, CancellationToken.None));

        _mockLogger.Verify(l => l.Error(
            It.IsAny<Exception>(),
            It.Is<string>(s => s.Contains("retries exhausted", StringComparison.OrdinalIgnoreCase)),
            It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    [Fact]
    public async Task PostStatus_5xx_LogsErrorBeforeThrowing()
    {
        var handler = new FakeHandler(HttpStatusCode.InternalServerError);
        var client = CreateClient(handler);
        var update = new WorkItemStatusUpdate { Status = "Failed" };

        await Assert.ThrowsAsync<WorkItemStatusPostException>(
            () => client.PostStatusAsync("wi-5xx", update, CancellationToken.None));

        _mockLogger.Verify(l => l.Error(
            It.Is<string>(s => s.Contains("Server error", StringComparison.OrdinalIgnoreCase) || s.Contains("500")),
            It.IsAny<int>(), It.IsAny<string>(), It.IsAny<string>()), Times.Once);
    }

    // ── Test Helpers ─────────────────────────────────────────────────────

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _content;

        public FakeHandler(HttpStatusCode statusCode, string content = "")
        {
            _statusCode = statusCode;
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;
        public ThrowingHandler(Exception exception) => _exception = exception;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            throw _exception;
        }
    }
}
