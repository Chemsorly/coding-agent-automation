using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Xunit;

namespace CodingAgentWebUI.Infrastructure.UnitTests.ApiClient;

/// <summary>
/// HTTP-level unit tests for <see cref="PipelineApiWorkItemClient"/> via WireMock.
/// Covers null-return branches (404, 409, 410), null-coalescing fallbacks, and URL construction.
/// These branches cannot be reached by mocking <see cref="IPipelineApiWorkItemClient"/>.
/// </summary>
public sealed class PipelineApiWorkItemClientTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly PipelineApiWorkItemClient _sut;

    private static readonly JsonSerializerOptions JsonOpts = PipelineJsonOptions.Default;

    public PipelineApiWorkItemClientTests()
    {
        _server = WireMockServer.Start();
        var http = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        _sut = new PipelineApiWorkItemClient(http);
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }

    // ── GetPendingAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetPendingAsync_Success_ReturnsList()
    {
        var items = new[]
        {
            new {
                id = Guid.NewGuid(),
                issueIdentifier = "owner/repo#1",
                issueProviderConfigId = "p1",
                taskType = "Implementation",
                createdAt = DateTimeOffset.UtcNow,
                agentSelector = "kiro",
                retryCount = 0,
                timeoutSeconds = 0
            }
        };
        _server.Given(Request.Create().WithPath("/api/work-items/pending").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(items)));

        var result = await _sut.GetPendingAsync();

        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetPendingAsync_NullResponse_ReturnsEmptyList()
    {
        _server.Given(Request.Create().WithPath("/api/work-items/pending").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("null"));

        var result = await _sut.GetPendingAsync();

        result.Should().BeEmpty();
    }

    /// <summary>
    /// Regression: production crash — orchestrator threw JsonException with
    /// "missing required properties including: 'timeoutSeconds'" when the API pod
    /// was still running the pre-deployment binary that did not yet emit the field.
    /// The HTTP-level client must propagate this as a thrown exception, not silently
    /// return a list with TimeoutSeconds=0.
    /// </summary>
    [Fact]
    public async Task GetPendingAsync_ResponseMissingTimeoutSeconds_ThrowsJsonException()
    {
        // Simulate the old API response: all fields present except timeoutSeconds.
        const string bodyMissingTimeout = """
            [{
                "id": "11111111-1111-1111-1111-111111111111",
                "issueIdentifier": "owner/repo#1",
                "issueProviderConfigId": "github",
                "taskType": 0,
                "createdAt": "2026-08-29T12:00:00+00:00",
                "agentSelector": "kiro",
                "retryCount": 0
            }]
            """;

        _server.Given(Request.Create().WithPath("/api/work-items/pending").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(bodyMissingTimeout));

        var act = () => _sut.GetPendingAsync();

        await act.Should().ThrowAsync<JsonException>(
            "timeoutSeconds is required on PendingWorkItemDto; a response without it must throw, " +
            "not silently default to 0 (which would produce an invalid activeDeadlineSeconds=60 on K8s Jobs)");
    }

    [Fact]
    public async Task GetPendingAsync_TimeoutSecondsNonZero_DeserializesCorrectValue()
    {
        // Regression: previous tests only asserted count, never asserting the TimeoutSeconds value.
        // A bug in the mapping (e.g., always emitting 0 from the API) would have been invisible.
        const string body = """
            [{
                "id": "11111111-1111-1111-1111-111111111111",
                "issueIdentifier": "owner/repo#1",
                "issueProviderConfigId": "github",
                "taskType": 0,
                "createdAt": "2026-08-29T12:00:00+00:00",
                "agentSelector": "kiro",
                "retryCount": 0,
                "timeoutSeconds": 7200
            }]
            """;

        _server.Given(Request.Create().WithPath("/api/work-items/pending").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(body));

        var result = await _sut.GetPendingAsync();

        result.Should().HaveCount(1);
        result[0].TimeoutSeconds.Should().Be(7200,
            "the TimeoutSeconds value from the API response must round-trip to the client " +
            "so the Job Controller can compute the correct activeDeadlineSeconds on the K8s Job");
    }

    // ── ClaimAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task ClaimAsync_Success_ReturnsResponse()
    {
        var workItemId = Guid.NewGuid();
        var response = new
        {
            workItemId,
            runId = "run-1",
            payloadJson = "{}",
            orchestratorUrl = "http://hub:8080"
        };
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/claim").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(response)));

        var result = await _sut.ClaimAsync(workItemId, new ClaimWorkItemRequest
        {
            AssignedAgentId = "agent-1",
            DispatchedAt = DateTimeOffset.UtcNow
        });

        result.Should().NotBeNull();
        result!.RunId.Should().Be("run-1");
    }

    [Fact]
    public async Task ClaimAsync_Conflict_ReturnsNull()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/claim").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(409));

        var result = await _sut.ClaimAsync(workItemId, new ClaimWorkItemRequest
        {
            DispatchedAt = DateTimeOffset.UtcNow
        });

        result.Should().BeNull("409 Conflict must be treated as a contention signal, not an error");
    }

    [Fact]
    public async Task ClaimAsync_NotFound_ThrowsWorkItemNotFoundException()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/claim").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(404));

        var act = () => _sut.ClaimAsync(workItemId, new ClaimWorkItemRequest
        {
            DispatchedAt = DateTimeOffset.UtcNow
        });

        await act.Should().ThrowAsync<WorkItemNotFoundException>()
            .Where(ex => ex.WorkItemId == workItemId,
                "404 during claim indicates a deleted/missing work item — distinct from 409 contention");
    }

    // ── GetAssignmentAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetAssignmentAsync_NotFound_ReturnsNull()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/assignment").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var result = await _sut.GetAssignmentAsync(workItemId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAssignmentAsync_Gone_ReturnsNull()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/assignment").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(410));

        var result = await _sut.GetAssignmentAsync(workItemId);

        result.Should().BeNull("410 Gone means the assignment was already consumed — must return null, not throw");
    }

    // ── GetRetryCountAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetRetryCountAsync_Success_ReturnsCount()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/retry-count").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"retryCount\": 3}"));

        var result = await _sut.GetRetryCountAsync(workItemId);

        result.Should().Be(3);
    }

    [Fact]
    public async Task GetRetryCountAsync_NullResponse_ReturnsZero()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/retry-count").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("null"));

        var result = await _sut.GetRetryCountAsync(workItemId);

        result.Should().Be(0, "null API response must fall back to the default 0 via null-coalescing");
    }

    // ── GetStatusAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_Success_ReturnsStatus()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/status").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"status\": \"Running\"}"));

        var result = await _sut.GetStatusAsync(workItemId);

        result.Should().Be(WorkItemStatus.Running);
    }

    [Fact]
    public async Task GetStatusAsync_NotFound_ReturnsNull()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/status").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var result = await _sut.GetStatusAsync(workItemId);

        result.Should().BeNull();
    }

    // ── GetK8sJobNameAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetK8sJobNameAsync_Success_ReturnsName()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/k8s-job-name").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"jobName\": \"caa-job-abc\"}"));

        var result = await _sut.GetK8sJobNameAsync(workItemId);

        result.Should().Be("caa-job-abc");
    }

    [Fact]
    public async Task GetK8sJobNameAsync_NotFound_ReturnsNull()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/k8s-job-name").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var result = await _sut.GetK8sJobNameAsync(workItemId);

        result.Should().BeNull();
    }

    // ── IsIssueDistributedAsync ────────────────────────────────────────────

    [Fact]
    public async Task IsIssueDistributedAsync_True_ReturnsTrue()
    {
        _server.Given(Request.Create().WithPath("/api/work-items/is-distributed").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"isDistributed\": true}"));

        var result = await _sut.IsIssueDistributedAsync("owner/repo#1", "provider-1");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsIssueDistributedAsync_NullResponse_ReturnsFalse()
    {
        _server.Given(Request.Create().WithPath("/api/work-items/is-distributed").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("null"));

        var result = await _sut.IsIssueDistributedAsync("owner/repo#1", "provider-1");

        result.Should().BeFalse("null response falls back to false via null-coalescing");
    }

    // ── GetActiveAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetActiveAsync_NullResponse_ReturnsEmpty()
    {
        _server.Given(Request.Create().WithPath("/api/work-items/active").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("null"));

        var result = await _sut.GetActiveAsync(60);

        result.Should().BeEmpty();
    }

    // ── GetActiveIdentifiersAsync ─────────────────────────────────────────

    [Fact]
    public async Task GetActiveIdentifiersAsync_NullResponse_ReturnsEmpty()
    {
        _server.Given(Request.Create().WithPath("/api/work-items/active-identifiers").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("null"));

        var result = await _sut.GetActiveIdentifiersAsync();

        result.Should().BeEmpty("null response from active-identifiers must fall back to empty list, not throw");
    }

    // ── GetStalenessAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task GetStalenessAsync_NotFound_ReturnsNull()
    {
        _server.Given(Request.Create().WithPath("/api/work-items/staleness").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var result = await _sut.GetStalenessAsync("owner/repo#1", "p1",
            DateTimeOffset.UtcNow.AddHours(-1));

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetStalenessAsync_Success_ReturnsResult()
    {
        _server.Given(Request.Create().WithPath("/api/work-items/staleness").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{\"hasAgentErrorSince\": true, \"lastSuccessfulCompletion\": null}"));

        var result = await _sut.GetStalenessAsync("owner/repo#1", "p1",
            DateTimeOffset.UtcNow.AddHours(-1));

        result.Should().NotBeNull();
        result!.HasAgentErrorSince.Should().BeTrue();
    }

    // ── PostStatusAsync / RequeueAsync / PostLastProgressAsync ──────────────

    [Fact]
    public async Task PostStatusAsync_SendsToCorrectPath()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/status").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        await _sut.PostStatusAsync(workItemId, new WorkItemStatusUpdate { Status = "Running" });

        _server.LogEntries.Should().HaveCount(1);
    }

    [Fact]
    public async Task RequeueAsync_SendsPostWithNoBody()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/requeue").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        await _sut.RequeueAsync(workItemId);

        _server.LogEntries.Should().HaveCount(1);
    }

    [Fact]
    public async Task RequeueAsync_Conflict_DoesNotThrow()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/requeue").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(409));

        // 409 means item is already Pending/Running/terminal — the requeue intent is satisfied.
        await _sut.RequeueAsync(workItemId); // must not throw

        _server.LogEntries.Should().HaveCount(1, "the request was sent and the 409 was received");
    }

    [Fact]
    public async Task PostLastProgressAsync_SendsToCorrectPath()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/last-progress").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        await _sut.PostLastProgressAsync(workItemId, DateTimeOffset.UtcNow);

        _server.LogEntries.Should().HaveCount(1);
    }

    // ── PostLabelSwapAsync ────────────────────────────────────────────────

    [Fact]
    public async Task PostLabelSwapAsync_SendsToCorrectPath()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/label-swap").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        await _sut.PostLabelSwapAsync(workItemId, "kiro");

        _server.LogEntries.Should().HaveCount(1);
    }
}
