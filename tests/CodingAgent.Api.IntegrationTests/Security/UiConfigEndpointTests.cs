using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using CodingAgent.Contracts.Auth;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Security;

/// <summary>
/// Spec 051 2.1a: the configuration reads of the UI surface. A project-scoped user sees only its
/// own projects (T3), and no Settings-only value (secrets, MCP server env) leaves the API (T4).
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class UiConfigEndpointTests(ApiWebApplicationFactory factory)
{
    // Project C exists but is never granted. Project B stays unknown: UiSurfaceTests relies on that.
    private const string ProjectC = "6f1c2a9e-0000-0000-0000-00000000000c";
    private const string TemplateA = "7a000000-0000-0000-0000-00000000000a";
    private const string TemplateC = "7a000000-0000-0000-0000-00000000000c";
    private const string SecretCanary = "canary-project-secret-5e1f";
    private const string McpEnvCanary = "canary-mcp-env-9b2c";

    private static readonly System.Text.Json.JsonSerializerOptions Json = new(System.Text.Json.JsonSerializerDefaults.Web);

    private HttpClient Client(AccessGrant grant)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestUserTokens.For(grant));
        return client;
    }

    private async Task SeedAsync()
    {
        var store = factory.Services.GetRequiredService<IProjectStore>();
        await store.SaveProjectAsync(new PipelineProject
        {
            Id = TestUserTokens.ProjectA,
            Name = "Alpha",
            Secrets = new Dictionary<string, string> { ["API_TOKEN"] = SecretCanary },
            McpServers = [new McpServerConfig { Name = "grafana", Env = new Dictionary<string, string> { ["GRAFANA_TOKEN"] = McpEnvCanary } }],
        }, CancellationToken.None);
        await store.SaveProjectAsync(new PipelineProject { Id = ProjectC, Name = "Charlie", Enabled = false }, CancellationToken.None);
        await store.SaveTemplateAsync(TestUserTokens.ProjectA,
            new PipelineJobTemplate { Id = TemplateA, Name = "alpha-api", IssueProviderId = "ip", RepoProviderId = "rp" }, CancellationToken.None);
        await store.SaveTemplateAsync(ProjectC,
            new PipelineJobTemplate { Id = TemplateC, Name = "charlie-api", IssueProviderId = "ip", RepoProviderId = "rp", Enabled = false }, CancellationToken.None);
    }

    [Fact]
    public async Task Projects_AGlobalReaderSeesAll_WithoutSecrets()
    {
        await SeedAsync();

        var response = await Client(TestUserTokens.Global(AccessRole.ReadOnly)).GetAsync("/api/ui/projects");
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var projects = System.Text.Json.JsonSerializer.Deserialize<List<UiProject>>(body, Json)!;
        projects.Should().Contain(new UiProject(TestUserTokens.ProjectA, "Alpha", true));
        projects.Should().Contain(new UiProject(ProjectC, "Charlie", false));
        body.Should().NotContain(SecretCanary).And.NotContain(McpEnvCanary);
    }

    [Fact]
    public async Task Projects_AScopedUserSeesOnlyItsOwn()
    {
        await SeedAsync();

        var projects = await Client(TestUserTokens.Scoped(TestUserTokens.ProjectA, AccessRole.ReadOnly))
            .GetFromJsonAsync<List<UiProject>>("/api/ui/projects", Json);

        projects.Should().Equal(new UiProject(TestUserTokens.ProjectA, "Alpha", true));
    }

    [Fact]
    public async Task Templates_OfAReadableProject_AreListed()
    {
        await SeedAsync();

        var templates = await Client(TestUserTokens.Scoped(TestUserTokens.ProjectA, AccessRole.ReadOnly))
            .GetFromJsonAsync<List<UiTemplate>>($"/api/ui/projects/{TestUserTokens.ProjectA}/templates", Json);

        templates.Should().Equal(new UiTemplate(TemplateA, "alpha-api", true));
    }

    [IdorCase("GET /api/ui/projects/{projectId}/templates")]
    [Fact]
    public async Task Templates_OfAnotherProject_Are404_LikeAMissingProject()
    {
        await SeedAsync();
        var scoped = Client(TestUserTokens.Scoped(TestUserTokens.ProjectA, AccessRole.Operator));

        (await scoped.GetAsync($"/api/ui/projects/{ProjectC}/templates")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "the project exists, but the user may not know that");
        (await scoped.GetAsync("/api/ui/projects/6f1c2a9e-0000-0000-0000-0000000000ff/templates")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Templates_OfAMissingProject_Are404_ForAGlobalReader()
    {
        var admin = Client(TestUserTokens.Global(AccessRole.Admin));

        (await admin.GetAsync("/api/ui/projects/6f1c2a9e-0000-0000-0000-0000000000ff/templates")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await admin.GetAsync("/api/ui/projects/not-a-project/templates")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task PipelineSettings_CarryTheGlobalValues()
    {
        var stored = await factory.Services.GetRequiredService<IPipelineConfigStore>().LoadPipelineConfigAsync(CancellationToken.None);

        var settings = await Client(TestUserTokens.Scoped(TestUserTokens.ProjectA, AccessRole.ReadOnly))
            .GetFromJsonAsync<UiPipelineSettings>("/api/ui/pipeline-settings", Json);

        settings.Should().Be(new UiPipelineSettings(stored.MaxRetries, stored.TriageRetentionDays));
    }

    [Theory]
    [InlineData("/api/ui/projects")]
    [InlineData("/api/ui/pipeline-settings")]
    [InlineData("/api/ui/projects/6f1c2a9e-0000-0000-0000-00000000000a/templates")]
    public async Task ConfigReads_RefuseATokenWithoutAnyRole(string path)
    {
        (await Client(AccessGrant.None).GetAsync(path)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
