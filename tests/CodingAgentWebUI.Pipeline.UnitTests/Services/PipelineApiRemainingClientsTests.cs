using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for the remaining zero-coverage API clients:
/// PipelineApiRunHistoryClient, PipelineApiAgentClient, PipelineApiConsolidationRunClient,
/// PipelineApiHarnessSuggestionClient, PipelineApiHealthClient, PipelineApiChatClient.
/// </summary>
public sealed class PipelineApiRemainingClientsTests
{
    // ── Helpers ───────────────────────────────────────────────────────────

    private static (T Client, StubHandler Handler) Create<T>(Func<HttpClient, T> factory)
    {
        var handler = new StubHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        return (factory(http), handler);
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

    private static PagedResult<PipelineRunSummary> MakeEmptyPage() =>
        new() { Items = [], Page = 1, PageSize = 50, HasMore = false };

    private static PipelineRunSummary MakeSummary(string runId = "r1") => new()
    {
        RunId = runId,
        IssueIdentifier = new IssueIdentifier("GH-1"),
        IssueTitle = "T",
        FinalStep = PipelineStep.Completed
    };

    // ─────────────────────────────────────────────────────────────────────
    // PipelineApiRunHistoryClient
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RunHistoryClient_GetRunHistoryAsync_ReturnsPage()
    {
        var (client, handler) = Create(h => new PipelineApiRunHistoryClient(h));
        var page = new PagedResult<PipelineRunSummary>
        {
            Items = [MakeSummary()],
            Page = 1,
            PageSize = 50,
            HasMore = false
        };
        handler.Respond = _ => JsonResponse(page);

        var result = await client.GetRunHistoryAsync();

        result.Items.Should().HaveCount(1);
        handler.LastRequest!.RequestUri!.PathAndQuery.Should().Contain("/api/pipeline-runs");
    }

    [Fact]
    public async Task RunHistoryClient_GetRunHistoryAsync_PassesFeedbackOnly()
    {
        var (client, handler) = Create(h => new PipelineApiRunHistoryClient(h));
        handler.Respond = _ => JsonResponse(MakeEmptyPage());

        await client.GetRunHistoryAsync(feedbackOnly: true);

        handler.LastRequest!.RequestUri!.PathAndQuery.Should().Contain("feedbackOnly=True");
    }

    [Fact]
    public async Task RunHistoryClient_GetRunAsync_WhenNotFound_ReturnsNull()
    {
        var (client, handler) = Create(h => new PipelineApiRunHistoryClient(h));
        handler.Respond = _ => Empty(HttpStatusCode.NotFound);

        var result = await client.GetRunAsync(Guid.NewGuid());
        result.Should().BeNull();
    }

    [Fact]
    public async Task RunHistoryClient_GetRunAsync_ReturnsRun()
    {
        var (client, handler) = Create(h => new PipelineApiRunHistoryClient(h));
        var summary = MakeSummary(Guid.NewGuid().ToString());
        handler.Respond = _ => JsonResponse(summary);

        var result = await client.GetRunAsync(Guid.NewGuid());
        result.Should().NotBeNull();
    }

    [Fact]
    public async Task RunHistoryClient_AddRunToHistoryAsync_SendsPost()
    {
        var (client, handler) = Create(h => new PipelineApiRunHistoryClient(h));
        handler.Respond = _ => Empty();

        await client.AddRunToHistoryAsync(MakeSummary(Guid.NewGuid().ToString()));

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.PathAndQuery.Should().StartWith("/api/pipeline-runs");
    }

    [Fact]
    public async Task RunHistoryClient_AddRunToHistoryAsync_NullThrows()
    {
        var (client, _) = Create(h => new PipelineApiRunHistoryClient(h));
        var act = () => client.AddRunToHistoryAsync(null!);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ─────────────────────────────────────────────────────────────────────
    // PipelineApiAgentClient
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AgentClient_GetAgentsAsync_ReturnsList()
    {
        var (client, handler) = Create(h => new PipelineApiAgentClient(h));
        var agents = new List<AgentEntry>
        {
            new()
            {
                AgentId = new AgentId("a1"),
                ConnectionId = "c1",
                Hostname = "host",
                Labels = [],
                RegisteredAt = DateTimeOffset.UtcNow
            }
        };
        handler.Respond = _ => JsonResponse(agents);

        var result = await client.GetAgentsAsync();
        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task AgentClient_GetAgentsAsync_WhenNull_ReturnsEmpty()
    {
        var (client, handler) = Create(h => new PipelineApiAgentClient(h));
        handler.Respond = _ => NullJson();

        var result = await client.GetAgentsAsync();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task AgentClient_AssignChatPromptAsync_SendsPost()
    {
        var (client, handler) = Create(h => new PipelineApiAgentClient(h));
        handler.Respond = _ => Empty();

        await client.AssignChatPromptAsync("agent-1", new ChatPromptMessage
        {
            SessionId = "sess-1",
            Prompt = "Hello"
        });

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.PathAndQuery.Should().Contain("/api/agents/agent-1/chat-prompt");
    }

    [Fact]
    public async Task AgentClient_AssignChatPromptAsync_EscapesAgentId()
    {
        var (client, handler) = Create(h => new PipelineApiAgentClient(h));
        handler.Respond = _ => Empty();

        await client.AssignChatPromptAsync("agent/id with spaces", new ChatPromptMessage
        {
            SessionId = "s",
            Prompt = "Hi"
        });

        handler.LastRequest!.RequestUri!.PathAndQuery.Should().Contain("agent%2Fid%20with%20spaces");
    }

    private static ConsolidationRun MakeConsolidationRun(string? runId = null) => new()
    {
        RunId = runId ?? Guid.NewGuid().ToString(),
        Type = ConsolidationRunType.BrainConsolidation,
        StartedAtUtc = DateTimeOffset.UtcNow
    };

    private static HarnessSuggestions MakeHarnessSuggestions() => new()
    {
        BasedOnRunCount = 5,
        GeneratedAtUtc = DateTime.UtcNow,
        SuccessRate = 0.8m,
        Suggestions = []
    };

    // ─────────────────────────────────────────────────────────────────────
    // PipelineApiConsolidationRunClient
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConsolidationRunClient_LoadAllRunsAsync_ReturnsList()
    {
        var (client, handler) = Create(h => new PipelineApiConsolidationRunClient(h));
        handler.Respond = _ => JsonResponse(new List<ConsolidationRun>());

        var result = await client.LoadAllRunsAsync();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ConsolidationRunClient_LoadAllRunsAsync_WhenNull_ReturnsEmpty()
    {
        var (client, handler) = Create(h => new PipelineApiConsolidationRunClient(h));
        handler.Respond = _ => NullJson();

        var result = await client.LoadAllRunsAsync();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ConsolidationRunClient_GetByIdAsync_InvalidGuid_ReturnsNull()
    {
        var (client, _) = Create(h => new PipelineApiConsolidationRunClient(h));
        var result = await client.GetByIdAsync("not-a-guid");
        result.Should().BeNull();
    }

    [Fact]
    public async Task ConsolidationRunClient_GetByIdAsync_NotFound_ReturnsNull()
    {
        var (client, handler) = Create(h => new PipelineApiConsolidationRunClient(h));
        handler.Respond = _ => Empty(HttpStatusCode.NotFound);

        var result = await client.GetByIdAsync(Guid.NewGuid().ToString());
        result.Should().BeNull();
    }

    [Fact]
    public async Task ConsolidationRunClient_SaveRunAsync_InvalidGuid_Throws()
    {
        var (client, _) = Create(h => new PipelineApiConsolidationRunClient(h));
        var run = new ConsolidationRun { RunId = "bad-id", Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTimeOffset.UtcNow };

        var act = () => client.SaveRunAsync(run);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ConsolidationRunClient_SaveRunAsync_ValidGuid_UsesPut()
    {
        var (client, handler) = Create(h => new PipelineApiConsolidationRunClient(h));
        handler.Respond = _ => Empty();

        await client.SaveRunAsync(MakeConsolidationRun());

        handler.LastRequest!.Method.Should().Be(HttpMethod.Put);
    }

    [Fact]
    public async Task ConsolidationRunClient_DeleteRunAsync_InvalidGuid_Throws()
    {
        var (client, _) = Create(h => new PipelineApiConsolidationRunClient(h));
        var act = () => client.DeleteRunAsync("not-a-guid");
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ConsolidationRunClient_DeleteRunAsync_ValidGuid_UsesDelete()
    {
        var (client, handler) = Create(h => new PipelineApiConsolidationRunClient(h));
        handler.Respond = _ => Empty();

        await client.DeleteRunAsync(Guid.NewGuid().ToString());

        handler.LastRequest!.Method.Should().Be(HttpMethod.Delete);
    }

    // ─────────────────────────────────────────────────────────────────────
    // PipelineApiHarnessSuggestionClient
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HarnessSuggestionClient_GetAsync_WhenNoContent_ReturnsNull()
    {
        var (client, handler) = Create(h => new PipelineApiHarnessSuggestionClient(h));
        handler.Respond = _ => Empty(HttpStatusCode.NoContent);

        var result = await client.GetAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task HarnessSuggestionClient_GetAsync_WhenNotFound_ReturnsNull()
    {
        var (client, handler) = Create(h => new PipelineApiHarnessSuggestionClient(h));
        handler.Respond = _ => Empty(HttpStatusCode.NotFound);

        var result = await client.GetAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task HarnessSuggestionClient_SaveAsync_UsesPut()
    {
        var (client, handler) = Create(h => new PipelineApiHarnessSuggestionClient(h));
        handler.Respond = _ => Empty();

        await client.SaveAsync(MakeHarnessSuggestions());

        handler.LastRequest!.Method.Should().Be(HttpMethod.Put);
        handler.LastRequest.RequestUri!.PathAndQuery.Should().Be("/api/harness-suggestions");
    }

    // ─────────────────────────────────────────────────────────────────────
    // PipelineApiHealthClient
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task HealthClient_IsHealthyAsync_WhenOk_ReturnsTrue()
    {
        var (client, handler) = Create(h => new PipelineApiHealthClient(h));
        handler.Respond = _ => Empty(HttpStatusCode.OK);

        var result = await client.IsHealthyAsync();
        result.Should().BeTrue();
    }

    [Fact]
    public async Task HealthClient_IsHealthyAsync_WhenServiceUnavailable_ReturnsFalse()
    {
        var (client, handler) = Create(h => new PipelineApiHealthClient(h));
        handler.Respond = _ => Empty(HttpStatusCode.ServiceUnavailable);

        var result = await client.IsHealthyAsync();
        result.Should().BeFalse();
    }

    [Fact]
    public async Task HealthClient_IsHealthyAsync_WhenException_ReturnsFalse()
    {
        var (client, handler) = Create(h => new PipelineApiHealthClient(h));
        handler.Respond = _ => throw new HttpRequestException("network error");

        var result = await client.IsHealthyAsync();
        result.Should().BeFalse();
    }

    [Fact]
    public async Task HealthClient_IsReadyAsync_WhenOk_ReturnsTrue()
    {
        var (client, handler) = Create(h => new PipelineApiHealthClient(h));
        handler.Respond = _ => Empty(HttpStatusCode.OK);

        var result = await client.IsReadyAsync();
        result.Should().BeTrue();
        handler.LastRequest!.RequestUri!.PathAndQuery.Should().Be("/readyz");
    }

    [Fact]
    public async Task HealthClient_IsReadyAsync_WhenException_ReturnsFalse()
    {
        var (client, handler) = Create(h => new PipelineApiHealthClient(h));
        handler.Respond = _ => throw new HttpRequestException("timeout");

        var result = await client.IsReadyAsync();
        result.Should().BeFalse();
    }

    // ─────────────────────────────────────────────────────────────────────
    // PipelineApiChatClient
    // ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ChatClient_DispatchChatPodAsync_ReturnsAgentId()
    {
        var (client, handler) = Create(h => new PipelineApiChatClient(h));
        handler.Respond = _ => JsonResponse(new { AgentId = "agent-xyz" });

        var result = await client.DispatchChatPodAsync("kiro", "claude", null);

        result.Should().Be("agent-xyz");
        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.PathAndQuery.Should().Be("/api/chat/dispatch");
    }

    [Fact]
    public async Task ChatClient_DispatchChatPodAsync_WhenNullBody_Throws()
    {
        var (client, handler) = Create(h => new PipelineApiChatClient(h));
        handler.Respond = _ => NullJson();

        var act = () => client.DispatchChatPodAsync("kiro", null, null);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task ChatClient_TerminateChatSessionAsync_SendsPost()
    {
        var (client, handler) = Create(h => new PipelineApiChatClient(h));
        handler.Respond = _ => Empty();

        await client.TerminateChatSessionAsync("agent-1");

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.PathAndQuery
            .Should().Be("/api/chat/agent-1/terminate");
    }

    [Fact]
    public async Task ChatClient_TerminateChatSessionAsync_EscapesAgentId()
    {
        var (client, handler) = Create(h => new PipelineApiChatClient(h));
        handler.Respond = _ => Empty();

        await client.TerminateChatSessionAsync("agent/1");

        handler.LastRequest!.RequestUri!.PathAndQuery.Should().Contain("agent%2F1");
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
