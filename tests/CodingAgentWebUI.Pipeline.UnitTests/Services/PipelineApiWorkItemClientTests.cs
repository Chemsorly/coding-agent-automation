using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for PipelineApiWorkItemClient — HTTP method, URL construction,
/// and special-case status code handling (409/404 → null, 404/410 → null).
/// </summary>
public sealed class PipelineApiWorkItemClientTests
{
    private static (IPipelineApiWorkItemClient Client, StubHandler Handler) Create()
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

    private static HttpResponseMessage Empty(HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent("") };

    private static PendingWorkItemDto MakePendingDto() => new()
    {
        Id = Guid.NewGuid(),
        IssueIdentifier = "GH-1",
        IssueProviderConfigId = "github",
        TaskType = WorkItemTaskType.Implementation,
        CreatedAt = DateTimeOffset.UtcNow,
        AgentSelector = "kiro",
        RetryCount = 0
    };

    // ── GetPendingAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetPendingAsync_ReturnsItems()
    {
        var (client, handler) = Create();
        handler.Respond = _ => JsonResponse(new List<PendingWorkItemDto> { MakePendingDto() });

        var result = await client.GetPendingAsync();

        result.Should().HaveCount(1);
        handler.LastRequest!.RequestUri!.PathAndQuery.Should().Contain("/api/work-items/pending");
    }

    [Fact]
    public async Task GetPendingAsync_WhenNull_ReturnsEmpty()
    {
        var (client, handler) = Create();
        handler.Respond = _ => NullJson();

        var result = await client.GetPendingAsync();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPendingAsync_PassesMaxResults()
    {
        var (client, handler) = Create();
        handler.Respond = _ => JsonResponse(new List<PendingWorkItemDto>());

        await client.GetPendingAsync(maxResults: 25);

        handler.LastRequest!.RequestUri!.PathAndQuery.Should().Contain("maxResults=25");
    }

    // ── ClaimAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task ClaimAsync_OnSuccess_ReturnsResponse()
    {
        var (client, handler) = Create();
        var workItemId = Guid.NewGuid();
        var response = new WorkItemClaimResponse
        {
            WorkItemId = workItemId,
            RunId = "run-1",
            PayloadJson = "{}",
            OrchestratorUrl = "http://localhost"
        };
        handler.Respond = _ => JsonResponse(response);

        var result = await client.ClaimAsync(workItemId, new ClaimWorkItemRequest
        {
            AssignedAgentId = "agent-1",
            DispatchedAt = DateTimeOffset.UtcNow
        });

        result.Should().NotBeNull();
        result!.WorkItemId.Should().Be(workItemId);
    }

    [Fact]
    public async Task ClaimAsync_OnConflict_ReturnsNull()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty(HttpStatusCode.Conflict);

        var result = await client.ClaimAsync(Guid.NewGuid(), new ClaimWorkItemRequest
        {
            DispatchedAt = DateTimeOffset.UtcNow
        });
        result.Should().BeNull();
    }

    [Fact]
    public async Task ClaimAsync_OnNotFound_ThrowsWorkItemNotFoundException()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty(HttpStatusCode.NotFound);

