using System.Diagnostics;
using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using OpenTelemetry;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

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

    // ── Traceparent Header Injection ─────────────────────────────────────

    [Fact]
    public async Task GetAssignment_WithAmbientActivity_InjectsTraceparentHeader()
    {
        // Use a real TracerProvider so ActivitySource.StartActivity produces a real span
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateEmpty())
            .AddSource("test.traceparent")
            .Build();

        var activitySource = new ActivitySource("test.traceparent");

        HttpRequestMessage? capturedRequest = null;
        var handler = new CapturingHandler(HttpStatusCode.Gone,
            req => capturedRequest = req);

        var client = CreateClient(handler);

        using var activity = activitySource.StartActivity("TestParent", ActivityKind.Internal);
        activity.Should().NotBeNull("TracerProvider must be active for StartActivity to return non-null");

        await client.GetAssignmentAsync("wi-traceparent", CancellationToken.None);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Headers.Should().ContainKey("traceparent");
        var traceparentValue = capturedRequest.Headers.GetValues("traceparent").FirstOrDefault();
        traceparentValue.Should().NotBeNullOrEmpty();
        // W3C traceparent format: 00-{traceId}-{spanId}-{flags}
        traceparentValue.Should().MatchRegex(@"^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$");
        // TODO [WARNING]: This only verifies the W3C format, not that the injected header actually
        // corresponds to the ambient activity. A future change that injects a hardcoded or randomly
        // generated traceparent would still pass this assertion. To close the gap, add:
        //   traceparentValue.Should().Contain(activity!.TraceId.ToHexString());
    }

    [Fact]
    public async Task PostStatus_WithAmbientActivity_InjectsTraceparentHeader()
    {
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateEmpty())
            .AddSource("test.traceparent.post")
            .Build();

        var activitySource = new ActivitySource("test.traceparent.post");

        HttpRequestMessage? capturedRequest = null;
        var handler = new CapturingHandler(HttpStatusCode.OK,
            req => capturedRequest = req);

        var client = CreateClient(handler);
        var update = new WorkItemStatusUpdate { Status = "Running" };

        using var activity = activitySource.StartActivity("TestParent", ActivityKind.Internal);
        activity.Should().NotBeNull("TracerProvider must be active for StartActivity to return non-null");

        await client.PostStatusAsync("wi-traceparent", update, CancellationToken.None);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Headers.Should().ContainKey("traceparent");
        var traceparentValue = capturedRequest.Headers.GetValues("traceparent").FirstOrDefault();
        traceparentValue.Should().NotBeNullOrEmpty();
        traceparentValue.Should().MatchRegex(@"^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$");
        // TODO [WARNING]: This only verifies the W3C format, not that the injected header actually
        // corresponds to the ambient activity. A future change that injects a hardcoded or randomly
        // generated traceparent would still pass this assertion. To close the gap, add:
        //   traceparentValue.Should().Contain(activity!.TraceId.ToHexString());
    }

    [Fact]
    public async Task GetAssignment_WithoutAmbientActivity_DoesNotInjectTraceparentHeader()
    {
        // No TracerProvider registered → Activity.Current is null → no header injected
        // Ensures backward compatibility: API must handle missing traceparent gracefully
        HttpRequestMessage? capturedRequest = null;
        var handler = new CapturingHandler(HttpStatusCode.Gone,
            req => capturedRequest = req);

        var client = CreateClient(handler);

        // Ensure no ambient activity
        // TODO [WARNING]: This precondition assert is fragile in parallel test execution — if another
        // test leaks an ambient activity across async continuations, this may fail non-deterministically.
        // Consider assigning Activity.Current = null or using an explicit scope as setup instead.
        Activity.Current.Should().BeNull("test must run without ambient activity");

        await client.GetAssignmentAsync("wi-no-trace", CancellationToken.None);

        capturedRequest.Should().NotBeNull();
        // No traceparent header when there's no ambient activity
        capturedRequest!.Headers.Contains("traceparent").Should().BeFalse();
    }

    [Fact]
    public async Task PostStatus_WithoutAmbientActivity_DoesNotInjectTraceparentHeader()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new CapturingHandler(HttpStatusCode.OK,
            req => capturedRequest = req);

        var client = CreateClient(handler);
        var update = new WorkItemStatusUpdate { Status = "Running" };

        // TODO [WARNING]: This precondition assert is fragile in parallel test execution — if another
        // test leaks an ambient activity across async continuations, this may fail non-deterministically.
        // Consider assigning Activity.Current = null or using an explicit scope as setup instead.
        Activity.Current.Should().BeNull("test must run without ambient activity");

        await client.PostStatusAsync("wi-no-trace", update, CancellationToken.None);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Headers.Contains("traceparent").Should().BeFalse();
    }

    [Fact]
    public async Task PostLabelSwap_WithAmbientActivity_InjectsTraceparentHeader()
    {
        using var tracerProvider = Sdk.CreateTracerProviderBuilder()
            .SetResourceBuilder(ResourceBuilder.CreateEmpty())
            .AddSource("test.traceparent.labelswap")
            .Build();

        var activitySource = new ActivitySource("test.traceparent.labelswap");

        HttpRequestMessage? capturedRequest = null;
        var handler = new CapturingHandler(HttpStatusCode.OK,
            req => capturedRequest = req);

        var client = CreateClient(handler);

        using var activity = activitySource.StartActivity("TestParent", ActivityKind.Internal);
        activity.Should().NotBeNull("TracerProvider must be active for StartActivity to return non-null");

        await client.PostLabelSwapAsync("wi-traceparent", "agent:in-progress", CancellationToken.None);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Headers.Should().ContainKey("traceparent");
        var traceparentValue = capturedRequest.Headers.GetValues("traceparent").FirstOrDefault();
        traceparentValue.Should().NotBeNullOrEmpty();
        // W3C traceparent format: 00-{traceId}-{spanId}-{flags}
        traceparentValue.Should().MatchRegex(@"^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$");
        // TODO [WARNING]: This only verifies the W3C format, not that the injected header actually
        // corresponds to the ambient activity. A future change that injects a hardcoded or randomly
        // generated traceparent would still pass this assertion. To close the gap, add:
        //   traceparentValue.Should().Contain(activity!.TraceId.ToHexString());
    }

    [Fact]
    public async Task PostLabelSwap_WithoutAmbientActivity_DoesNotInjectTraceparentHeader()
    {
        // No TracerProvider registered → Activity.Current is null → no header injected
        // Ensures backward compatibility: API must handle missing traceparent gracefully
        HttpRequestMessage? capturedRequest = null;
        var handler = new CapturingHandler(HttpStatusCode.OK,
            req => capturedRequest = req);

        var client = CreateClient(handler);

        // TODO [WARNING]: This precondition assert is fragile in parallel test execution — if another
        // test leaks an ambient activity across async continuations, this may fail non-deterministically.
        // Consider assigning Activity.Current = null or using an explicit scope as setup instead.
        Activity.Current.Should().BeNull("test must run without ambient activity");

        await client.PostLabelSwapAsync("wi-no-trace", "agent:in-progress", CancellationToken.None);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Headers.Contains("traceparent").Should().BeFalse();
    }

    // ── PostLabelSwapAsync — Response Classification ─────────────────────

    [Fact]
    public async Task PostLabelSwap_200OK_ReturnsTrue()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        var client = CreateClient(handler);

        var result = await client.PostLabelSwapAsync("wi-1", "agent:in-progress", CancellationToken.None);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task PostLabelSwap_404NotFound_ReturnsFalse()
    {
        var handler = new FakeHandler(HttpStatusCode.NotFound);
        var client = CreateClient(handler);

        var result = await client.PostLabelSwapAsync("wi-gone", "agent:in-progress", CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task PostLabelSwap_UnexpectedClientError_ReturnsFalse()
    {
        var handler = new FakeHandler(HttpStatusCode.Forbidden);
        var client = CreateClient(handler);

        var result = await client.PostLabelSwapAsync("wi-1", "agent:in-progress", CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task PostLabelSwap_5xx_ThrowsWorkItemLabelSwapException()
    {
        // After resilience handler exhaustion, a 5xx may leak through
        var handler = new FakeHandler(HttpStatusCode.InternalServerError);
        var client = CreateClient(handler);

        var act = () => client.PostLabelSwapAsync("wi-5xx", "agent:in-progress", CancellationToken.None);

        await act.Should().ThrowAsync<WorkItemLabelSwapException>()
            .WithMessage("*Server error 500*retries exhausted*");
    }

    // ── PostLabelSwapAsync — Exception Wrapping ───────────────────────────

    [Fact]
    public async Task PostLabelSwap_HttpRequestException_WrapsInWorkItemLabelSwapException()
    {
        var handler = new ThrowingHandler(new HttpRequestException("Connection reset"));
        var client = CreateClient(handler);

        var act = () => client.PostLabelSwapAsync("wi-net", "agent:in-progress", CancellationToken.None);

        var ex = await act.Should().ThrowAsync<WorkItemLabelSwapException>();
        ex.WithMessage("*All retries exhausted*");
        ex.WithInnerException<HttpRequestException>();
    }

    [Fact]
    public async Task PostLabelSwap_TimeoutException_WrapsInWorkItemLabelSwapException()
    {
        var handler = new ThrowingHandler(new TimeoutException("Timed out"));
        var client = CreateClient(handler);

        var act = () => client.PostLabelSwapAsync("wi-timeout", "agent:in-progress", CancellationToken.None);

        var ex = await act.Should().ThrowAsync<WorkItemLabelSwapException>();
        ex.WithMessage("*All retries exhausted*");
        ex.WithInnerException<TimeoutException>();
    }

    // ── PostLabelSwapAsync — Guard Clauses ────────────────────────────────

    [Fact]
    public async Task PostLabelSwap_NullWorkItemId_Throws()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        var client = CreateClient(handler);

        var act = () => client.PostLabelSwapAsync(null!, "agent:in-progress", CancellationToken.None);

        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task PostLabelSwap_NullLabel_Throws()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        var client = CreateClient(handler);

        var act = () => client.PostLabelSwapAsync("wi-1", null!, CancellationToken.None);

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
    /// Captures the outgoing HttpRequestMessage for header inspection.
    /// </summary>
    /// <remarks>
    /// TODO [WARNING]: This handler retains a reference to the <see cref="HttpRequestMessage"/>
    /// after <c>SendAsync</c> returns. The caller's <c>using var request</c> disposes the message
    /// before the test reads headers via <c>capturedRequest.Headers</c>. In current .NET runtimes
    /// <c>HttpRequestHeaders</c> does not throw after the owning message is disposed, but this is
    /// not guaranteed by the IDisposable contract. For a safer pattern, capture the header snapshot
    /// (e.g., <c>capturedRequest.Headers.GetValues("traceparent").FirstOrDefault()</c>) inside the
    /// callback rather than retaining the full message reference.
    /// </remarks>
    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _statusCode;
        private readonly Action<HttpRequestMessage> _capture;

        public CapturingHandler(HttpStatusCode statusCode, Action<HttpRequestMessage> capture)
        {
            _statusCode = statusCode;
            _capture = capture;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            _capture(request);
            var response = new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent("", System.Text.Encoding.UTF8, "application/json")
            };
            return Task.FromResult(response);
        }
    }

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
