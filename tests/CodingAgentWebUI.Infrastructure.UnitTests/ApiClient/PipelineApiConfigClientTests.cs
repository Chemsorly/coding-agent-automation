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
/// HTTP-level unit tests for <see cref="PipelineApiConfigClient"/> via WireMock.
/// Exercises all branches that cannot be reached by mocking <see cref="IPipelineApiConfigClient"/> —
/// specifically 404/null-return paths, null-guard throws, error-body paths, and URL encoding.
/// </summary>
public sealed class PipelineApiConfigClientTests : IDisposable
{
    private readonly WireMockServer _server;
    private readonly PipelineApiConfigClient _sut;

    private static readonly JsonSerializerOptions JsonOpts = PipelineJsonOptions.Default;

    public PipelineApiConfigClientTests()
    {
        _server = WireMockServer.Start();
        var http = new HttpClient { BaseAddress = new Uri(_server.Url!) };
        _sut = new PipelineApiConfigClient(http);
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }

    // ── GetPipelineConfigAsync ───────────────────────────────────────────

    [Fact]
    public async Task GetPipelineConfigAsync_Returns_Deserialized_Config()
    {
        var config = new PipelineConfiguration { WorkspaceBaseDirectory = "/data" };
        StubGetJson("/api/config/pipeline", config);

        var result = await _sut.GetPipelineConfigAsync();

        result.WorkspaceBaseDirectory.Should().Be("/data");
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

        await _sut.Invoking(c => c.GetPipelineConfigAsync())
            .Should().ThrowAsync<InvalidOperationException>();
    }

    // ── SavePipelineConfigAsync / UpdatePipelineConfigAsync ──────────────

    [Fact]
    public async Task SavePipelineConfigAsync_PutsToCorrectEndpoint()
    {
        StubPutNoContent("/api/config/pipeline");

        var config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" };
        await _sut.SavePipelineConfigAsync(config);

        _server.LogEntries.Should().HaveCount(1);
        _server.LogEntries[0].RequestMessage!.Path.Should().Be("/api/config/pipeline");
    }

    [Fact]
    public async Task UpdatePipelineConfigAsync_FetchesThenSaves()
    {
        var original = new PipelineConfiguration { WorkspaceBaseDirectory = "/old" };
        StubGetJson("/api/config/pipeline", original);
        StubPutNoContent("/api/config/pipeline");

        await _sut.UpdatePipelineConfigAsync(c => c with { WorkspaceBaseDirectory = "/new" });

        // GET then PUT
        _server.LogEntries.Should().HaveCount(2);
    }

    // ── GetProviderConfigsAsync / GetProviderConfigsWithSecretsAsync ──────

    [Fact]
    public async Task GetProviderConfigsAsync_ReturnsDeserializedList()
    {
        var configs = new[]
        {
            new { id = "p1", kind = "Repository", providerType = "GitHub", displayName = "Repo" }
        };
        _server.Given(Request.Create().WithPath("/api/config/provider-configs").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(configs)));

        var result = await _sut.GetProviderConfigsAsync(ProviderKind.Repository);

        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetProviderConfigsAsync_NullApiResponse_ReturnsEmptyList()
    {
        _server.Given(Request.Create().WithPath("/api/config/provider-configs").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("null"));

        var result = await _sut.GetProviderConfigsAsync(ProviderKind.Repository);

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

        await _sut.GetProviderConfigsWithSecretsAsync(ProviderKind.Agent);

        var queryString = _server.LogEntries[0].RequestMessage!.RawQuery;
        queryString.Should().Contain("includeSecrets=True");
    }

    // ── GetProjectByIdAsync ──────────────────────────────────────────────

    [Fact]
    public async Task GetProjectByIdAsync_ExistingId_ReturnsProject()
    {
        var project = new PipelineProject { Id = "proj-1", Name = "My Project" };
        StubGetJson("/api/config/projects/proj-1", project);

        var result = await _sut.GetProjectByIdAsync("proj-1");

        result.Should().NotBeNull();
        result!.Name.Should().Be("My Project");
    }

