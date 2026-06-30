using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests for <see cref="WorkItemHttpClient"/> retry logic, error classification,
/// and status code handling for both GET assignment and POST status endpoints.
/// </summary>
public class WorkItemHttpClientTests
{
    private readonly Mock<Serilog.ILogger> _mockLogger = new();

    private WorkItemHttpClient CreateClient(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        return new WorkItemHttpClient(httpClient, _mockLogger.Object);
    }

    // ── Constructor Guard Clauses ────────────────────────────────────────

    [Fact]
    public void Constructor_NullHttpClient_Throws()
    {
        var act = () => new WorkItemHttpClient(null!, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new WorkItemHttpClient(new HttpClient(), null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── GetAssignmentAsync — Happy Path ──────────────────────────────────

    [Fact]
    public async Task GetAssignment_200OK_ReturnsDeserializedMessage()
    {
        var expected = CreateMinimalAssignment("job-1", "owner/repo#42");
        var json = JsonSerializer.Serialize(expected, PipelineJsonOptions.Default);
        var handler = new FakeHandler(HttpStatusCode.OK, json);

        var client = CreateClient(handler);
        var result = await client.GetAssignmentAsync("wi-1", CancellationToken.None);

        result.Should().NotBeNull();
        result!.JobId.Should().Be("job-1");
        result.IssueIdentifier.Should().Be("owner/repo#42");
    }

    [Fact]
    public async Task GetAssignment_410Gone_ReturnsNull()
    {
        var handler = new FakeHandler(HttpStatusCode.Gone);
        var client = CreateClient(handler);

        var result = await client.GetAssignmentAsync("wi-terminal", CancellationToken.None);

        result.Should().BeNull();
    }

    // ── GetAssignmentAsync — Error Classification ────────────────────────

    [Fact]
    public async Task GetAssignment_404NotFound_ThrowsWorkItemFetchException()
    {
        var handler = new FakeHandler(HttpStatusCode.NotFound);
        var client = CreateClient(handler);

        var act = () => client.GetAssignmentAsync("wi-missing", CancellationToken.None);

        await act.Should().ThrowAsync<WorkItemFetchException>()
            .WithMessage("*not found*404*");
    }

    [Fact]
    public async Task GetAssignment_UnexpectedClientError_ThrowsImmediately()
    {
        var handler = new FakeHandler(HttpStatusCode.Forbidden);
        var client = CreateClient(handler);

        var act = () => client.GetAssignmentAsync("wi-forbidden", CancellationToken.None);

        await act.Should().ThrowAsync<WorkItemFetchException>()
            .WithMessage("*Unexpected status 403*");
    }

    // ── GetAssignmentAsync — Retry Behavior ──────────────────────────────

    [Fact]
    public async Task GetAssignment_5xx_RetriesAtLeastOnce_ThenCancelled()
    {
        // Verifies retry loop starts on 5xx. Uses cancellation to avoid waiting full backoff.
        var handler = new FakeHandler(HttpStatusCode.InternalServerError);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var client = CreateClient(handler);

        var act = () => client.GetAssignmentAsync("wi-retry", cts.Token);

        // Should be cancelled during a retry delay, proving retry was attempted
        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.CallCount.Should().BeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task GetAssignment_5xxThenSuccess_RetriesAndReturns()
    {
        var expected = CreateMinimalAssignment("job-retry", "owner/repo#99");
        var json = JsonSerializer.Serialize(expected, PipelineJsonOptions.Default);
        var handler = new SequenceHandler(
            (HttpStatusCode.InternalServerError, ""),
            (HttpStatusCode.OK, json));

        var client = CreateClient(handler);
        // First attempt: 500 → waits 2s → second attempt: 200 OK
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await client.GetAssignmentAsync("wi-retry", cts.Token);

        result.Should().NotBeNull();
        result!.JobId.Should().Be("job-retry");
        handler.CallCount.Should().Be(2);
    }

    [Fact]
    public async Task GetAssignment_NetworkErrorThenSuccess_Retries()
    {
        var expected = CreateMinimalAssignment("job-net", "owner/repo#7");
        var json = JsonSerializer.Serialize(expected, PipelineJsonOptions.Default);
        var handler = new ExceptionThenSuccessHandler(
            new HttpRequestException("connection refused"),
            HttpStatusCode.OK, json);

        var client = CreateClient(handler);
        // Note: first attempt fails (network error), then 2s delay, then second attempt succeeds
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var result = await client.GetAssignmentAsync("wi-net", cts.Token);

        result.Should().NotBeNull();
        result!.JobId.Should().Be("job-net");
    }

    [Fact]
    public async Task GetAssignment_CancellationRequested_ThrowsOperationCanceled()
    {
        var handler = new FakeHandler(HttpStatusCode.InternalServerError);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = CreateClient(handler);

        var act = () => client.GetAssignmentAsync("wi-cancel", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task GetAssignment_PreCancelledToken_ThrowsImmediately()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var client = CreateClient(handler);

        var act = () => client.GetAssignmentAsync("wi-1", cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.CallCount.Should().Be(0);
    }

    // ── PostStatusAsync — Happy Path ─────────────────────────────────────

    [Fact]
    public async Task PostStatus_200OK_ReturnsTrue()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        var client = CreateClient(handler);
        var update = new WorkItemStatusUpdate { Status = "Completed" };

        var result = await client.PostStatusAsync("wi-1", update, CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task PostStatus_400BadRequest_ReturnsFalse()
    {
        var handler = new FakeHandler(HttpStatusCode.BadRequest);
        var client = CreateClient(handler);
        var update = new WorkItemStatusUpdate { Status = "Invalid" };

        var result = await client.PostStatusAsync("wi-1", update, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task PostStatus_404NotFound_ReturnsFalse()
    {
        var handler = new FakeHandler(HttpStatusCode.NotFound);
        var client = CreateClient(handler);
        var update = new WorkItemStatusUpdate { Status = "Completed" };

        var result = await client.PostStatusAsync("wi-gone", update, CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task PostStatus_UnexpectedClientError_ReturnsFalse()
    {
        var handler = new FakeHandler(HttpStatusCode.Forbidden);
        var client = CreateClient(handler);
        var update = new WorkItemStatusUpdate { Status = "Running" };

        var result = await client.PostStatusAsync("wi-1", update, CancellationToken.None);

        result.Should().BeFalse();
    }

    // ── PostStatusAsync — Retry Behavior ─────────────────────────────────

    [Fact]
    public async Task PostStatus_5xx_RetriesAtLeastOnce_ThenCancelled()
    {
        // Verifies retry loop starts on 5xx. Uses cancellation to avoid waiting full backoff.
        var handler = new FakeHandler(HttpStatusCode.InternalServerError);
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        var client = CreateClient(handler);
        var update = new WorkItemStatusUpdate { Status = "Failed" };

        var act = () => client.PostStatusAsync("wi-retry", update, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        handler.CallCount.Should().BeGreaterThanOrEqualTo(1);
    }

    // ── Guard Clause Tests ───────────────────────────────────────────────

    [Fact]
    public async Task GetAssignment_NullWorkItemId_Throws()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        var client = CreateClient(handler);

        var act = () => client.GetAssignmentAsync(null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task PostStatus_NullWorkItemId_Throws()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        var client = CreateClient(handler);
        var update = new WorkItemStatusUpdate { Status = "Running" };

        var act = () => client.PostStatusAsync(null!, update, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task PostStatus_NullUpdate_Throws()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        var client = CreateClient(handler);

        var act = () => client.PostStatusAsync("wi-1", null!, CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── Test Helpers ─────────────────────────────────────────────────────

    private static JobAssignmentMessage CreateMinimalAssignment(string jobId, string issueId) => new()
    {
        JobId = jobId,
        IssueIdentifier = issueId,
        IssueDetail = new IssueDetail { Identifier = issueId, Title = "Test", Description = "", Labels = [] },
        ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
        RepoProviderConfigId = "repo-1",
        AgentProviderConfigId = "agent-1",
        PipelineConfiguration = new PipelineConfiguration(),
        ProviderConfigs = [],
        ReviewerConfigs = [],
        QualityGateConfigs = [],
        IssueComments = [],
        InitiatedBy = "test"
    };

    /// <summary>
    /// Always returns the same status code. Tracks call count.
    /// </summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly string _content;
        public int CallCount { get; private set; }

        public FakeHandler(HttpStatusCode statusCode, string content = "")
        {
            _statusCode = statusCode;
            _content = content;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_content, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// Returns responses from a sequence, repeating the last one if exhausted.
    /// </summary>
    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly List<(HttpStatusCode Status, string Content)> _responses;
        private int _index;
        public int CallCount { get; private set; }

        public SequenceHandler(params (HttpStatusCode, string)[] responses)
        {
            _responses = responses.ToList();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            CallCount++;
            var (status, content) = _index < _responses.Count ? _responses[_index++] : _responses[^1];
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(content, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// Throws an exception on first call, then returns success.
    /// </summary>
    private sealed class ExceptionThenSuccessHandler : HttpMessageHandler
    {
        private readonly Exception _exception;
        private readonly HttpStatusCode _successStatus;
        private readonly string _successContent;
        private bool _threw;

        public ExceptionThenSuccessHandler(Exception exception, HttpStatusCode successStatus, string successContent)
        {
            _exception = exception;
            _successStatus = successStatus;
            _successContent = successContent;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (!_threw)
            {
                _threw = true;
                throw _exception;
            }
            var response = new HttpResponseMessage(_successStatus)
            {
                Content = new StringContent(_successContent, System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }
}
