using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests for <see cref="WorkItemHttpClient"/> response classification and exception wrapping.
/// Resilience (retries, circuit breaker) is handled by the DI-configured handler — these tests
/// verify the client's response parsing and domain exception contracts.
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

    [Fact]
    public async Task GetAssignment_5xx_ThrowsWorkItemFetchException()
    {
        // After resilience handler exhaustion, a 5xx may leak through to the client
        var handler = new FakeHandler(HttpStatusCode.InternalServerError);
        var client = CreateClient(handler);

        var act = () => client.GetAssignmentAsync("wi-5xx", CancellationToken.None);

        await act.Should().ThrowAsync<WorkItemFetchException>()
            .WithMessage("*Unexpected status 500*");
    }

    // ── GetAssignmentAsync — Exception Wrapping ──────────────────────────

    [Fact]
    public async Task GetAssignment_HttpRequestException_WrapsInWorkItemFetchException()
    {
        // Simulates resilience handler exhaustion throwing HttpRequestException
        var handler = new ThrowingHandler(new HttpRequestException("Connection refused"));
        var client = CreateClient(handler);

        var act = () => client.GetAssignmentAsync("wi-net", CancellationToken.None);

        var ex = await act.Should().ThrowAsync<WorkItemFetchException>();
        ex.WithMessage("*All retries exhausted*");
        ex.WithInnerException<HttpRequestException>();
    }

    [Fact]
    public async Task GetAssignment_TimeoutException_WrapsInWorkItemFetchException()
    {
        // Simulates resilience handler timeout (e.g., Polly.Timeout.TimeoutRejectedException)
        var handler = new ThrowingHandler(new TimeoutException("Request timed out"));
        var client = CreateClient(handler);

        var act = () => client.GetAssignmentAsync("wi-timeout", CancellationToken.None);

        var ex = await act.Should().ThrowAsync<WorkItemFetchException>();
        ex.WithMessage("*All retries exhausted*");
        ex.WithInnerException<TimeoutException>();
    }

    [Fact]
    public async Task GetAssignment_CancellationRequested_ThrowsOperationCanceled()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
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

    [Fact]
    public async Task PostStatus_5xx_ThrowsWorkItemStatusPostException()
    {
        // After resilience handler exhaustion, a 5xx may leak through
        var handler = new FakeHandler(HttpStatusCode.InternalServerError);
        var client = CreateClient(handler);
        var update = new WorkItemStatusUpdate { Status = "Failed" };

        var act = () => client.PostStatusAsync("wi-5xx", update, CancellationToken.None);

        await act.Should().ThrowAsync<WorkItemStatusPostException>()
            .WithMessage("*Server error 500*retries exhausted*");
    }

    // ── PostStatusAsync — Exception Wrapping ─────────────────────────────

    [Fact]
    public async Task PostStatus_HttpRequestException_WrapsInWorkItemStatusPostException()
    {
        var handler = new ThrowingHandler(new HttpRequestException("Connection reset"));
        var client = CreateClient(handler);
        var update = new WorkItemStatusUpdate { Status = "Completed" };

        var act = () => client.PostStatusAsync("wi-net", update, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<WorkItemStatusPostException>();
        ex.WithMessage("*All retries exhausted*");
        ex.WithInnerException<HttpRequestException>();
    }

    [Fact]
    public async Task PostStatus_TimeoutException_WrapsInWorkItemStatusPostException()
    {
        var handler = new ThrowingHandler(new TimeoutException("Timed out"));
        var client = CreateClient(handler);
        var update = new WorkItemStatusUpdate { Status = "Completed" };

        var act = () => client.PostStatusAsync("wi-timeout", update, CancellationToken.None);

        var ex = await act.Should().ThrowAsync<WorkItemStatusPostException>();
        ex.WithMessage("*All retries exhausted*");
        ex.WithInnerException<TimeoutException>();
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
    /// Always throws the specified exception. Simulates resilience handler exhaustion.
    /// </summary>
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
