using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Moq;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using System.Text;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Api.IntegrationTests;

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
        RetryCount = 0,
        TimeoutSeconds = 0
    };

    private static ActiveWorkItemDto MakeActive(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Status = WorkItemStatus.Running,
        DispatchedAt = DateTimeOffset.UtcNow,
        AgentSelector = "kiro",
        IssueIdentifier = "owner/repo#5"
    };

    private static (IPipelineApiWorkItemClient Client, StubHandler Handler) CreateWithStubHandler()
    {
        var handler = new StubHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new PipelineApiWorkItemClient(http);
        return (client, handler);
    }

    private static HttpResponseMessage JsonResponse(object value, HttpStatusCode status = HttpStatusCode.OK)
    {
        var json = JsonSerializer.Serialize(value, PipelineJsonOptions.Default);
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage NullJson(HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent("null", Encoding.UTF8, "application/json") };

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

        var act = () => _client.GetPendingAsync();

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

        var result = await _client.GetPendingAsync();

        result.Should().HaveCount(1);
        result[0].TimeoutSeconds.Should().Be(7200,
            "the TimeoutSeconds value from the API response must round-trip to the client " +
            "so the Job Controller can compute the correct activeDeadlineSeconds on the K8s Job");
    }

    // ── ClaimAsync ─────────────────────────────────────────────────────────────

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

    // ── CreateAsync ────────────────────────────────────────────────────────────

    private static JobDistributionRequest CreateRequest() => new()
    {
        IssueIdentifier = new IssueIdentifier("owner/repo#9"),
        IssueProviderConfigId = "prov-1",
        RepoProviderConfigId = "repo-1",
        InitiatedBy = "test",
        TaskType = WorkItemTaskType.Implementation,
        TimeoutSeconds = 3600
    };

    [Fact]
    public async Task CreateAsync_A422WithProblemDetails_ThrowsTheRefusalWithItsDetail()
    {
        // Spec 053 DD2: the API answers a request it cannot route with 422 and the reason as ProblemDetails detail.
        _server.Given(Request.Create().WithPath("/api/work-items").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(422)
                .WithHeader("Content-Type", "application/problem+json")
                .WithBody("""{"title":"Work item refused","status":422,"detail":"No agent route: set the default route in Settings → Agents → Routing"}"""));

        var act = () => _client.CreateAsync(CreateRequest());

        await act.Should().ThrowAsync<WorkItemRefusedException>()
            .WithMessage("No agent route: set the default route in Settings → Agents → Routing");
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"title":"Unprocessable"}""")]
    public async Task CreateAsync_A422WithoutADetail_ThrowsTheFixedText(string body)
    {
        // A previous-release API replica answers a consolidation without a job template with a bare 422 (V9).
        _server.Given(Request.Create().WithPath("/api/work-items").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(422).WithBody(body));

        var act = () => _client.CreateAsync(CreateRequest());

        await act.Should().ThrowAsync<WorkItemRefusedException>().WithMessage(PipelineApiWorkItemClient.RefusedWithoutReason);
    }

    [Fact]
    public async Task KubernetesWorkDistributor_ARefusedCreate_IsAPermanentFailureWithTheReason()
    {
        _server.Given(Request.Create().WithPath("/api/work-items").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(422)
                .WithHeader("Content-Type", "application/problem+json")
                .WithBody("""{"status":422,"detail":"agent profile 'Kiro' is disabled"}"""));
        var distributor = new CodingAgent.Orchestration.Dispatch.KubernetesWorkDistributor(
            _client, Microsoft.Extensions.Logging.Abstractions.NullLogger<CodingAgent.Orchestration.Dispatch.KubernetesWorkDistributor>.Instance);

        var result = await distributor.DistributeAsync(CreateRequest(), CancellationToken.None);

        result.Success.Should().BeFalse();
        result.IsPermanentFailure.Should().BeTrue("no retry changes a routing refusal");
        result.ErrorMessage.Should().Be("agent profile 'Kiro' is disabled");
    }

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

    // ── PostStatusAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task PostStatusAsync_Returns200_ReturnsTrue()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/status").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{}"));

        var result = await _client.PostStatusAsync(workItemId,
            new WorkItemStatusUpdate { Status = "Running" });

        result.Should().BeTrue("HTTP 200 means a real state transition occurred");
        _server.LogEntries.Should().Contain(e =>
            e.RequestMessage!.Method == "POST" &&
            e.RequestMessage.Path == $"/api/work-items/{workItemId}/status");
    }

    [Fact]
    public async Task PostStatusAsync_Returns204_ReturnsFalse()
    {
        // HTTP 204 No Content = idempotent no-op (already-terminal item) — issue #2802
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/status").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(204));

        var result = await _client.PostStatusAsync(workItemId,
            new WorkItemStatusUpdate { Status = "Failed" });

        result.Should().BeFalse("HTTP 204 No Content signals an idempotent no-op (already-terminal item)");
    }

    // ── RequeueAsync ───────────────────────────────────────────────────────────

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

    [Fact]
    public async Task IsIssueDistributedAsync_NullResponse_ReturnsFalse()
    {
        _server.Given(Request.Create().WithPath("/api/work-items/is-distributed").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("null"));

        var result = await _client.IsIssueDistributedAsync("owner/repo#1", "provider-1");

        result.Should().BeFalse("null response falls back to false via null-coalescing");
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

    [Fact]
    public async Task GetStalenessAsync_EncodesQueryParams()
    {
        var (client, handler) = CreateWithStubHandler();
        handler.Respond = _ => JsonResponse(new WorkItemStalenessResult
        {
            HasAgentErrorSince = false,
            LastSuccessfulCompletion = null
        });

        await client.GetStalenessAsync("GH-1", "github", DateTimeOffset.UtcNow);

        handler.LastRequest!.RequestUri!.PathAndQuery
            .Should().Contain("issueIdentifier=GH-1")
            .And.Contain("issueProviderConfigId=github");
    }

    // ── PostLabelSwapAsync ─────────────────────────────────────────────────────

    // ── PostLastProgressAsync ──────────────────────────────────────────────────

    // ── SetPriorityAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task SetPriorityAsync_Success_DoesNotThrow()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/priority").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var act = () => _client.SetPriorityAsync(workItemId, 500);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SetPriorityAsync_Conflict_ThrowsHttpRequestException()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/priority").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(409));

        var act = () => _client.SetPriorityAsync(workItemId, 100);
        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task SetPriorityAsync_BadRequest_ThrowsHttpRequestException()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/priority").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(400));

        var act = () => _client.SetPriorityAsync(workItemId, -1);
        await act.Should().ThrowAsync<HttpRequestException>();
        // TODO: The three SetPriorityAsync tests do not verify the request body serialisation.
        // WireMock stubs match on path only, so they would pass even if the body were empty or
        // malformed. Add a test that inspects the recorded request body to confirm it contains
        // {"priorityWeight": <value>} so regressions in payload serialisation are caught.
    }

    // ── DispatchPendingAsync ──────────────────────────────────────────────

    [Fact]
    public async Task DispatchPendingAsync_200WithDispatchedTrue_ReturnsDispatched()
    {
        // Issue #2976: 200 with dispatched:true (not 200 with no body) is the success response.
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/dispatch").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"dispatched":true,"reason":"none"}"""));

        var result = await _client.DispatchPendingAsync(workItemId);

        result.Should().Be(DispatchPendingResult.Dispatched,
            "200 OK with dispatched:true must return Dispatched so the poller continues to the next item");
    }

    [Fact]
    public async Task DispatchPendingAsync_200WithDispatchedFalse_ConcurrencyLimit_ReturnsPermanentRejection()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/dispatch").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"dispatched":false,"reason":"concurrency_limit"}"""));

        var result = await _client.DispatchPendingAsync(workItemId);

        result.Should().Be(DispatchPendingResult.PermanentRejection,
            "200 with dispatched:false must return PermanentRejection (stop selector this cycle)");
    }

    [Fact]
    public async Task DispatchPendingAsync_200WithDispatchedFalse_NotPending_ReturnsPermanentRejection()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/dispatch").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"dispatched":false,"reason":"not_pending"}"""));

        var result = await _client.DispatchPendingAsync(workItemId);

        result.Should().Be(DispatchPendingResult.PermanentRejection,
            "200 with dispatched:false (not_pending) must return PermanentRejection");
    }

    [Fact]
    public async Task DispatchPendingAsync_200WithDispatchedFalse_NoTemplate_ReturnsPermanentRejection()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/dispatch").UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"dispatched":false,"reason":"no_template"}"""));

        var result = await _client.DispatchPendingAsync(workItemId);

        result.Should().Be(DispatchPendingResult.PermanentRejection,
            "200 with dispatched:false (no_template) must return PermanentRejection");
    }

    [Fact]
    public async Task DispatchPendingAsync_409_ThrowsHttpRequestException()
    {
        // Issue #2976: 409 is no longer an expected response from this endpoint.
        // The client must surface it as an exception (via EnsureSuccessStatusCode).
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/dispatch").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(409));

        var act = () => _client.DispatchPendingAsync(workItemId);

        await act.Should().ThrowAsync<HttpRequestException>(
            "409 is no longer an expected response; it must surface as an exception");
    }

    [Fact]
    public async Task DispatchPendingAsync_ServiceUnavailable_ReturnsTransient()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/dispatch").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(503));

        var result = await _client.DispatchPendingAsync(workItemId);

        result.Should().Be(DispatchPendingResult.Transient,
            "503 Service Unavailable must return Transient so the Scheduler poller retries next cycle");
    }

    [Fact]
    public async Task DispatchPendingAsync_UnexpectedStatus_ThrowsHttpRequestException()
    {
        var workItemId = Guid.NewGuid();
        _server.Given(Request.Create().WithPath($"/api/work-items/{workItemId}/dispatch").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(500));

        var act = () => _client.DispatchPendingAsync(workItemId);

        await act.Should().ThrowAsync<HttpRequestException>(
            "unexpected status codes (e.g. 500 Internal Server Error) must throw, not be silently swallowed");
    }

    // ── GetActiveDecompositionCountAsync ──────────────────────────────────

    [Fact]
    public async Task GetActiveDecompositionCountAsync_ReturnsCount()
    {
        var (client, handler) = CreateWithStubHandler();
        handler.Respond = _ => JsonResponse(new { count = 3 });

        var result = await client.GetActiveDecompositionCountAsync();

        result.Should().Be(3);
        handler.LastRequest!.RequestUri!.PathAndQuery.Should().Be("/api/work-items/active-decomposition-count");
    }

    [Fact]
    public async Task GetActiveDecompositionCountAsync_WhenNullDeserialization_ThrowsAndLogsWarning()
    {
        var mockLogger = new Mock<ILogger>(MockBehavior.Default);
        // ForContext<T>() returns null when not set up on a Loose mock (reference return type).
        // Without this setup, _logger is null and Warning() throws NullReferenceException instead
        // of the InvalidOperationException asserted below. Pattern consistent with
        // AutoConsolidationServiceTests and WorkItemDispatchLoopTests.
        mockLogger
            .Setup(l => l.ForContext<PipelineApiWorkItemClient>())
            .Returns(mockLogger.Object);

        var handler = new StubHandler { Respond = _ => NullJson() };
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new PipelineApiWorkItemClient(http, mockLogger.Object);

        var act = () => client.GetActiveDecompositionCountAsync();

        await act.Should().ThrowAsync<InvalidOperationException>(
            "a null deserialization result must throw so the Scheduler's catch block treats it " +
            "as a transient error rather than a valid zero count");

        // The Warning call uses two structured properties ({Endpoint}, {Type}), so the invoked
        // Serilog overload is Warning<T0,T1>(string, T0, T1). The Verify must match that exact
        // 3-argument signature — a mismatch yields 0 invocations (false-negative) rather than a
        // compile error.
        mockLogger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("null") || s.Contains("deserialized")),
                It.IsAny<string>(),   // {Endpoint} parameter
                It.IsAny<string>()), // {Type} parameter
            Times.Once,
            "a Warning must be logged before throwing so the failure is visible in telemetry");
    }

    // ── Stub ──────────────────────────────────────────────────────────────

    internal sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Respond { get; set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(Respond?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
