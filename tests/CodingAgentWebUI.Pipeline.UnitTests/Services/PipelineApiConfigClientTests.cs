using System.Net;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for PipelineApiConfigClient — verifies HTTP method, URL construction,
/// serialisation, and special-case handling (404 → null, null response → empty list, etc.).
/// </summary>
public sealed class PipelineApiConfigClientTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    private static (IPipelineApiConfigClient Client, StubHandler Handler) Create()
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

    private static AgentProfile MakeAgentProfile(string id = "a1") => new()
    {
        Id = id,
        DisplayName = "Test Agent",
        AgentProviderConfigId = "kiro"
    };

    // ── GetPipelineConfigAsync ────────────────────────────────────────────

    [Fact]
    public async Task GetPipelineConfigAsync_ReturnsDeserializedConfig()
    {
        var (client, handler) = Create();
        var expected = new PipelineConfiguration();
        handler.Respond = _ => JsonResponse(expected);

        var result = await client.GetPipelineConfigAsync();

        result.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.PathAndQuery.Should().Be("/api/config/pipeline");
        handler.LastRequest.Method.Should().Be(HttpMethod.Get);
    }

    [Fact]
    public async Task GetPipelineConfigAsync_WhenNullResponse_Throws()
    {
        var (client, handler) = Create();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json")
        };

        var act = () => client.GetPipelineConfigAsync();
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── SavePipelineConfigAsync ───────────────────────────────────────────

    [Fact]
    public async Task SavePipelineConfigAsync_SendsPutRequest()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty();

        await client.SavePipelineConfigAsync(new PipelineConfiguration());

        handler.LastRequest!.Method.Should().Be(HttpMethod.Put);
        handler.LastRequest.RequestUri!.PathAndQuery.Should().Be("/api/config/pipeline");
    }

    // ── UpdatePipelineConfigAsync ─────────────────────────────────────────

    [Fact]
    public async Task UpdatePipelineConfigAsync_FetchesThenPuts()
    {
        var (client, handler) = Create();
        var callCount = 0;
        handler.Respond = req =>
        {
            callCount++;
            return callCount == 1
                ? JsonResponse(new PipelineConfiguration())
                : Empty();
        };

        await client.UpdatePipelineConfigAsync(c => c);

        callCount.Should().Be(2);
    }

    // ── GetProviderConfigsAsync ───────────────────────────────────────────

    [Fact]
    public async Task GetProviderConfigsAsync_ReturnsConfigs()
    {
        var (client, handler) = Create();
        handler.Respond = _ => JsonResponse(new List<ProviderConfig> { MakeProviderConfig() });

        var result = await client.GetProviderConfigsAsync(ProviderKind.Issue);

        result.Should().HaveCount(1);
        handler.LastRequest!.RequestUri!.PathAndQuery
            .Should().Contain("kind=Issue").And.Contain("includeSecrets=False");
    }

    [Fact]
    public async Task GetProviderConfigsAsync_WhenNullBody_ReturnsEmpty()
    {
        var (client, handler) = Create();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json")
        };

        var result = await client.GetProviderConfigsAsync(ProviderKind.Agent);
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetProviderConfigsWithSecretsAsync_PassesIncludeSecretsTrue()
    {
        var (client, handler) = Create();
        handler.Respond = _ => JsonResponse(new List<ProviderConfig>());

        await client.GetProviderConfigsWithSecretsAsync(ProviderKind.Repository);

        handler.LastRequest!.RequestUri!.PathAndQuery.Should().Contain("includeSecrets=True");
    }

    // ── SaveProviderConfigAsync ───────────────────────────────────────────

    [Fact]
    public async Task SaveProviderConfigAsync_SendsPut()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty();

        await client.SaveProviderConfigAsync(MakeProviderConfig());

        handler.LastRequest!.Method.Should().Be(HttpMethod.Put);
        handler.LastRequest.RequestUri!.PathAndQuery.Should().Be("/api/config/provider-configs");
    }

    // ── DeleteProviderConfigAsync ─────────────────────────────────────────

    [Fact]
    public async Task DeleteProviderConfigAsync_SendsDeleteWithEscapedId()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty();

        await client.DeleteProviderConfigAsync("my id/with slash", ProviderKind.Issue);

        handler.LastRequest!.Method.Should().Be(HttpMethod.Delete);
        handler.LastRequest.RequestUri!.PathAndQuery.Should().Contain("my%20id%2Fwith%20slash");
    }

    // ── Agent profiles ────────────────────────────────────────────────────

    [Fact]
    public async Task GetAgentProfilesAsync_ReturnsEmpty_WhenNull()
    {
        var (client, handler) = Create();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json")
        };

        var result = await client.GetAgentProfilesAsync();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetAgentProfilesAsync_ReturnsList()
    {
        var (client, handler) = Create();
        handler.Respond = _ => JsonResponse(new List<AgentProfile> { MakeAgentProfile() });

        var result = await client.GetAgentProfilesAsync();
        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task SaveAgentProfileAsync_UsesPut()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty();

        await client.SaveAgentProfileAsync(MakeAgentProfile());

        handler.LastRequest!.Method.Should().Be(HttpMethod.Put);
    }

    [Fact]
    public async Task DeleteAgentProfileAsync_UsesDelete()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty();

        await client.DeleteAgentProfileAsync("prof-1");

        handler.LastRequest!.Method.Should().Be(HttpMethod.Delete);
        handler.LastRequest.RequestUri!.PathAndQuery.Should().Contain("prof-1");
    }

    // ── Quality gate configs ──────────────────────────────────────────────

    [Fact]
    public async Task GetQualityGateConfigsAsync_ReturnsEmpty_WhenNull()
    {
        var (client, handler) = Create();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json")
        };

        var result = await client.GetQualityGateConfigsAsync();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task DeleteQualityGateConfigAsync_UsesDelete()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty();

        await client.DeleteQualityGateConfigAsync("qg-1");

        handler.LastRequest!.Method.Should().Be(HttpMethod.Delete);
    }

    // ── Reviewer configs ──────────────────────────────────────────────────

    [Fact]
    public async Task GetReviewerConfigsAsync_ReturnsEmpty_WhenNull()
    {
        var (client, handler) = Create();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json")
        };

        var result = await client.GetReviewerConfigsAsync();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ResetReviewerConfigsToDefaultAsync_UsesPost()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty();

        await client.ResetReviewerConfigsToDefaultAsync();

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.PathAndQuery
            .Should().Be("/api/config/reviewer-configs/reset-to-defaults");
    }

    // ── Projects ──────────────────────────────────────────────────────────

    [Fact]
    public async Task GetProjectsAsync_ReturnsList()
    {
        var (client, handler) = Create();
        handler.Respond = _ => JsonResponse(new List<PipelineProject>
        {
            new() { Id = "proj1", Name = "Test Project" }
        });

        var result = await client.GetProjectsAsync();
        result.Should().HaveCount(1);
    }

    [Fact]
    public async Task GetProjectByIdAsync_WhenNotFound_ReturnsNull()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty(HttpStatusCode.NotFound);

        var result = await client.GetProjectByIdAsync("missing");
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetProjectByIdAsync_WhenFound_ReturnsProject()
    {
        var (client, handler) = Create();
        handler.Respond = _ => JsonResponse(new PipelineProject { Id = "proj1", Name = "Test" });

        var result = await client.GetProjectByIdAsync("proj1");
        result!.Id.Should().Be("proj1");
    }

    [Fact]
    public async Task DeleteProjectAsync_UsesDeleteWithEscapedId()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty();

        await client.DeleteProjectAsync("project/1");

        handler.LastRequest!.Method.Should().Be(HttpMethod.Delete);
        handler.LastRequest.RequestUri!.PathAndQuery.Should().Contain("project%2F1");
    }

    [Fact]
    public async Task HasEnabledTemplatesAsync_ReturnsTrue()
    {
        var (client, handler) = Create();
        handler.Respond = _ => JsonResponse(true);

        var result = await client.HasEnabledTemplatesAsync();
        result.Should().BeTrue();
    }

    // ── Templates ─────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAllTemplatesAsync_ReturnsEmpty_WhenNull()
    {
        var (client, handler) = Create();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json")
        };

        var result = await client.GetAllTemplatesAsync();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTemplatesForProjectAsync_EncodesProjectId()
    {
        var (client, handler) = Create();
        handler.Respond = _ => JsonResponse(new List<PipelineJobTemplate>());

        await client.GetTemplatesForProjectAsync("my project");

        handler.LastRequest!.RequestUri!.PathAndQuery.Should().Contain("my%20project");
    }

    [Fact]
    public async Task MoveTemplateAsync_UsesPost()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty();

        await client.MoveTemplateAsync("src", "dst", "tmpl-1");

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.PathAndQuery.Should().Be("/api/config/templates/move");
    }

    // ── Key-value store ───────────────────────────────────────────────────

    [Fact]
    public async Task GetKeyValueAsync_WhenNotFound_ReturnsNull()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty(HttpStatusCode.NotFound);

        var result = await client.GetKeyValueAsync("missing-key");
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetKeyValueAsync_WhenFound_ReturnsValue()
    {
        var (client, handler) = Create();
        handler.Respond = _ => JsonResponse("hello");

        var result = await client.GetKeyValueAsync("mykey");
        result.Should().Be("hello");
    }

    [Fact]
    public async Task SetKeyValueAsync_UsesPut()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty();

        await client.SetKeyValueAsync("k", "v");

        handler.LastRequest!.Method.Should().Be(HttpMethod.Put);
        handler.LastRequest.RequestUri!.PathAndQuery.Should().Contain("/api/config/key-value/k");
    }

    [Fact]
    public async Task DeleteKeyValueAsync_UsesDelete()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty();

        await client.DeleteKeyValueAsync("old-key");

        handler.LastRequest!.Method.Should().Be(HttpMethod.Delete);
    }

    // ── Export / Import ───────────────────────────────────────────────────

    [Fact]
    public async Task ExportConfigAsync_ReturnsBytes()
    {
        var (client, handler) = Create();
        var bytes = new byte[] { 1, 2, 3 };
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes)
        };

        var result = await client.ExportConfigAsync();
        result.Should().BeEquivalentTo(bytes);
        handler.LastRequest!.RequestUri!.PathAndQuery.Should().Be("/api/config/export");
    }

    [Fact]
    public async Task ImportConfigAsync_UsesMultipartPost()
    {
        var (client, handler) = Create();
        handler.Respond = _ => Empty();

        using var stream = new MemoryStream([0x7B, 0x7D]);
        await client.ImportConfigAsync(stream, "config.json");

        handler.LastRequest!.Method.Should().Be(HttpMethod.Post);
        handler.LastRequest.RequestUri!.PathAndQuery.Should().Be("/api/config/import");
        handler.LastRequest.Content.Should().BeOfType<MultipartFormDataContent>();
    }

    // ── GetModelsAsync ────────────────────────────────────────────────────

    [Fact]
    public async Task GetModelsAsync_OnSuccess_ReturnsModels()
    {
        var (client, handler) = Create();
        var models = new List<AgentModelInfo> { new() { ModelId = "gpt-4" } };
        handler.Respond = _ => JsonResponse(models);

        var (result, error) = await client.GetModelsAsync();

        result.Should().HaveCount(1);
        error.Should().BeNull();
    }

    [Fact]
    public async Task GetModelsAsync_OnFailure_ReturnsErrorString()
    {
        var (client, handler) = Create();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("service down")
        };

        var (result, error) = await client.GetModelsAsync();

        result.Should().BeEmpty();
        error.Should().Be("service down");
    }

    [Fact]
    public async Task GetModelsAsync_WhenNullBody_ReturnsEmpty()
    {
        var (client, handler) = Create();
        handler.Respond = _ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("null", Encoding.UTF8, "application/json")
        };

        var (result, error) = await client.GetModelsAsync();

        result.Should().BeEmpty();
        error.Should().BeNull();
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
