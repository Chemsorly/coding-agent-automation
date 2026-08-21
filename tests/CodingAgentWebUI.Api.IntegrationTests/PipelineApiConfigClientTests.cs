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
        _server.Given(Request.Create().WithPath("/api/config/key-value/missing-key").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var result = await _client.GetKeyValueAsync("missing-key");

        result.Should().BeNull();
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
}
