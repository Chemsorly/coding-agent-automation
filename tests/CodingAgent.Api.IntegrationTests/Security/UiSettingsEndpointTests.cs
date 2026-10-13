using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using CodingAgent.Contracts.Auth;
using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Security;

/// <summary>
/// Spec 051 2.1b: Settings on the UI surface. Admin only; project secrets and MCP server env values
/// and headers come back masked (D4, T4), and saving the mask keeps the stored value.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class UiSettingsEndpointTests(ApiWebApplicationFactory factory)
{
    // Not ProjectB: UiSurfaceTests expects that one to stay unknown.
    private const string ProjectId = "6f1c2a9e-0000-0000-0000-0000000000d1";
    private const string ProfileId = "6f1c2a9e-0000-0000-0000-0000000000d4";
    private const string SecretCanary = "canary-settings-secret-71aa";
    private const string EnvCanary = "canary-settings-env-82bb";
    private const string HeaderCanary = "canary-settings-header-93cc";

    private static readonly System.Text.Json.JsonSerializerOptions Json = PipelineJsonOptions.Default;

    private HttpClient Client(AccessGrant grant)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestUserTokens.For(grant));
        return client;
    }

    private HttpClient Admin => Client(TestUserTokens.Global(AccessRole.Admin));

    private static McpServerConfig Grafana(string env, string header) => new()
    {
        Name = "grafana",
        Env = new Dictionary<string, string> { ["GRAFANA_TOKEN"] = env },
        Headers = new Dictionary<string, string> { ["Authorization"] = header },
    };

    private async Task<PipelineProject> SeedProjectAsync()
    {
        var project = new PipelineProject
        {
            Id = ProjectId,
            Name = "Delta",
            Secrets = new Dictionary<string, string> { ["API_TOKEN"] = SecretCanary, ["OTHER"] = "other-value" },
            McpServers = [Grafana(EnvCanary, HeaderCanary)],
        };
        await factory.Services.GetRequiredService<IProjectStore>().SaveProjectAsync(project, CancellationToken.None);
        return project;
    }

    private async Task<PipelineProject> StoredProjectAsync() =>
        (await factory.Services.GetRequiredService<IProjectStore>().GetProjectByIdAsync(ProjectId, CancellationToken.None))!;

    [Theory]
    [InlineData(AccessRole.ReadOnly)]
    [InlineData(AccessRole.Operator)]
    public async Task Settings_RefuseEveryoneButAdmin(AccessRole role)
    {
        var client = Client(TestUserTokens.Global(role));

        (await client.GetAsync("/api/ui/settings/pipeline")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync("/api/ui/settings/projects")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsync("/api/ui/settings/first-run/dismiss", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Settings_RefuseAProjectOperator()
    {
        var client = Client(TestUserTokens.Scoped(ProjectId, AccessRole.Operator));

        (await client.GetAsync($"/api/ui/settings/projects/{ProjectId}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Projects_MaskSecretsAndMcpEnvAndHeaders()
    {
        await SeedProjectAsync();

        var list = await Admin.GetStringAsync("/api/ui/settings/projects");
        var single = await Admin.GetStringAsync($"/api/ui/settings/projects/{ProjectId}");

        foreach (var body in new[] { list, single })
            body.Should().NotContain(SecretCanary).And.NotContain(EnvCanary).And.NotContain(HeaderCanary).And.NotContain("other-value");
        var project = System.Text.Json.JsonSerializer.Deserialize<PipelineProject>(single, Json)!;
        project.Secrets.Should().Equal(new Dictionary<string, string> { ["API_TOKEN"] = "****", ["OTHER"] = "****" });
        project.McpServers!.Single().Env["GRAFANA_TOKEN"].Should().Be("****");
        project.McpServers!.Single().Headers["Authorization"].Should().Be("****");
    }

    [Fact]
    public async Task SavingTheMaskedForm_KeepsStoredSecrets_AppliesChanges_AndDropsRemovedKeys()
    {
        await SeedProjectAsync();
        var masked = await Admin.GetFromJsonAsync<PipelineProject>($"/api/ui/settings/projects/{ProjectId}", Json);

        var edited = masked! with
        {
            Name = "Delta renamed",
            Secrets = new Dictionary<string, string> { ["API_TOKEN"] = "****", ["NEW_KEY"] = "new-value" },
        };
        var response = await Admin.PutAsJsonAsync("/api/ui/settings/projects", edited, Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await StoredProjectAsync();
        stored.Name.Should().Be("Delta renamed");
        stored.Secrets.Should().Equal(new Dictionary<string, string> { ["API_TOKEN"] = SecretCanary, ["NEW_KEY"] = "new-value" });
        stored.McpServers!.Single().Env["GRAFANA_TOKEN"].Should().Be(EnvCanary);
        stored.McpServers!.Single().Headers["Authorization"].Should().Be(HeaderCanary);
    }

    [Fact]
    public async Task AMaskWithoutAStoredValue_IsRefused_AndNothingIsStored()
    {
        await SeedProjectAsync();
        var masked = await Admin.GetFromJsonAsync<PipelineProject>($"/api/ui/settings/projects/{ProjectId}", Json);

        var renamedServer = masked! with { McpServers = [masked.McpServers!.Single() with { Name = "grafana-2" }] };
        var response = await Admin.PutAsJsonAsync("/api/ui/settings/projects", renamedServer, Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("grafana-2").And.Contain("GRAFANA_TOKEN");
        (await StoredProjectAsync()).McpServers!.Single().Name.Should().Be("grafana");
    }

    [Fact]
    public async Task AgentProfiles_MaskMcpEnv_AndSavingTheMaskKeepsIt()
    {
        var store = factory.Services.GetRequiredService<IAgentProfileStore>();
        await store.SaveAgentProfileAsync(new AgentProfile
        {
            Id = ProfileId,
            DisplayName = "d4",
            AgentProviderConfigId = "ap-1",
            MatchLabels = ["d4"],
            McpServers = [Grafana(EnvCanary, HeaderCanary)],
        }, CancellationToken.None);

        var body = await Admin.GetStringAsync("/api/ui/settings/agent-profiles");
        body.Should().NotContain(EnvCanary).And.NotContain(HeaderCanary);

        var profile = System.Text.Json.JsonSerializer.Deserialize<List<AgentProfile>>(body, Json)!.Single(p => p.Id == ProfileId);
        (await Admin.PutAsJsonAsync("/api/ui/settings/agent-profiles", profile with { DisplayName = "d4 renamed" }, Json))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var stored = (await store.LoadAgentProfilesAsync(CancellationToken.None)).Single(p => p.Id == ProfileId);
        stored.DisplayName.Should().Be("d4 renamed");
        stored.McpServers.Single().Env["GRAFANA_TOKEN"].Should().Be(EnvCanary);
    }

    [Fact]
    public async Task Export_IsTheOnlyUnredactedRead()
    {
        // Spec 051 Q1: the admin's backup and restore file keeps live credentials.
        await SeedProjectAsync();

        (await Admin.GetStringAsync("/api/ui/settings/export")).Should().Contain(SecretCanary);
    }

    [Fact]
    public async Task FirstRun_ReportsAndStoresTheDismissal()
    {
        (await Admin.PostAsync("/api/ui/settings/first-run/dismiss", null)).StatusCode.Should().Be(HttpStatusCode.OK);

        var state = await Admin.GetFromJsonAsync<UiFirstRunState>("/api/ui/settings/first-run", Json);

        state!.BannerDismissed.Should().BeTrue();
        using var scope = factory.Services.CreateScope();
        (await scope.ServiceProvider.GetRequiredService<IKeyValueStore>().GetAsync("first_run_banner_dismissed", CancellationToken.None))
            .Should().Be("true");
    }

    [Fact]
    public async Task PipelineConfig_RoundTrips_ThroughTheSharedValidation()
    {
        var config = await Admin.GetFromJsonAsync<PipelineConfiguration>("/api/ui/settings/pipeline", Json);

        (await Admin.PutAsJsonAsync("/api/ui/settings/pipeline", config! with { MaxRetries = -1 }, Json))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "the service surface's validation applies");
        (await Admin.PutAsJsonAsync("/api/ui/settings/pipeline", config, Json)).StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
