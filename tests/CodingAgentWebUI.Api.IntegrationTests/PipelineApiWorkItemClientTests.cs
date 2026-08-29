using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace CodingAgentWebUI.Api.IntegrationTests;

/// <summary>
/// Unit tests for <see cref="PipelineApiWorkItemClient"/> using WireMock.Net to stub HTTP responses.
/// Each test gets its own WireMock server on a random port; no live API required.
/// </summary>
public sealed class PipelineApiWorkItemClientTests : IAsyncDisposable
{
    private readonly WireMockServer _server;
    private readonly PipelineApiWorkItemClient _client;

    public PipelineApiWorkItemClientTests()
    {
        _server = WireMockServer.Start();
        var http = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        _client = new PipelineApiWorkItemClient(http);
    }

    public ValueTask DisposeAsync()
    {
        _server.Stop();
        _server.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string Serialize<T>(T obj) => JsonSerializer.Serialize(obj, PipelineJsonOptions.Default);

    private static PendingWorkItemDto MakePending(string identifier = "owner/repo#1") => new()
    {
        Id = Guid.NewGuid(),
        IssueIdentifier = identifier,
        IssueProviderConfigId = "prov-1",
        TaskType = WorkItemTaskType.Implementation,
        CreatedAt = DateTimeOffset.UtcNow,
        AgentSelector = "kiro",
        RetryCount = 0
    };

    private static ActiveWorkItemDto MakeActive(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Status = WorkItemStatus.Running,
        DispatchedAt = DateTimeOffset.UtcNow,
        AgentSelector = "kiro",
        IssueIdentifier = "owner/repo#5"
    };

    // ── GetPendingAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetPendingAsync_Returns_DeserializedList()
    {
        var items = new List<PendingWorkItemDto> { MakePending("issue#1") };
        _server.Given(Request.Create()
                .WithPath("/api/work-items/pending")
                .WithParam("maxResults", "50")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(Serialize(items)));

        var result = await _client.GetPendingAsync(50);

        result.Should().HaveCount(1);
        result[0].IssueIdentifier.Should().Be("issue#1");
    }

    [Fact]
    public async Task GetPendingAsync_NullResponse_ReturnsEmptyList()
    {
        _server.Given(Request.Create().WithPath("/api/work-items/pending").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("null"));

        var result = await _client.GetPendingAsync();

        result.Should().BeEmpty();
    }

    // ── ClaimAsync ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ClaimAsync_Success_ReturnsResponse()
    {
        var workItemId = Guid.NewGuid();
        var claim = new WorkItemClaimResponse
        {
            WorkItemId = workItemId,
            RunId = "run-abc",
            PayloadJson = "{}",
            OrchestratorUrl = "http://localhost/hub"
        };
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/claim").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(Serialize(claim)));

        var result = await _client.ClaimAsync(workItemId,
            new ClaimWorkItemRequest { AssignedAgentId = "agent-1", DispatchedAt = DateTimeOffset.UtcNow });

        result.Should().NotBeNull();
        result!.WorkItemId.Should().Be(workItemId);
        result.RunId.Should().Be("run-abc");
    }

    [Fact]
    public async Task ClaimAsync_Conflict_ReturnsNull()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/claim").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(409));

        var result = await _client.ClaimAsync(workItemId,
            new ClaimWorkItemRequest { DispatchedAt = DateTimeOffset.UtcNow });

        result.Should().BeNull();
    }

    [Fact]
    public async Task ClaimAsync_NotFound_ThrowsWorkItemNotFoundException()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/claim").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(404));

        var act = () => _client.ClaimAsync(workItemId,
            new ClaimWorkItemRequest { DispatchedAt = DateTimeOffset.UtcNow });

