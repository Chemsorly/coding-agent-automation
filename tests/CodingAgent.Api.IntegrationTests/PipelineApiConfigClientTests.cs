using System.Net;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using System.Text;

namespace CodingAgent.Api.IntegrationTests;

/// <summary>
/// Unit tests for <see cref="PipelineApiConfigClient"/> using WireMock.Net to stub HTTP responses.
/// Each test gets its own WireMock server on a random port; no live API required.
/// </summary>
public sealed class PipelineApiConfigClientTests : IAsyncDisposable
{
    private readonly WireMockServer _server;
    private readonly PipelineApiConfigClient _client;

    public PipelineApiConfigClientTests()
    {
        _server = WireMockServer.Start();
        var http = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        _client = new PipelineApiConfigClient(http);
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

    private void StubGet(string path, object body, int status = 200) =>
        _server.Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(status)
                .WithHeader("Content-Type", "application/json")
                .WithBody(Serialize(body)));

    private void StubPut(string path, int status = 200) =>
        _server.Given(Request.Create().WithPath(path).UsingPut())
            .RespondWith(Response.Create()
                .WithStatusCode(status)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{}"));

    private void StubDelete(string path, int status = 200) =>
        _server.Given(Request.Create().WithPath(path).UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(status));

    private void StubPost(string path, int status = 200) =>
        _server.Given(Request.Create().WithPath(path).UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(status)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{}"));

    private void StubPutNoContent(string path)
    {
        _server.Given(Request.Create().WithPath(path).UsingPut())
            .RespondWith(Response.Create().WithStatusCode(204));
    }

    private static (IPipelineApiConfigClient Client, StubHandler Handler) CreateWithStubHandler()
    {
        var handler = new StubHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var client = new PipelineApiConfigClient(http);
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

    private static ProviderConfig MakeProviderConfig() => new()
    {
        Id = "p1",
        Kind = ProviderKind.Issue,
        DisplayName = "Test",
        ProviderType = "GitHub"
    };

    // ── GetPipelineConfigAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task GetPipelineConfigAsync_Returns_DeserializedConfig()
    {
        var config = new PipelineConfiguration { MaxRetries = 5 };
        StubGet("/api/config/pipeline", config);

        var result = await _client.GetPipelineConfigAsync();

        result.Should().NotBeNull();
        result.MaxRetries.Should().Be(5);
    }

    [Fact]
    public async Task GetPipelineConfigAsync_SendsGetToCorrectPath()
    {
        StubGet("/api/config/pipeline", new PipelineConfiguration());

        await _client.GetPipelineConfigAsync();

        _server.LogEntries.Should().Contain(e =>
            e.RequestMessage!.Method == "GET" &&
            e.RequestMessage.Path == "/api/config/pipeline");
    }

    [Fact]
    public async Task GetPipelineConfigAsync_NullApiResponse_ThrowsInvalidOperationException()
    {
        // API returns literal JSON null — null-guard must throw
        _server.Given(Request.Create().WithPath("/api/config/pipeline").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("null"));

        await _client.Invoking(c => c.GetPipelineConfigAsync())
            .Should().ThrowAsync<InvalidOperationException>();
    }

    // ── SavePipelineConfigAsync ────────────────────────────────────────────────

    [Fact]
    public async Task SavePipelineConfigAsync_SendsPutToCorrectPath()
    {
        StubPut("/api/config/pipeline");

        var config = new PipelineConfiguration { MaxRetries = 3 };
        await _client.SavePipelineConfigAsync(config);

        _server.LogEntries.Should().Contain(e =>
            e.RequestMessage!.Method == "PUT" &&
            e.RequestMessage.Path == "/api/config/pipeline");
    }

    [Fact]
    public async Task SavePipelineConfigAsync_NonSuccessResponse_ThrowsInvalidOperationExceptionWithBody()
    {
        const string errorBody = "AgentTimeout must be at least 60 seconds";
        _server.Given(Request.Create().WithPath("/api/config/pipeline").UsingPut())
            .RespondWith(Response.Create()
                .WithStatusCode(400)
                .WithHeader("Content-Type", "application/json")
                .WithBody(errorBody));

        var config = new PipelineConfiguration { MaxRetries = 3 };
        var act = () => _client.SavePipelineConfigAsync(config);

        await act.Should().ThrowAsync<InvalidOperationException>(
            "a non-2xx response must surface as an exception so callers are not silently swallowed")
            .WithMessage($"*{errorBody}*",
                "the exception message must include the response body so operators can diagnose the failure");
    }

    // ── GetProviderConfigsAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetProviderConfigsAsync_Returns_DeserializedList()
    {
        var configs = new List<ProviderConfig>
        {
            new() { Id = "p1", Kind = ProviderKind.Issue, DisplayName = "Provider 1", ProviderType = "github" }
        };
        _server.Given(Request.Create()
                .WithPath("/api/config/provider-configs")
                .WithParam("kind", "Issue")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(Serialize(configs)));

        var result = await _client.GetProviderConfigsAsync(ProviderKind.Issue);

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("p1");
    }

    [Fact]
    public async Task GetProviderConfigsAsync_ReturnsConfigs()
    {
        var (client, handler) = CreateWithStubHandler();
        handler.Respond = _ => JsonResponse(new List<ProviderConfig> { MakeProviderConfig() });

        var result = await client.GetProviderConfigsAsync(ProviderKind.Issue);

        result.Should().HaveCount(1);
        handler.LastRequest!.RequestUri!.PathAndQuery
            .Should().Contain("kind=Issue").And.Contain("includeSecrets=False");
    }

    [Fact]
    public async Task GetProviderConfigsAsync_NullResponse_ReturnsEmptyList()
    {
        _server.Given(Request.Create()
                .WithPath("/api/config/provider-configs")
                .WithParam("kind", "Repository")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("null"));

        var result = await _client.GetProviderConfigsAsync(ProviderKind.Repository);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetProviderConfigsWithSecretsAsync_IncludesSecretsTrueInQueryString()
    {
        _server.Given(Request.Create().WithPath("/api/config/provider-configs").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("[]"));

        await _client.GetProviderConfigsWithSecretsAsync(ProviderKind.Agent);

        var queryString = _server.LogEntries[0].RequestMessage!.RawQuery;
        queryString.Should().Contain("includeSecrets=True");
    }

    // ── GetAgentProfilesAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task GetAgentProfilesAsync_Returns_DeserializedList()
    {
        var profiles = new List<AgentProfile>
        {
            new() { Id = "ap1", DisplayName = "Agent 1", AgentProviderConfigId = "prov-1" }
        };
        StubGet("/api/config/agent-profiles", profiles);

        var result = await _client.GetAgentProfilesAsync();

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("ap1");
        result[0].DisplayName.Should().Be("Agent 1");
    }

    [Fact]
    public async Task GetAgentProfilesAsync_NullResponse_ReturnsEmptyList()
    {
        _server.Given(Request.Create().WithPath("/api/config/agent-profiles").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("null"));

        var result = await _client.GetAgentProfilesAsync();

        result.Should().BeEmpty();
    }

    // ── GetQualityGateConfigsAsync ─────────────────────────────────────────────

    [Fact]
    public async Task GetQualityGateConfigsAsync_Returns_DeserializedList()
    {
        var configs = new List<QualityGateConfiguration>
        {
            new() { Id = "qg1", DisplayName = "Gate 1" }
        };
        StubGet("/api/config/quality-gate-configs", configs);

        var result = await _client.GetQualityGateConfigsAsync();

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("qg1");
    }

    // ── GetReviewerConfigsAsync ────────────────────────────────────────────────

    [Fact]
    public async Task GetReviewerConfigsAsync_Returns_DeserializedList()
    {
        var configs = new List<ReviewerConfiguration>
        {
            new() { Id = "rev1", DisplayName = "Reviewer 1", Agents = [] }
        };
        StubGet("/api/config/reviewer-configs", configs);

        var result = await _client.GetReviewerConfigsAsync();

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("rev1");
    }

    // ── GetProjectsAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetProjectsAsync_Returns_DeserializedList()
    {
        var projects = new List<PipelineProject>
        {
            new() { Id = "proj1", Name = "Project 1" }
        };
        StubGet("/api/config/projects", projects);

        var result = await _client.GetProjectsAsync();

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("proj1");
    }

    [Fact]
    public async Task GetProjectByIdAsync_NotFound_ReturnsNull()
    {
        _server.Given(Request.Create().WithPath("/api/config/projects/nonexistent").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var result = await _client.GetProjectByIdAsync("nonexistent");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetProjectByIdAsync_Found_ReturnsProject()
    {
        var project = new PipelineProject { Id = "proj-abc", Name = "My Project" };
        _server.Given(Request.Create().WithPath("/api/config/projects/proj-abc").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(Serialize(project)));

        var result = await _client.GetProjectByIdAsync("proj-abc");

        result.Should().NotBeNull();
        result!.Id.Should().Be("proj-abc");
    }

    [Fact]
    public async Task GetProjectByIdAsync_EncodesIdInPath()
    {
        // The stub handler sees the raw request URI; WireMock would match the decoded path whatever the encoding.
        var (client, handler) = CreateWithStubHandler();
        handler.Respond = _ => JsonResponse(new PipelineProject { Id = "id/with/slash", Name = "Encoded" });

        var result = await client.GetProjectByIdAsync("id/with/slash");

        result.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.PathAndQuery.Should().Be("/api/config/projects/id%2Fwith%2Fslash");
    }

    // ── GetAllTemplatesAsync ───────────────────────────────────────────────────

    [Fact]
    public async Task GetAllTemplatesAsync_Returns_DeserializedList()
    {
        var templates = new List<PipelineJobTemplate>
        {
            new()
            {
                Id = "tmpl1",
                Name = "Template 1",
                IssueProviderId = "issue-prov",
                RepoProviderId = "repo-prov"
            }
        };
        StubGet("/api/config/templates", templates);

        var result = await _client.GetAllTemplatesAsync();

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("tmpl1");
    }

    // ── UpdatePipelineConfigAsync ──────────────

    [Fact]
    public async Task UpdatePipelineConfigAsync_FetchesThenSaves()
    {
        var original = new PipelineConfiguration { WorkspaceBaseDirectory = "/old" };
        StubGet("/api/config/pipeline", original);
        StubPutNoContent("/api/config/pipeline");

        await _client.UpdatePipelineConfigAsync(c => c with { WorkspaceBaseDirectory = "/new" });

        // GET then PUT
        _server.LogEntries.Should().HaveCount(2);
    }

    // ── Templates ────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAllTemplatesAsync_NullResponse_ReturnsEmpty()
    {
        _server.Given(Request.Create().WithPath("/api/config/templates").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("null"));

        var result = await _client.GetAllTemplatesAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTemplatesForProjectAsync_EncodesProjectId()
    {
        var (client, handler) = CreateWithStubHandler();
        handler.Respond = _ => JsonResponse(new List<PipelineJobTemplate>());

        await client.GetTemplatesForProjectAsync("my project");

        handler.LastRequest!.RequestUri!.PathAndQuery.Should().Contain("my%20project");
    }

    [Fact]
    public async Task SavePipelineConfigAsync_RefusedWithAJsonString_ThrowsWithTheApiReason()
    {
        var (client, handler) = CreateWithStubHandler();
        handler.Respond = _ => JsonResponse("AgentTimeout must be at least 60 seconds.", HttpStatusCode.BadRequest);

        var act = () => client.SavePipelineConfigAsync(new PipelineConfiguration());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("AgentTimeout must be at least 60 seconds.");
    }

    // ── GetQualityGateConfigsAsync / GetReviewerConfigsAsync ─────────────

    [Fact]
    public async Task GetQualityGateConfigsAsync_NullResponse_ReturnsEmpty()
    {
        _server.Given(Request.Create().WithPath("/api/config/quality-gate-configs").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("null"));

        var result = await _client.GetQualityGateConfigsAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetReviewerConfigsAsync_NullResponse_ReturnsEmpty()
    {
        _server.Given(Request.Create().WithPath("/api/config/reviewer-configs").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("null"));

        var result = await _client.GetReviewerConfigsAsync();

        result.Should().BeEmpty();
    }

    // ── Stub handler ──────────────────────────────────────────────────────

    internal sealed class StubHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage>? Respond { get; set; }
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            var response = Respond?.Invoke(request) ?? new HttpResponseMessage(HttpStatusCode.OK);
            return Task.FromResult(response);
        }
    }
}
