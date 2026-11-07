using System.Text;
using AwesomeAssertions;
using CodingAgent.Contracts.Auth;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;
using CodingAgent.Web.Ui;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.Security;

/// <summary>
/// Spec 051: the web's UI clients (CodingAgent.Web.Client) against the real API. They serialize with
/// source-generated metadata, not the API's runtime options, so this checks that both sides agree on
/// every path, verb and payload, and that a refusal reaches the page as the API's reason.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class UiClientContractTests(ApiWebApplicationFactory factory)
{
    private const string ProjectX = "6f1c2a9e-0000-0000-0000-0000000000e1";
    private const string ProjectY = "6f1c2a9e-0000-0000-0000-0000000000e2";
    private const string TemplateX = "7a000000-0000-0000-0000-0000000000e1";

    private sealed class FixedToken(string token) : IUiAccessTokenSource
    {
        public ValueTask<string?> GetTokenAsync(CancellationToken ct = default) => ValueTask.FromResult<string?>(token);
    }

    private static FixedToken Token(AccessGrant grant) => new(TestUserTokens.For(grant));

    private UiSettingsClient Settings(AccessGrant? grant = null) =>
        new(factory.CreateClient(), Token(grant ?? TestUserTokens.Global(AccessRole.Admin)));

    private UiConfigClient Config(AccessGrant grant) => new(factory.CreateClient(), Token(grant));

    [Fact]
    public async Task PipelineConfig_ReadsAndUpdates_AndARefusalCarriesTheReason()
    {
        var settings = Settings();
        var original = await settings.GetPipelineConfigAsync();

        try
        {
            await settings.UpdatePipelineConfigAsync(c => c with { MaxRetries = original.MaxRetries == 3 ? 4 : 3 });
            (await settings.GetPipelineConfigAsync()).MaxRetries.Should().NotBe(original.MaxRetries);

            var refused = () => settings.UpdatePipelineConfigAsync(c => c with { MaxRetries = -1 });
            (await refused.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Contain("MaxRetries");
        }
        finally
        {
            await settings.UpdatePipelineConfigAsync(_ => original);
        }
    }

    [Fact]
    public async Task NonAdmin_IsRefused()
    {
        var act = () => Settings(TestUserTokens.Global(AccessRole.Operator)).GetPipelineConfigAsync();

        (await act.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);
    }

    // Spec 057 req 6.1, 6.3: the web client's capacity calls; a key with a comma round-trips unchanged.
    [Fact]
    public async Task AgentCapacity_SavesAndReads_AndARefusalCarriesTheReason()
    {
        var settings = Settings();
        try
        {
            await settings.SaveAgentCapacityAsync(new AgentCapacityLimits
            {
                MaxConcurrentAgents = 5,
                MaxConcurrentByJobTemplate = new Dictionary<string, int> { [AgentRoutingTestData.DefaultJobTemplate] = 2 },
            });

            var page = await settings.GetAgentCapacityAsync();
            page.All.UiLimit.Should().Be(5);
            page.Templates.Single(r => r.TemplateKey == AgentRoutingTestData.DefaultJobTemplate).UiLimit.Should().Be(2);

            var refused = () => settings.SaveAgentCapacityAsync(new AgentCapacityLimits { MaxConcurrentAgents = 101 });
            (await refused.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should()
                .Contain("The limit for all agents must be between 0 and 100 (was 101).");
        }
        finally
        {
            await settings.SaveAgentCapacityAsync(AgentCapacityLimits.Empty);
        }
    }

    // Spec 056 req 2.4: the web client's credential calls; an unknown credential's 404 carries the reason.
    [Fact]
    public async Task AgentCredentials_PauseReadAndResume_AndA404CarriesTheReason()
    {
        var credentials = new UiCredentialsClient(factory.CreateClient(), Token(TestUserTokens.Global(AccessRole.Operator)));
        var until = DateTimeOffset.UtcNow.AddHours(3);
        try
        {
            await credentials.PauseAsync(AgentCredentials.ClaudeApiKey, until, "contract test");

            var paused = (await credentials.GetAsync()).Credentials.Single(c => c.Credential == AgentCredentials.ClaudeApiKey);
            paused.Paused.Should().BeTrue();
            paused.Detail.Should().Be("contract test");
            paused.Until.Should().BeCloseTo(until, TimeSpan.FromSeconds(1));

            var refused = () => credentials.PauseAsync("codex", null, null);
            (await refused.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should()
                .Contain("Unknown agent credential 'codex'");
        }
        finally
        {
            await credentials.ResumeAsync(AgentCredentials.ClaudeApiKey);
        }

        (await credentials.GetAsync()).Credentials.Should().NotContain(c => c.Credential == AgentCredentials.ClaudeApiKey && c.Paused);
    }

    [Fact]
    public async Task AgentProfiles_SaveListAndDelete_KeepingTheMaskedSecret()
    {
        var settings = Settings();
        var id = Guid.NewGuid().ToString();
        await settings.SaveAgentProfileAsync(AgentRoutingTestData.Profile(id, displayName: $"contract {id[..8]}",
            mcpServers: [new McpServerConfig { Name = "grafana", Env = new Dictionary<string, string> { ["TOKEN"] = "real-token" } }]));

        var listed = (await settings.GetAgentProfilesAsync()).Profiles.Single(p => p.Id == id);
        listed.McpServers.Single().Env["TOKEN"].Should().Be("****");
        await settings.SaveAgentProfileAsync(listed with { DisplayName = $"contract 2 {id[..8]}" });

        var stored = (await factory.Services.GetRequiredService<IAgentProfileStore>().LoadAgentProfilesAsync(CancellationToken.None)).Single(p => p.Id == id);
        stored.DisplayName.Should().Be($"contract 2 {id[..8]}");
        stored.McpServers.Single().Env["TOKEN"].Should().Be("real-token");

        await settings.DeleteAgentProfileAsync(id);
        (await settings.GetAgentProfilesAsync()).Profiles.Should().NotContain(p => p.Id == id);
    }

    [Fact]
    public async Task QualityGatesAndReviewers_SaveListDeleteAndReset()
    {
        var settings = Settings();
        var gateId = Guid.NewGuid().ToString();
        await settings.SaveQualityGateConfigAsync(new QualityGateConfiguration { Id = gateId, DisplayName = "contract gate", MatchLabels = ["contract"] });
        (await settings.GetQualityGateConfigsAsync()).Should().Contain(g => g.Id == gateId);
        await settings.DeleteQualityGateConfigAsync(gateId);
        (await settings.GetQualityGateConfigsAsync()).Should().NotContain(g => g.Id == gateId);

        var reviewer = new ReviewerConfiguration
        {
            DisplayName = "contract reviewer",
            MatchLabels = ["contract"],
            Agents = [new ReviewAgent { Name = "correctness", Prompt = "Review for correctness." }],
        };
        await settings.SaveReviewerConfigAsync(reviewer);
        (await settings.GetReviewerConfigsAsync()).Should().Contain(r => r.Id == reviewer.Id && r.Agents.Single().Name == "correctness");
        await settings.DeleteReviewerConfigAsync(reviewer.Id);
        (await settings.GetReviewerConfigsAsync()).Should().NotContain(r => r.Id == reviewer.Id);
        await settings.ResetReviewerConfigsToDefaultAsync();
        (await settings.GetReviewerConfigsAsync()).Should().NotBeEmpty();
    }

    [Fact]
    public async Task Projects_AndTemplateMoves_RoundTrip()
    {
        var settings = Settings();
        await settings.SaveProjectAsync(new PipelineProject { Id = ProjectX, Name = "Echo", Secrets = new() { ["KEY"] = "value" } });
        await settings.SaveProjectAsync(new PipelineProject { Id = ProjectY, Name = "Foxtrot" });
        await factory.Services.GetRequiredService<IProjectStore>().SaveTemplateAsync(ProjectX,
            new PipelineJobTemplate { Id = TemplateX, Name = "echo-api", IssueProviderId = "ip-contract", RepoProviderId = "rp-contract" }, CancellationToken.None);

        (await settings.GetProjectByIdAsync(ProjectX))!.Secrets.Should().Equal(new Dictionary<string, string> { ["KEY"] = "****" });
        (await settings.GetProjectByIdAsync("6f1c2a9e-0000-0000-0000-0000000000ef")).Should().BeNull();
        (await settings.GetProjectsAsync()).Should().Contain(p => p.Id == ProjectY);
        (await settings.GetAllTemplatesAsync()).Should().Contain(t => t.Id == TemplateX);

        await settings.MoveTemplateAsync(ProjectX, ProjectY, TemplateX);
        (await Config(TestUserTokens.Global(AccessRole.ReadOnly)).GetTemplatesAsync(ProjectY)).Should().Contain(t => t.Id == TemplateX);

        await settings.DeleteProjectAsync(ProjectY);
        (await settings.GetProjectByIdAsync(ProjectY)).Should().BeNull();
    }

    [Fact]
    public async Task ExportAndImport_RoundTrip()
    {
        var settings = Settings();

        var bundle = await settings.ExportConfigAsync();
        Encoding.UTF8.GetString(bundle).Should().StartWith("{");

        await settings.ImportConfigAsync(new MemoryStream(bundle), "config.json");
    }

    [Fact]
    public async Task FirstRun_ReadsAndDismisses()
    {
        var settings = Settings();

        await settings.DismissFirstRunBannerAsync();

        (await settings.GetFirstRunStateAsync()).BannerDismissed.Should().BeTrue();
    }

    [Fact]
    public async Task ReadsForEveryRole_Deserialize()
    {
        var grant = TestUserTokens.Global(AccessRole.ReadOnly);

        (await Config(grant).GetProjectsAsync()).Should().NotBeNull();
        (await Config(grant).GetPipelineSettingsAsync()).MaxRetries.Should().BeGreaterThanOrEqualTo(0);
        (await new UiMeClient(factory.CreateClient(), Token(grant)).GetAsync()).GlobalRole.Should().Be("readonly");
        (await new UiHealthClient(factory.CreateClient(), Token(grant)).GetAsync()).RedisConnected.Should().BeNull();
    }
}