        await act.Should().ThrowAsync<WorkItemNotFoundException>()
            .Where(ex => ex.WorkItemId == workItemId,
                "404 during claim indicates a deleted/missing work item — distinct from 409 contention");
    }

    // ── GetAssignmentAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetAssignmentAsync_NotFound_ReturnsNull()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/assignment").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var result = await _client.GetAssignmentAsync(workItemId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAssignmentAsync_Gone_ReturnsNull()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/assignment").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(410));

        var result = await _client.GetAssignmentAsync(workItemId);

        result.Should().BeNull();
    }

    // ── GetStatusAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_NotFound_ReturnsNull()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/status").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var result = await _client.GetStatusAsync(workItemId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetStatusAsync_Found_ReturnsStatus()
    {
        var workItemId = Guid.NewGuid();
        // PipelineJsonOptions uses JsonStringEnumConverter — enum serialized as string
        var response = new { status = "Running" };
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/status").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(response)));

        var result = await _client.GetStatusAsync(workItemId);

        result.Should().Be(WorkItemStatus.Running);
    }

    // ── GetK8sJobNameAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetK8sJobNameAsync_NotFound_ReturnsNull()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/k8s-job-name").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var result = await _client.GetK8sJobNameAsync(workItemId);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetK8sJobNameAsync_Found_ReturnsJobName()
    {
        var workItemId = Guid.NewGuid();
        var response = new { jobName = "k8s-job-abc" };
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/k8s-job-name").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(response)));

        var result = await _client.GetK8sJobNameAsync(workItemId);

        result.Should().Be("k8s-job-abc");
    }

    // ── CreateAsync ────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_Returns_Guid()
    {
        var newId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath("/api/work-items").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(newId)));

        var request = new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier("owner/repo#1"),
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "kiro",
            TimeoutSeconds = 3600
        };

        var result = await _client.CreateAsync(request);

        result.Should().Be(newId);
        _server.LogEntries.Should().Contain(e =>
            e.RequestMessage!.Method == "POST" &&
            e.RequestMessage.Path == "/api/work-items");
    }

    /// <summary>
    /// Test D — client-side idempotency key header.
    /// <see cref="PipelineApiWorkItemClient.CreateAsync"/> must send <c>X-Idempotency-Key: {RunId}</c>
    /// when <c>request.RunId</c> is non-empty.
    /// </summary>
    [Fact]
    public async Task CreateAsync_SendsIdempotencyKeyHeader_WhenRunIdProvided()
    {
        var runId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath("/api/work-items").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(201)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(runId)));

        var request = new JobDistributionRequest
        {
            RunId = runId.ToString(),
            IssueIdentifier = new IssueIdentifier("owner/repo#1"),
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "kiro",
            TimeoutSeconds = 3600
        };

        await _client.CreateAsync(request);

#pragma warning disable CS8602
        _server.LogEntries.Should().Contain(e =>
            e.RequestMessage!.Headers.ContainsKey("X-Idempotency-Key") &&
            e.RequestMessage!.Headers["X-Idempotency-Key"].Contains(runId.ToString()),
            "CreateAsync must send X-Idempotency-Key header matching the RunId for Polly retry safety");
#pragma warning restore CS8602
    }

    [Fact]
    public async Task CreateAsync_DoesNotSendIdempotencyKeyHeader_WhenRunIdEmpty()
    {
        var newId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath("/api/work-items").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(201)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(newId)));

        var request = new JobDistributionRequest
        {
            // RunId intentionally omitted — server generates a new GUID; no stable idempotency key
            IssueIdentifier = new IssueIdentifier("owner/repo#2"),
            IssueProviderConfigId = "prov-1",
            RepoProviderConfigId = "repo-1",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "kiro",
            TimeoutSeconds = 3600
        };

        await _client.CreateAsync(request);

#pragma warning disable CS8602
        // TODO [WARNING]: LogEntries accumulates across all calls on this server instance for its
        // lifetime. xUnit creates a new PipelineApiWorkItemClientTests instance per test (fresh
        // server each time), so isolation is currently safe. If this class ever becomes a shared
        // fixture, add _server.ResetLogEntries() before this assertion and scope it to
        // e.RequestMessage.Path == "/api/work-items" to prevent false failures from accumulated entries.
        _server.LogEntries.Should().NotContain(e =>
            e.RequestMessage!.Headers.ContainsKey("X-Idempotency-Key"),
            "X-Idempotency-Key must not be sent when RunId is absent — no stable key exists");
