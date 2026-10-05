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

    private static HttpResponseMessage Empty(HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent("") };

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

    // ── SaveProviderConfigAsync ────────────────────────────────────────────────

    [Fact]
    public async Task SaveProviderConfigAsync_SendsPutWithBody()
    {
        StubPut("/api/config/provider-configs");

        var config = new ProviderConfig { Id = "p1", Kind = ProviderKind.Issue, DisplayName = "Test", ProviderType = "github" };
        await _client.SaveProviderConfigAsync(config);

        var entry = _server.LogEntries.First(e =>
            e.RequestMessage!.Method == "PUT" &&
            e.RequestMessage.Path == "/api/config/provider-configs");
        entry.RequestMessage!.Body.Should().Contain("p1");
    }

    // ── DeleteProviderConfigAsync ──────────────────────────────────────────────

    [Fact]
    public async Task DeleteProviderConfigAsync_SendsDeleteToCorrectPath()
    {
        _server.Given(Request.Create()
                .WithPath("/api/config/provider-configs/my-provider-id")
                .UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(200));

        await _client.DeleteProviderConfigAsync("my-provider-id", ProviderKind.Issue);

        _server.LogEntries.Should().Contain(e =>
            e.RequestMessage!.Method == "DELETE" &&
            e.RequestMessage.Path!.Contains("my-provider-id"));
    }

    [Fact]
    public async Task DeleteProviderConfigAsync_SendsDeleteWithEscapedId()
    {
        var (client, handler) = CreateWithStubHandler();
        handler.Respond = _ => Empty();

        await client.DeleteProviderConfigAsync("my id/with slash", ProviderKind.Issue);

        handler.LastRequest!.Method.Should().Be(HttpMethod.Delete);
        handler.LastRequest.RequestUri!.PathAndQuery.Should().Contain("my%20id%2Fwith%20slash");
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
        var project = new PipelineProject { Id = "id/with/slash", Name = "Encoded" };
        // HttpClient encodes "/" as "%2F". WireMock matches the decoded path "/api/config/projects/id/with/slash"
        StubGet("/api/config/projects/id/with/slash", project);

        var result = await _client.GetProjectByIdAsync("id/with/slash");

        result.Should().NotBeNull();
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

    // ── GetKeyValueAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task GetKeyValueAsync_NotFound_ReturnsNull()
    {
        // Server now returns 200 with JSON null for unset keys (not 404)
        _server.Given(Request.Create().WithPath("/api/config/key-value/missing-key").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("null"));

        var result = await _client.GetKeyValueAsync("missing-key");

        result.Should().BeNull();
        // TODO: [WARNING] The client's string.IsNullOrEmpty branch (empty-body path) has no
        // test coverage here. The real server emits `null` (not empty), so this is not a
        // production defect, but the live branch is untested. Consider adding a complementary
        // test that stubs a 200 response with an empty body to document that fallback path.
    }

    [Fact]
    public async Task GetKeyValueAsync_Found_ReturnsValue()
    {
        _server.Given(Request.Create().WithPath("/api/config/key-value/my-key").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("\"hello-world\""));

        var result = await _client.GetKeyValueAsync("my-key");

        result.Should().Be("hello-world");
    }

    [Fact]
    public async Task GetKeyValueAsync_EncodesKeyInPath()
    {
        // HttpClient encodes spaces as %20, WireMock matches the decoded path
        _server.Given(Request.Create().WithPath("/api/config/key-value/has space").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("\"ok\""));

        var result = await _client.GetKeyValueAsync("has space");

        result.Should().Be("ok");
    }

    // ── HasEnabledTemplatesAsync ───────────────────────────────────────────────

    [Fact]
    public async Task HasEnabledTemplatesAsync_ReturnsTrue_WhenServerSaysTrue()
    {
        _server.Given(Request.Create().WithPath("/api/config/projects/has-enabled-templates").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("true"));

        var result = await _client.HasEnabledTemplatesAsync();

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasEnabledTemplatesAsync_ReturnsFalse_WhenServerSaysFalse()
    {
        _server.Given(Request.Create().WithPath("/api/config/projects/has-enabled-templates").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("false"));

        var result = await _client.HasEnabledTemplatesAsync();

        result.Should().BeFalse();
    }

    // ── GetModelsAsync ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetModelsAsync_Success_ReturnsList()
    {
        var models = new List<AgentModelInfo>
        {
            new() { ModelId = "gpt-4o", Description = "GPT-4o model" }
        };
        StubGet("/api/config/models", models);

        var (result, error) = await _client.GetModelsAsync();

        result.Should().HaveCount(1);
        result[0].ModelId.Should().Be("gpt-4o");
        error.Should().BeNull();
    }

    [Fact]
    public async Task GetModelsAsync_Failure_ReturnsEmptyListWithError()
    {
        _server.Given(Request.Create().WithPath("/api/config/models").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(500)
                .WithBody("Internal Server Error"));

        var (result, error) = await _client.GetModelsAsync();

        result.Should().BeEmpty();
        error.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task GetModelsAsync_NullApiResponse_ReturnsEmptyList()
    {
        _server.Given(Request.Create().WithPath("/api/config/models").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("null"));

        var (result, error) = await _client.GetModelsAsync();

        result.Should().BeEmpty();
        error.Should().BeNull();
    }

    [Fact]
    public async Task GetModelsAsync_OnFailure_ReturnsErrorString()
    {
        var (client, handler) = CreateWithStubHandler();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("service down")
        };

        var (result, error) = await client.GetModelsAsync();

        result.Should().BeEmpty();
        error.Should().Be("service down");
    }

    // ── ExportConfigAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExportConfigAsync_ReturnsBytes()
    {
        var payload = new byte[] { 1, 2, 3 };
        _server.Given(Request.Create().WithPath("/api/config/export").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(payload));

        var result = await _client.ExportConfigAsync();

        result.Should().BeEquivalentTo(payload);
    }

    // ── SaveAgentProfileAsync ──────────────────────────────────────────────────

    [Fact]
    public async Task SaveAgentProfileAsync_SendsPutToCorrectPath()
    {
        StubPut("/api/config/agent-profiles");

        var profile = new AgentProfile { Id = "ap1", DisplayName = "Test", AgentProviderConfigId = "prov-1" };
        await _client.SaveAgentProfileAsync(profile);

        _server.LogEntries.Should().Contain(e =>
            e.RequestMessage!.Method == "PUT" &&
            e.RequestMessage.Path == "/api/config/agent-profiles");
    }

    // ── DeleteAgentProfileAsync ────────────────────────────────────────────────

    [Fact]
    public async Task DeleteAgentProfileAsync_SendsDeleteToCorrectPath()
    {
        _server.Given(Request.Create().WithPath("/api/config/agent-profiles/ap1").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(200));

        await _client.DeleteAgentProfileAsync("ap1");

        _server.LogEntries.Should().Contain(e =>
            e.RequestMessage!.Method == "DELETE" &&
            e.RequestMessage.Path!.Contains("ap1"));
    }

    [Fact]
    public async Task DeleteAgentProfileAsync_EncodesIdInPath()
    {
        // WireMock matches decoded paths; %2F becomes /
        _server.Given(Request.Create().WithPath("/api/config/agent-profiles/id/slash").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(200));

        await _client.DeleteAgentProfileAsync("id/slash");

        _server.LogEntries.Should().HaveCount(1);
    }

    // ── ResetReviewerConfigsToDefaultAsync ────────────────────────────────────

    [Fact]
    public async Task ResetReviewerConfigsToDefaultAsync_SendsPostToCorrectPath()
    {
        StubPost("/api/config/reviewer-configs/reset-to-defaults");

        await _client.ResetReviewerConfigsToDefaultAsync();

        _server.LogEntries.Should().Contain(e =>
            e.RequestMessage!.Method == "POST" &&
            e.RequestMessage.Path == "/api/config/reviewer-configs/reset-to-defaults");
    }

    // ── SaveProjectAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task SaveProjectAsync_SendsPutWithBody()
    {
        StubPut("/api/config/projects");

        var project = new PipelineProject { Id = "p1", Name = "Project" };
        await _client.SaveProjectAsync(project);

        var entry = _server.LogEntries.First(e =>
            e.RequestMessage!.Method == "PUT" &&
            e.RequestMessage.Path == "/api/config/projects");
        entry.RequestMessage!.Body.Should().Contain("p1");
    }

    // ── SetKeyValueAsync ───────────────────────────────────────────────────────

    [Fact]
    public async Task SetKeyValueAsync_SendsPutToKeyPath()
    {
        _server.Given(Request.Create().WithPath("/api/config/key-value/my-setting").UsingPut())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("{}"));

        await _client.SetKeyValueAsync("my-setting", "some-value");

        _server.LogEntries.Should().Contain(e =>
            e.RequestMessage!.Method == "PUT" &&
            e.RequestMessage.Path == "/api/config/key-value/my-setting");
    }

    // ── DeleteKeyValueAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteKeyValueAsync_SendsDeleteToKeyPath()
    {
        _server.Given(Request.Create().WithPath("/api/config/key-value/old-key").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(200));

        await _client.DeleteKeyValueAsync("old-key");

        _server.LogEntries.Should().Contain(e =>
            e.RequestMessage!.Method == "DELETE" &&
            e.RequestMessage.Path == "/api/config/key-value/old-key");
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
    public async Task DeleteTemplateAsync_EncodesProjectAndTemplateIdInPath()
    {
        // WireMock matches decoded paths
        _server.Given(Request.Create()
                .WithPath("/api/config/projects/proj/1/templates/tmpl/1")
                .UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(200));

        await _client.DeleteTemplateAsync("proj/1", "tmpl/1");

        _server.LogEntries.Should().HaveCount(1);
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
    public async Task MoveTemplateAsync_UsesPost()
    {
        var (client, handler) = CreateWithStubHandler();
        handler.Respond = _ => Empty();

        await client.MoveTemplateAsync("src", "dst", "tmpl-1");

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.PathAndQuery.Should().Be("/api/config/templates/move");
    }

    [Fact]
    public async Task SaveTemplateAsync_RefusedByTheApi_ThrowsWithTheApiReason()
    {
        var (client, handler) = CreateWithStubHandler();
        handler.Respond = _ => JsonResponse("The repository is already used by the enabled template \"Api\".", HttpStatusCode.BadRequest);
        var template = new PipelineJobTemplate { Id = "t1", Name = "Web", IssueProviderId = "ip", RepoProviderId = "rp" };

        var act = () => client.SaveTemplateAsync("proj-1", template);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("The repository is already used by the enabled template \"Api\".");
    }

    [Fact]
    public async Task MoveTemplateAsync_UnknownTargetProject_ThrowsWithTheApiReason()
    {
        var (client, handler) = CreateWithStubHandler();
        handler.Respond = _ => JsonResponse("Project dst does not exist.", HttpStatusCode.NotFound);

        var act = () => client.MoveTemplateAsync("src", "dst", "tmpl-1");

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Project dst does not exist.");
    }

    [Fact]
    public async Task SaveTemplateAsync_ServerError_StillThrowsHttpRequestException()
    {
        var (client, handler) = CreateWithStubHandler();
        handler.Respond = _ => Empty(HttpStatusCode.InternalServerError);
        var template = new PipelineJobTemplate { Id = "t1", Name = "Web", IssueProviderId = "ip", RepoProviderId = "rp" };

        var act = () => client.SaveTemplateAsync("proj-1", template);

        await act.Should().ThrowAsync<HttpRequestException>();
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

    // ── Quality gate configs ──────────────────────────────────────────────

    [Fact]
    public async Task DeleteQualityGateConfigAsync_UsesDelete()
    {
        var (client, handler) = CreateWithStubHandler();
        handler.Respond = _ => Empty();

        await client.DeleteQualityGateConfigAsync("qg-1");

        handler.LastRequest!.Method.Should().Be(HttpMethod.Delete);
    }

    // ── Projects ──────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteProjectAsync_UsesDeleteWithEscapedId()
    {
        var (client, handler) = CreateWithStubHandler();
        handler.Respond = _ => Empty();

        await client.DeleteProjectAsync("project/1");

        handler.LastRequest!.Method.Should().Be(HttpMethod.Delete);
        handler.LastRequest.RequestUri!.PathAndQuery.Should().Contain("project%2F1");
    }

    // ── Export / Import ───────────────────────────────────────────────────

    [Fact]
    public async Task ImportConfigAsync_UsesMultipartPost()
    {
        var (client, handler) = CreateWithStubHandler();
        handler.Respond = _ => Empty();

        using var stream = new MemoryStream([0x7B, 0x7D]);
        await client.ImportConfigAsync(stream, "config.json");

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.PathAndQuery.Should().Be("/api/config/import");
        handler.LastRequest.Content.Should().BeOfType<MultipartFormDataContent>();
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
