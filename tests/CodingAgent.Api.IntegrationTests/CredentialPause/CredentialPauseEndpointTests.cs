using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using AwesomeAssertions;
using CodingAgent.Api.IntegrationTests.Security;
using CodingAgent.Contracts.Auth;
using CodingAgent.Contracts.Ui;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.CredentialPause;

/// <summary>An API host with one job template per tool and a profile on each (Claude twice: subscription and API key).</summary>
public sealed class CredentialPauseWebApplicationFactory : ApiWebApplicationFactory
{
    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.RemoveAll<JobTemplateStore>();
        services.AddSingleton(JobTemplateStore.LoadFromYaml("""
            - labels: "kiro,dotnet"
              image: "agent:test"
              providerType: "kiro"
            - labels: "opencode"
              image: "agent:test"
              providerType: "opencode"
            - labels: "claude"
              image: "agent:test"
              providerType: "claude"
            """));
    }

    /// <summary>Saves the four profiles of the tests (idempotent).</summary>
    public async Task SeedProfilesAsync()
    {
        var store = Services.GetRequiredService<IAgentProfileStore>();
        foreach (var profile in Profiles)
            await store.SaveAgentProfileAsync(profile, CancellationToken.None);
    }

    public static IReadOnlyList<AgentProfile> Profiles { get; } =
    [
        new() { Id = "56000000-0000-0000-0000-000000000001", DisplayName = "Kiro dotnet", JobTemplate = "kiro,dotnet" },
        new() { Id = "56000000-0000-0000-0000-000000000002", DisplayName = "OpenCode", JobTemplate = "opencode" },
        new() { Id = "56000000-0000-0000-0000-000000000003", DisplayName = "Claude sub", JobTemplate = "claude", Settings = new Dictionary<string, string> { ["authMode"] = "subscription" } },
        new() { Id = "56000000-0000-0000-0000-000000000004", DisplayName = "Claude key", JobTemplate = "claude", Settings = new Dictionary<string, string> { ["authMode"] = "apiKey" } },
    ];
}

/// <summary>Spec 056 req 2.4, 2.6, 8.1, 9.5, D11, D12, design "API Endpoints" (tasks 2.3, 2.4, 2.6).</summary>
[Collection(CredentialPauseCollection.Name)]
public sealed class CredentialPauseEndpointTests : IClassFixture<CredentialPauseWebApplicationFactory>
{
    private const string Route = "/api/ui/agent-credentials";
    private readonly CredentialPauseWebApplicationFactory _factory;

    public CredentialPauseEndpointTests(CredentialPauseWebApplicationFactory factory)
    {
        _factory = factory;
        factory.SeedProfilesAsync().GetAwaiter().GetResult();
        foreach (var credential in AgentCredentials.All)
            Store.ResumeAsync(credential, "test", CancellationToken.None).GetAwaiter().GetResult();
    }

    private CredentialPauseStore Store => _factory.Services.GetRequiredService<CredentialPauseStore>();