        var workItemId = Guid.NewGuid();
        var act = () => client.ClaimAsync(workItemId, new ClaimWorkItemRequest
        {
            DispatchedAt = DateTimeOffset.UtcNow
        });
        await act.Should().ThrowAsync<WorkItemNotFoundException>()
            .Where(ex => ex.WorkItemId == workItemId);
    }

    // ── GetAssignmentAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetAssignmentAsync_OnNotFound_ReturnsNull()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty(HttpStatusCode.NotFound);

        var result = await client.GetAssignmentAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetAssignmentAsync_OnGone_ReturnsNull()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty(HttpStatusCode.Gone);

        var result = await client.GetAssignmentAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    // ── PostStatusAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task PostStatusAsync_SendsPost()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty();
        var id = Guid.NewGuid();

        await client.PostStatusAsync(id, new WorkItemStatusUpdate
        {
            Status = "Running"
        });

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.PathAndQuery
            .Should().Be($"/api/work-items/{id}/status");
    }

    // ── RequeueAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task RequeueAsync_SendsPost()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty();
        var id = Guid.NewGuid();

        await client.RequeueAsync(id);

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.PathAndQuery.Should().Be($"/api/work-items/{id}/requeue");
    }

    [Fact]
    public async Task RequeueAsync_OnConflict_DoesNotThrow()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty(HttpStatusCode.Conflict);
        var id = Guid.NewGuid();

        // 409 Conflict means the item is already Pending/Running/terminal — the requeue
        // intent is satisfied. Must not throw.
        var act = () => client.RequeueAsync(id);
        await act.Should().NotThrowAsync();
    }

    // ── GetRetryCountAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetRetryCountAsync_ReturnsCount()
    {
        var (client, handler) = Create();
        handler.Respond = _ => JsonResponse(new { RetryCount = 3 });

        var result = await client.GetRetryCountAsync(Guid.NewGuid());
        result.Should().Be(3);
    }

    [Fact]
    public async Task GetRetryCountAsync_WhenNull_ReturnsZero()
    {
        var (client, handler) = Create();
        handler.Respond = _ => NullJson();

        var result = await client.GetRetryCountAsync(Guid.NewGuid());
        result.Should().Be(0);
    }

    // ── GetStalenessAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task GetStalenessAsync_OnNotFound_ReturnsNull()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty(HttpStatusCode.NotFound);

        var result = await client.GetStalenessAsync("GH-1", "github", DateTimeOffset.UtcNow);
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetStalenessAsync_EncodesQueryParams()
    {
        var (client, handler) = Create();
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

    // ── CreateAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task CreateAsync_ReturnsGuid()
    {
        var (client, handler) = Create();
        var expected = Guid.NewGuid();
        handler.Respond = _ => JsonResponse(expected);

        var result = await client.CreateAsync(new JobDistributionRequest
        {
            IssueIdentifier = new IssueIdentifier("GH-1"),
            IssueProviderConfigId = "github",
            RepoProviderConfigId = "github-repo",
            InitiatedBy = "test",
            TaskType = WorkItemTaskType.Implementation,
            AgentSelector = "kiro",
            TimeoutSeconds = 3600
        });

        result.Should().Be(expected);
    }

    // ── PostLabelSwapAsync ────────────────────────────────────────────────

    [Fact]
    public async Task PostLabelSwapAsync_SendsCorrectUrl()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty();
        var id = Guid.NewGuid();

        await client.PostLabelSwapAsync(id, "agent:done");

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.PathAndQuery
            .Should().Be($"/api/work-items/{id}/label-swap");
    }

    // ── GetActiveAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetActiveAsync_WhenNull_ReturnsEmpty()
    {
        var (client, handler) = Create();
        handler.Respond = _ => NullJson();

        var result = await client.GetActiveAsync(300);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetActiveAsync_PassesOlderThanSeconds()
    {
        var (client, handler) = Create();
        handler.Respond = _ => JsonResponse(new List<ActiveWorkItemDto>());

        await client.GetActiveAsync(olderThanSeconds: 600);

        handler.LastRequest!.RequestUri!.PathAndQuery.Should().Contain("olderThanSeconds=600");
    }

    // ── PostLastProgressAsync ─────────────────────────────────────────────

    [Fact]
    public async Task PostLastProgressAsync_SendsPost()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty();
        var id = Guid.NewGuid();

        await client.PostLastProgressAsync(id, DateTimeOffset.UtcNow);

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.PathAndQuery
            .Should().Be($"/api/work-items/{id}/last-progress");
    }

    // ── GetK8sJobNameAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetK8sJobNameAsync_OnNotFound_ReturnsNull()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty(HttpStatusCode.NotFound);

        var result = await client.GetK8sJobNameAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetK8sJobNameAsync_ReturnsJobName()
    {
        var (client, handler) = Create();
        handler.Respond = _ => JsonResponse(new { JobName = "agent-job-abc" });

        var result = await client.GetK8sJobNameAsync(Guid.NewGuid());
        result.Should().Be("agent-job-abc");
    }

    // ── GetStatusAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetStatusAsync_OnNotFound_ReturnsNull()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty(HttpStatusCode.NotFound);

        var result = await client.GetStatusAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetStatusAsync_ReturnsStatus()
    {
        var (client, handler) = Create();
        handler.Respond = _ => JsonResponse(new { Status = WorkItemStatus.Running });

        var result = await client.GetStatusAsync(Guid.NewGuid());
        result.Should().Be(WorkItemStatus.Running);
    }

    // ── IsIssueDistributedAsync ───────────────────────────────────────────

    [Fact]
    public async Task IsIssueDistributedAsync_ReturnsTrue()
    {
        var (client, handler) = Create();
        handler.Respond = _ => JsonResponse(new { IsDistributed = true });

        var result = await client.IsIssueDistributedAsync("GH-1", "github");
        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsIssueDistributedAsync_WhenNull_ReturnsFalse()
    {
        var (client, handler) = Create();
        handler.Respond = _ => NullJson();

        var result = await client.IsIssueDistributedAsync("GH-1", "github");
        result.Should().BeFalse();
    }

    // ── GetActiveIdentifiersAsync ─────────────────────────────────────────

    [Fact]
    public async Task GetActiveIdentifiersAsync_WhenNull_ReturnsEmpty()
    {
        var (client, handler) = Create();
        handler.Respond = _ => NullJson();

        var result = await client.GetActiveIdentifiersAsync();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetActiveIdentifiersAsync_ReturnsTuples()
    {
        var (client, handler) = Create();
        handler.Respond = _ => JsonResponse(new[]
        {
            new { IssueIdentifier = "GH-1", IssueProviderConfigId = "github" }
        });

        var result = await client.GetActiveIdentifiersAsync();
        result.Should().HaveCount(1);
        result[0].IssueIdentifier.Should().Be("GH-1");
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