    [Fact]
    public async Task GetProjectByIdAsync_NotFound_ReturnsNull()
    {
        _server.Given(Request.Create().WithPath("/api/config/projects/missing").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var result = await _sut.GetProjectByIdAsync("missing");

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetProjectByIdAsync_EncodesIdInPath()
    {
        var project = new PipelineProject { Id = "id/with/slash", Name = "Encoded" };
        // HttpClient encodes "/" as "%2F". WireMock matches the decoded path "/api/config/projects/id/with/slash"
        StubGetJson("/api/config/projects/id/with/slash", project);

        var result = await _sut.GetProjectByIdAsync("id/with/slash");

        result.Should().NotBeNull();
    }

    // ── GetKeyValueAsync / SetKeyValueAsync / DeleteKeyValueAsync ────────

    [Fact]
    public async Task GetKeyValueAsync_ExistingKey_ReturnsValue()
    {
        _server.Given(Request.Create().WithPath("/api/config/key-value/mykey").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("\"myvalue\""));

        var result = await _sut.GetKeyValueAsync("mykey");

        result.Should().Be("myvalue");
    }

    [Fact]
    public async Task GetKeyValueAsync_NotFound_ReturnsNull()
    {
        _server.Given(Request.Create().WithPath("/api/config/key-value/nokey").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(404));

        var result = await _sut.GetKeyValueAsync("nokey");

        result.Should().BeNull();
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

        var result = await _sut.GetKeyValueAsync("has space");

        result.Should().Be("ok");
    }

    [Fact]
    public async Task SetKeyValueAsync_PutsToCorrectPath()
    {
        StubPutNoContent("/api/config/key-value/k1");

        await _sut.SetKeyValueAsync("k1", "v1");

        _server.LogEntries.Should().HaveCount(1);
        _server.LogEntries[0].RequestMessage!.Path.Should().Be("/api/config/key-value/k1");
    }

    [Fact]
    public async Task DeleteKeyValueAsync_SendsDelete()
    {
        _server.Given(Request.Create().WithPath("/api/config/key-value/delkey").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(200));

        await _sut.DeleteKeyValueAsync("delkey");

        _server.LogEntries.Should().HaveCount(1);
    }

    // ── GetModelsAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetModelsAsync_Success_ReturnsModels()
    {
        // AgentModelInfo: ModelId, Description, RateMultiplier
        var models = new[] { new { modelId = "gpt-4o", description = "GPT-4o", rateMultiplier = 1.0 } };
        _server.Given(Request.Create().WithPath("/api/config/models").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(models)));

        var (result, error) = await _sut.GetModelsAsync();

        result.Should().HaveCount(1);
        result[0].ModelId.Should().Be("gpt-4o");
        error.Should().BeNull();
    }

    [Fact]
    public async Task GetModelsAsync_NonSuccess_ReturnsEmptyModelsAndErrorMessage()
    {
        _server.Given(Request.Create().WithPath("/api/config/models").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(503)
                .WithHeader("Content-Type", "text/plain")
                .WithBody("Service Unavailable"));

        var (result, error) = await _sut.GetModelsAsync();

        result.Should().BeEmpty("non-2xx response returns empty models list");
        error.Should().Contain("Service Unavailable");
    }

    [Fact]
    public async Task GetModelsAsync_NullApiResponse_ReturnsEmptyList()
    {
        _server.Given(Request.Create().WithPath("/api/config/models").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("null"));

        var (result, error) = await _sut.GetModelsAsync();

        result.Should().BeEmpty();
        error.Should().BeNull();
    }

    // ── Agent profiles ───────────────────────────────────────────────────

    [Fact]
    public async Task GetAgentProfilesAsync_NullResponse_ReturnsEmpty()
    {
        _server.Given(Request.Create().WithPath("/api/config/agent-profiles").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("null"));

        var result = await _sut.GetAgentProfilesAsync();

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteAgentProfileAsync_EncodesIdInPath()
    {
        // WireMock matches decoded paths; %2F becomes /
        _server.Given(Request.Create().WithPath("/api/config/agent-profiles/id/slash").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(200));

        await _sut.DeleteAgentProfileAsync("id/slash");

        _server.LogEntries.Should().HaveCount(1);
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

        var result = await _sut.GetAllTemplatesAsync();

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

        await _sut.DeleteTemplateAsync("proj/1", "tmpl/1");

        _server.LogEntries.Should().HaveCount(1);
    }

    // ── ResetReviewerConfigsToDefaultAsync ───────────────────────────────

    [Fact]
    public async Task ResetReviewerConfigsToDefaultAsync_PostsToCorrectEndpoint()
    {
        _server.Given(Request.Create()
                .WithPath("/api/config/reviewer-configs/reset-to-defaults")
                .UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        await _sut.ResetReviewerConfigsToDefaultAsync();

        _server.LogEntries.Should().HaveCount(1);
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

        var result = await _sut.GetQualityGateConfigsAsync();

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

        var result = await _sut.GetReviewerConfigsAsync();

        result.Should().BeEmpty();
    }

    // ── HasEnabledTemplatesAsync ──────────────────────────────────────────

    [Fact]
    public async Task HasEnabledTemplatesAsync_ReturnsTrue()
    {
        _server.Given(Request.Create().WithPath("/api/config/projects/has-enabled-templates").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("true"));

        var result = await _sut.HasEnabledTemplatesAsync();

        result.Should().BeTrue();
    }

    [Fact]
    public async Task HasEnabledTemplatesAsync_ReturnsFalse()
    {
        _server.Given(Request.Create().WithPath("/api/config/projects/has-enabled-templates").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody("false"));

        var result = await _sut.HasEnabledTemplatesAsync();

        result.Should().BeFalse();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private void StubGetJson(string path, object body)
    {
        _server.Given(Request.Create().WithPath(path).UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(JsonSerializer.Serialize(body, JsonOpts)));
    }

    private void StubPutNoContent(string path)
    {
        _server.Given(Request.Create().WithPath(path).UsingPut())
            .RespondWith(Response.Create().WithStatusCode(204));
    }
}