    private HttpClient Client(AccessGrant grant)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestUserTokens.For(grant));
        return client;
    }

    private HttpClient Operator => Client(TestUserTokens.Global(AccessRole.Operator));

    [Fact]
    public async Task Get_ListsEveryCredentialInUse_WithItsProfiles_Req2_4()
    {
        var page = await Client(TestUserTokens.Global(AccessRole.ReadOnly))
            .GetFromJsonAsync<UiAgentCredentialsPage>(Route, PipelineJsonOptions.Default);

        page!.Credentials.Select(c => c.Credential).Should().Equal(
            AgentCredentials.Kiro, AgentCredentials.ClaudeSubscription, AgentCredentials.ClaudeApiKey, AgentCredentials.Opencode);
        page.Credentials.Should().OnlyContain(c => !c.Paused && c.Text == null);
        page.Credentials.Single(c => c.Credential == AgentCredentials.ClaudeApiKey).Profiles.Should().Equal("Claude key");
    }

    [Fact]
    public async Task Pause_ThenGet_ShowsThePause_ThenResume_Clears_Req2_4()
    {
        var until = DateTimeOffset.UtcNow.AddDays(2);

        var paused = await Operator.PostAsJsonAsync($"{Route}/kiro/pause", new UiCredentialPauseRequest(until, "monthly limit"), PipelineJsonOptions.Default);

        paused.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var kiro = (await Operator.GetFromJsonAsync<UiAgentCredentialsPage>(Route, PipelineJsonOptions.Default))!
            .Credentials.Single(c => c.Credential == AgentCredentials.Kiro);
        kiro.Paused.Should().BeTrue();
        kiro.Reason.Should().Be(CredentialPauseReasons.Manual);
        kiro.Source.Should().Be(CredentialPauseSources.Manual);
        kiro.Detail.Should().Be("monthly limit");
        kiro.PausedBy.Should().Be("tester");
        kiro.Until.Should().BeCloseTo(until, TimeSpan.FromSeconds(1));
        kiro.Text.Should().StartWith("Kiro paused — paused by tester");

        (await Operator.PostAsync($"{Route}/kiro/resume", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await Store.LoadAsync(AgentCredentials.Kiro, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Pause_WithoutBody_PausesUntilResumed()
    {
        (await Operator.PostAsync($"{Route}/opencode/pause", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await Store.LoadAsync(AgentCredentials.Opencode, CancellationToken.None))!.Until.Should().BeNull();
    }

    [Fact]
    public async Task Resume_WhenNotPaused_Is204() =>
        (await Operator.PostAsync($"{Route}/claude-api-key/resume", null)).StatusCode.Should().Be(HttpStatusCode.NoContent);

    [Theory]
    [InlineData("pause")]
    [InlineData("resume")]
    public async Task UnknownCredential_Is404WithTheReason(string action)
    {
        var response = await Operator.PostAsync($"{Route}/codex/{action}", null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Unknown agent credential 'codex'");
    }

    [Fact]
    public async Task Pause_UntilInThePast_Is400() =>
        (await Operator.PostAsJsonAsync($"{Route}/kiro/pause", new UiCredentialPauseRequest(DateTimeOffset.UtcNow.AddMinutes(-1), null), PipelineJsonOptions.Default))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

    [Fact]
    public async Task Pause_NoteOver200Characters_Is400_Req9_5()
    {
        var response = await Operator.PostAsJsonAsync($"{Route}/kiro/pause", new UiCredentialPauseRequest(null, new string('n', 201)), PipelineJsonOptions.Default);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Store.LoadAsync(AgentCredentials.Kiro, CancellationToken.None)).Should().BeNull();
    }

    // T2 (D11): pausing needs global operate; reading is open to every UI role.
    [Theory]
    [InlineData(false, AccessRole.ReadOnly)]
    [InlineData(true, AccessRole.Admin)]
    [InlineData(true, AccessRole.Operator)]
    public async Task ProjectScopedUsersAndGlobalReadOnly_Get403OnPauseAndResume_200OnGet_T2(bool scoped, AccessRole role)
    {
        var client = Client(scoped ? TestUserTokens.Scoped("project-1", role) : TestUserTokens.Global(role));

        (await client.GetAsync(Route)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync($"{Route}/kiro/pause", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PostAsync($"{Route}/kiro/resume", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Store.LoadAsync(AgentCredentials.Kiro, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task SchedulerEndpoint_AnswersThePausedStatePerCredential_Req8_1()
    {
        await Operator.PostAsync($"{Route}/claude-subscription/pause", null);
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);

        var status = await client.GetFromJsonAsync<List<AgentCredentialStatus>>("/api/agents/credentials", PipelineJsonOptions.Default);

        status.Should().BeEquivalentTo([
            new AgentCredentialStatus(AgentCredentials.Kiro, false),
            new AgentCredentialStatus(AgentCredentials.ClaudeSubscription, true),
            new AgentCredentialStatus(AgentCredentials.ClaudeApiKey, false),
            new AgentCredentialStatus(AgentCredentials.Opencode, false),
        ]);
    }

    // D12, req 2.6: a pause is not in the export and survives an import.
    [Fact]
    public async Task Pause_IsNotExported_AndSurvivesAnImport_D12()
    {
        await Operator.PostAsync($"{Route}/kiro/pause", null);
        var service = _factory.CreateClient();
        service.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);

        var exported = await service.GetStringAsync("/api/config/export");
        exported.Should().NotContain("agent-credential-pause");

        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(exported));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "file", "bundle.json");
        (await service.PostAsync("/api/config/import", content)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Store.LoadAsync(AgentCredentials.Kiro, CancellationToken.None)).Should().NotBeNull();
    }
}