#pragma warning restore CS8602
    }

    // ── GetActiveIdentifiersAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GetActiveIdentifiersAsync_Returns_TupleList()
    {
        var items = new[]
        {
            new { issueIdentifier = "issue#1", issueProviderConfigId = "prov1" },
            new { issueIdentifier = "issue#2", issueProviderConfigId = "prov2" }
        };
        _server.Given(Request.Create().WithPath("/api/work-items/active-identifiers").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(items)));

        var result = await _client.GetActiveIdentifiersAsync();

        result.Should().HaveCount(2);
        result[0].IssueIdentifier.Should().Be("issue#1");
        result[0].IssueProviderConfigId.Should().Be("prov1");
        result[1].IssueIdentifier.Should().Be("issue#2");
    }

    [Fact]
    public async Task GetActiveIdentifiersAsync_NullResponse_ReturnsEmptyList()
    {
        _server.Given(Request.Create().WithPath("/api/work-items/active-identifiers").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("null"));

        var result = await _client.GetActiveIdentifiersAsync();

        result.Should().BeEmpty();
    }

    // ── GetRetryCountAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task GetRetryCountAsync_Returns_Count()
    {
        var workItemId = Guid.NewGuid();
        var response = new { retryCount = 3 };
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/retry-count").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(response)));

        var result = await _client.GetRetryCountAsync(workItemId);

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

        var result = await _client.GetRetryCountAsync(workItemId);

        result.Should().Be(0);
    }

    // ── PostStatusAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task PostStatusAsync_SendsPostToCorrectPath()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/status").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{}"));

        await _client.PostStatusAsync(workItemId,
            new WorkItemStatusUpdate { Status = "Running" });

        _server.LogEntries.Should().Contain(e =>
            e.RequestMessage!.Method == "POST" &&
            e.RequestMessage.Path == $"/api/work-items/{workItemId}/status");
    }

    // ── RequeueAsync ───────────────────────────────────────────────────────────

    [Fact]
    public async Task RequeueAsync_SendsPostToCorrectPath()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/requeue").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{}"));

        await _client.RequeueAsync(workItemId);

        _server.LogEntries.Should().Contain(e =>
            e.RequestMessage!.Method == "POST" &&
            e.RequestMessage.Path == $"/api/work-items/{workItemId}/requeue");
    }

    // ── GetActiveAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetActiveAsync_Returns_DeserializedList()
    {
        var items = new List<ActiveWorkItemDto> { MakeActive() };
        _server.Given(Request.Create()
                .WithPath("/api/work-items/active")
                .WithParam("olderThanSeconds", "60")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(Serialize(items)));

        var result = await _client.GetActiveAsync(60);

        result.Should().HaveCount(1);
        result[0].Status.Should().Be(WorkItemStatus.Running);
    }

    [Fact]
    public async Task GetActiveAsync_NullResponse_ReturnsEmptyList()
    {
        _server.Given(Request.Create().WithPath("/api/work-items/active").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("null"));

        var result = await _client.GetActiveAsync(60);

        result.Should().BeEmpty();
    }

    // ── IsIssueDistributedAsync ────────────────────────────────────────────────

    [Fact]
    public async Task IsIssueDistributedAsync_ReturnsTrue_WhenDistributed()
    {
        _server.Given(Request.Create()
                .WithPath("/api/work-items/is-distributed")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(new { isDistributed = true })));

        var result = await _client.IsIssueDistributedAsync("issue#1", "prov-1");

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsIssueDistributedAsync_ReturnsFalse_WhenNotDistributed()
    {
        _server.Given(Request.Create()
                .WithPath("/api/work-items/is-distributed")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(new { isDistributed = false })));

        var result = await _client.IsIssueDistributedAsync("issue#2", "prov-1");

        result.Should().BeFalse();
    }

    // ── GetStalenessAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task GetStalenessAsync_NotFound_ReturnsNull()
    {
        _server.Given(Request.Create().WithPath("/api/work-items/staleness").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var result = await _client.GetStalenessAsync("issue#1", "prov-1", DateTimeOffset.UtcNow);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetStalenessAsync_Found_ReturnsResult()
    {
        var staleness = new WorkItemStalenessResult
        {
            HasAgentErrorSince = true,
            LastSuccessfulCompletion = null
        };
        _server.Given(Request.Create().WithPath("/api/work-items/staleness").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(Serialize(staleness)));

        var result = await _client.GetStalenessAsync("issue#1", "prov-1", DateTimeOffset.UtcNow.AddHours(-1));

        result.Should().NotBeNull();
        result!.HasAgentErrorSince.Should().BeTrue();
    }

    // ── PostLabelSwapAsync ─────────────────────────────────────────────────────

    [Fact]
    public async Task PostLabelSwapAsync_SendsPostWithLabel()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/label-swap").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{}"));

        await _client.PostLabelSwapAsync(workItemId, "in-progress");

        var entry = _server.LogEntries.First(e =>
            e.RequestMessage!.Method == "POST" &&
            e.RequestMessage.Path == $"/api/work-items/{workItemId}/label-swap");
        entry.RequestMessage!.Body.Should().Contain("in-progress");
    }

    // ── PostLastProgressAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task PostLastProgressAsync_SendsPostWithTimestamp()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/last-progress").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{}"));

        await _client.PostLastProgressAsync(workItemId, DateTimeOffset.UtcNow);

        _server.LogEntries.Should().Contain(e =>
            e.RequestMessage!.Method == "POST" &&
            e.RequestMessage.Path == $"/api/work-items/{workItemId}/last-progress");
    }
}
