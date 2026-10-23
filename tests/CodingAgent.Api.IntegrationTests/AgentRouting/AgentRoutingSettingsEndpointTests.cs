using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgent.Api.IntegrationTests.Security;
using CodingAgent.Contracts.Auth;
using CodingAgent.Contracts.Ui;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.TestUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using RoutingTable = CodingAgent.Pipeline.Models.AgentRouting;

namespace CodingAgent.Api.IntegrationTests.AgentRouting;

/// <summary>
/// Its own database, because the tests replace the routing table the collection's other tests route by, and job
/// templates for each tool plus one without a tool.
/// </summary>
public sealed class AgentRoutingSettingsFactory : ApiWebApplicationFactory
{
    public const string KiroTemplate = "dotnet,kiro";
    public const string ClaudeTemplate = "claude,dotnet";
    public const string OpenCodeTemplate = "opencode,python";
    public const string TemplateWithoutTool = "notool";

    protected override void ConfigureTestServices(IServiceCollection services)
    {
        services.RemoveAll<JobTemplateStore>();
        services.AddSingleton(AgentRoutingTestData.JobTemplates(
            (KiroTemplate, JobTemplateProviderType.Kiro),
            (ClaudeTemplate, JobTemplateProviderType.Claude),
            (OpenCodeTemplate, JobTemplateProviderType.Opencode),
            (TemplateWithoutTool, "")));
    }
}

/// <summary>
/// Spec 053 tasks 5.1–5.3: the Routing and Agent Profiles settings endpoints, repository rows on the template save,
/// the resolved routes of <c>GET /api/ui/pipelines</c> (D17), and hostile bodies (req 10.5).
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class AgentRoutingSettingsEndpointTests : IClassFixture<AgentRoutingSettingsFactory>
{
    private const string ProjectId = "6f1c2a9e-0000-0000-0000-000000000531";
    private const string OtherProjectId = "6f1c2a9e-0000-0000-0000-000000000532";
    private const string TemplateId = "7a000000-0000-0000-0000-000000000531";
    private const string OtherTemplateId = "7a000000-0000-0000-0000-000000000532";
    private const string KiroProfileId = "0e5e0000-0000-4000-8000-000000000531";
    private const string ClaudeProfileId = "0e5e0000-0000-4000-8000-000000000532";
    private const string OpenCodeProfileId = "0e5e0000-0000-4000-8000-000000000533";
    private const string DisabledProfileId = "0e5e0000-0000-4000-8000-000000000534";
    private const string RoutingUrl = "/api/ui/settings/agent-routing";
    private const string ProfilesUrl = "/api/ui/settings/agent-profiles";
    private const string TemplatesUrl = $"/api/ui/settings/projects/{ProjectId}/templates";

    private static readonly JsonSerializerOptions Json = PipelineJsonOptions.Default;
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly AgentRoutingSettingsFactory _factory;

    public AgentRoutingSettingsEndpointTests(AgentRoutingSettingsFactory factory) => _factory = factory;

    private HttpClient Client(AccessGrant grant)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestUserTokens.For(grant));
        return client;
    }

    private HttpClient Admin => Client(TestUserTokens.Global(AccessRole.Admin));

    private AgentRoutingStore Routing => _factory.Services.GetRequiredService<AgentRoutingStore>();

    private IAgentProfileStore Profiles => _factory.Services.GetRequiredService<IAgentProfileStore>();

    private IProjectStore Projects => _factory.Services.GetRequiredService<IProjectStore>();

    private static Dictionary<WorkItemTaskType, AgentRoute> Rows(WorkItemTaskType taskType, AgentRoute route) => new() { [taskType] = route };

    /// <summary>
    /// A profile per tool and a disabled one, two projects with a template each, the global routing table (by default a
    /// default route to the Kiro profile) and the first template's rows (by default none).
    /// </summary>
    private async Task SeedAsync(RoutingTable? routing = null, IReadOnlyDictionary<WorkItemTaskType, AgentRoute>? templateRows = null)
    {
        foreach (var profile in new[]
                 {
                     AgentRoutingTestData.Profile(KiroProfileId, AgentRoutingSettingsFactory.KiroTemplate, "Kiro agent"),
                     AgentRoutingTestData.Profile(ClaudeProfileId, AgentRoutingSettingsFactory.ClaudeTemplate, "Claude agent"),
                     AgentRoutingTestData.Profile(OpenCodeProfileId, AgentRoutingSettingsFactory.OpenCodeTemplate, "OpenCode agent"),
                     AgentRoutingTestData.Profile(DisabledProfileId, AgentRoutingSettingsFactory.KiroTemplate, "Disabled agent", enabled: false),
                 })
        {
            await Profiles.SaveAgentProfileAsync(profile, Ct);
        }

        await Projects.SaveProjectAsync(new PipelineProject { Id = ProjectId, Name = "Routing" }, Ct);
        await Projects.SaveProjectAsync(new PipelineProject { Id = OtherProjectId, Name = "Other" }, Ct);
        await Projects.SaveTemplateAsync(ProjectId, new PipelineJobTemplate
        {
            Id = TemplateId, Name = "routing-api", IssueProviderId = "issues-531", RepoProviderId = "repo-531", AgentRoutes = templateRows,
        }, Ct);
        await Projects.SaveTemplateAsync(OtherProjectId, new PipelineJobTemplate
        {
            Id = OtherTemplateId, Name = "other-api", IssueProviderId = "issues-532", RepoProviderId = "repo-532",
        }, Ct);
        await Routing.SaveAsync(routing ?? AgentRoutingTestData.Routing(KiroProfileId), Ct);
    }

    private async Task<PipelineJobTemplate> StoredTemplateAsync(string projectId = ProjectId, string templateId = TemplateId) =>
        (await Projects.LoadTemplatesForProjectAsync(projectId, Ct)).Single(t => t.Id == templateId);

    /// <summary>A 400 carries its reason as a JSON string.</summary>
    private static async Task<string> ReasonAsync(HttpResponseMessage response) =>
        JsonSerializer.Deserialize<string>(await response.Content.ReadAsStringAsync())!;

    private static string NewProfileId() => Guid.NewGuid().ToString();

    // ── Routing (req 2.1–2.7, 10.1) ─────────────────────────────────────────────────

    [Theory]
    [InlineData(AccessRole.ReadOnly)]
    [InlineData(AccessRole.Operator)]
    public async Task Routing_NeedsTheGlobalAdmin(AccessRole role)
    {
        await SeedAsync();

        foreach (var client in new[] { Client(TestUserTokens.Global(role)), Client(TestUserTokens.Scoped(ProjectId, AccessRole.Admin)) })
        {
            (await client.GetAsync(RoutingUrl)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
            (await client.PutAsJsonAsync(RoutingUrl, AgentRoutingTestData.Routing(ClaudeProfileId), Json))
                .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        (await Routing.LoadAsync(Ct))!.Default!.ProfileId.Should().Be(KiroProfileId);
    }

    [Fact]
    public async Task GetRouting_ReturnsTheTable_TheProfilesWithTheirTool_AndTheJobTemplates()
    {
        var routing = AgentRoutingTestData.Routing(KiroProfileId, "auto", "high",
            (WorkItemTaskType.Decomposition, AgentRoutingTestData.Route(ClaudeProfileId, "claude-opus-5-5", "max")));
        await SeedAsync(routing);

        var page = await Admin.GetFromJsonAsync<UiAgentRoutingPage>(RoutingUrl, Json);

        page!.Routing.Should().BeEquivalentTo(routing);
        page.Profiles.Should().Contain(new UiRoutingProfile(
            ClaudeProfileId, "Claude agent", JobTemplateProviderType.Claude, AgentRoutingSettingsFactory.ClaudeTemplate, true));
        page.Profiles.Should().Contain(new UiRoutingProfile(
            DisabledProfileId, "Disabled agent", JobTemplateProviderType.Kiro, AgentRoutingSettingsFactory.KiroTemplate, false));
        page.JobTemplates.Should().Contain(new UiJobTemplate(AgentRoutingSettingsFactory.KiroTemplate, JobTemplateProviderType.Kiro))
            .And.Contain(new UiJobTemplate(AgentRoutingSettingsFactory.TemplateWithoutTool, ""));
    }

    [Fact]
    public async Task PutRouting_StoresAValidTable()
    {
        await SeedAsync();
        var routing = AgentRoutingTestData.Routing(ClaudeProfileId, "claude-opus-5-5", "xhigh",
            (WorkItemTaskType.Triage, AgentRoutingTestData.Route(OpenCodeProfileId, "azure-foundry/Kimi-2.6")));

        (await Admin.PutAsJsonAsync(RoutingUrl, routing, Json)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Routing.LoadAsync(Ct)).Should().BeEquivalentTo(routing);
    }

    [Fact]
    public async Task PutRouting_RefusesEveryInvalidRow_WithOneLinePerRow_AndStoresNothing()
    {
        await SeedAsync();
        var stored = await Routing.LoadAsync(Ct);
        var routing = new RoutingTable
        {
            // A valid default unlike the stored one, so a save of the valid rows alone would show.
            Default = AgentRoutingTestData.Route(ClaudeProfileId, "claude-opus-5-5", "high"),
            ByTaskType = new Dictionary<WorkItemTaskType, AgentRoute>
            {
                [WorkItemTaskType.Decomposition] = AgentRoutingTestData.Route(ClaudeProfileId, model: " "),
                [WorkItemTaskType.Review] = AgentRoutingTestData.Route(KiroProfileId, effort: "xhigh"),
                [WorkItemTaskType.Triage] = AgentRoutingTestData.Route(OpenCodeProfileId, effort: "high"),
                [WorkItemTaskType.Consolidation] = AgentRoutingTestData.Route(DisabledProfileId),
            },
        };

        var response = await Admin.PutAsJsonAsync(RoutingUrl, routing, Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReasonAsync(response)).Split('\n').Should().BeEquivalentTo(
            "Decomposition: model is required (auto lets the tool choose)",
            "PR review: Kiro does not accept effort 'xhigh' (use auto, low, medium, high, max)",
            "Triage: OpenCode has no effort setting; use auto",
            "Consolidation: agent profile 'Disabled agent' is disabled");
        (await Routing.LoadAsync(Ct)).Should().BeEquivalentTo(stored, "an invalid table stores nothing, not even its valid rows");
    }

    [Fact]
    public async Task PutRouting_WithoutADefaultRoute_IsRefused()
    {
        await SeedAsync();

        var response = await Admin.PutAsJsonAsync(RoutingUrl, new RoutingTable(), Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReasonAsync(response)).Should().Be("Default: a default route is required");
    }

    [Fact]
    public async Task PipelineSave_LeavesTheRoutingUnchanged()
    {
        // Req 2.7: only the Routing endpoints write the table.
        var routing = AgentRoutingTestData.Routing(ClaudeProfileId, "claude-opus-5-5", "max");
        await SeedAsync(routing);
        var config = await Admin.GetFromJsonAsync<PipelineConfiguration>("/api/ui/settings/pipeline", Json);

        (await Admin.PutAsJsonAsync("/api/ui/settings/pipeline", config! with { MaxRetries = 4 }, Json))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await Routing.LoadAsync(Ct)).Should().BeEquivalentTo(routing);
    }

    // ── Agent profiles (req 1.3–1.5) ────────────────────────────────────────────────

    [Fact]
    public async Task ProfileSave_WithoutToolSettings_IsRefusedAsAFormOfThePreviousRelease()
    {
        await SeedAsync();
        var body = AgentRoutingTestData.Profile(NewProfileId(), AgentRoutingSettingsFactory.KiroTemplate, "Stale form") with { Settings = null };

        var response = await Admin.PutAsJsonAsync(ProfilesUrl, body, Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReasonAsync(response)).Should().Be("This page is out of date; reload");
    }

    public static TheoryData<string, string, string> InvalidProfiles => new()
    {
        { " ", AgentRoutingSettingsFactory.KiroTemplate, "A display name is required." },
        { "KIRO agent", AgentRoutingSettingsFactory.KiroTemplate, "Another agent profile is named 'Kiro agent'." },
        { "Fresh name", " ", "A job template is required." },
        { "Fresh name", "java,kiro", "Job template [java,kiro] does not exist in the Helm values (jobTemplates)." },
        { "Fresh name", AgentRoutingSettingsFactory.TemplateWithoutTool, "Job template notool of agent profile 'Fresh name' has no providerType" },
    };

    [Theory]
    [MemberData(nameof(InvalidProfiles))]
    public async Task ProfileSave_IsRefused_WithTheReason(string name, string jobTemplate, string reason)
    {
        await SeedAsync();
        var id = NewProfileId();

        var response = await Admin.PutAsJsonAsync(ProfilesUrl, AgentRoutingTestData.Profile(id, jobTemplate, name), Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReasonAsync(response)).Should().Be(reason);
        (await Profiles.LoadAgentProfilesAsync(Ct)).Should().NotContain(p => p.Id == id);
    }

    [Fact]
    public async Task ProfileSave_KeepsTheJobTemplateAndToolSettings_AndTheListNamesTheJobTemplates()
    {
        await SeedAsync();
        var id = NewProfileId();
        var profile = AgentRoutingTestData.Profile(id, AgentRoutingSettingsFactory.ClaudeTemplate, $"Claude {id[..8]}",
            settings: new Dictionary<string, string> { [ProviderSettingKeys.AuthMode] = ClaudeCodeAuthModes.Subscription });

        (await Admin.PutAsJsonAsync(ProfilesUrl, profile, Json)).StatusCode.Should().Be(HttpStatusCode.OK);

        var page = await Admin.GetFromJsonAsync<UiAgentProfilesPage>(ProfilesUrl, Json);
        var stored = page!.Profiles.Single(p => p.Id == id);
        stored.JobTemplate.Should().Be(AgentRoutingSettingsFactory.ClaudeTemplate);
        stored.Settings.Should().Equal(profile.Settings);
        page.JobTemplates.Should().Contain(new UiJobTemplate(AgentRoutingSettingsFactory.ClaudeTemplate, JobTemplateProviderType.Claude));
    }

    [Fact]
    public async Task DisablingOrDeletingAProfileThatRoutesName_IsRefused_WithTheRoutes()
    {
        await SeedAsync(
            AgentRoutingTestData.Routing(KiroProfileId, "auto", "auto",
                (WorkItemTaskType.Decomposition, AgentRoutingTestData.Route(ClaudeProfileId))),
            Rows(WorkItemTaskType.Implementation, AgentRoutingTestData.Route(ClaudeProfileId, "claude-opus-5-5")));
        var claude = (await Profiles.LoadAgentProfilesAsync(Ct)).Single(p => p.Id == ClaudeProfileId);

        var disable = await Admin.PutAsJsonAsync(ProfilesUrl, claude with { Enabled = false }, Json);
        var delete = await Admin.DeleteAsync($"{ProfilesUrl}/{ClaudeProfileId}");

        disable.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReasonAsync(disable)).Should().Be(
            "Agent profile 'Claude agent' cannot be disabled while routes name it: Global · Decomposition, " +
            "Repository routing-api · Implementation. Change those routes first.");
        delete.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReasonAsync(delete)).Should().Contain("cannot be deleted while routes name it: Global · Decomposition");
        (await Profiles.LoadAgentProfilesAsync(Ct)).Single(p => p.Id == ClaudeProfileId).Enabled.Should().BeTrue();
    }

    [Fact]
    public async Task MovingAProfileToAToolThatRefusesTheEffortOfItsRoutes_IsRefused_WithTheRows()
    {
        // Req 2.5: Kiro has no xhigh. A Claude profile moved onto a Kiro job template would leave rows that every later
        // save of the routing table refuses.
        await SeedAsync(
            AgentRoutingTestData.Routing(KiroProfileId, "auto", "auto",
                (WorkItemTaskType.Decomposition, AgentRoutingTestData.Route(ClaudeProfileId, "claude-opus-5-5", "xhigh"))),
            Rows(WorkItemTaskType.Review, AgentRoutingTestData.Route(ClaudeProfileId, "claude-opus-5-5", "high")));
        var claude = (await Profiles.LoadAgentProfilesAsync(Ct)).Single(p => p.Id == ClaudeProfileId);

        var moved = await Admin.PutAsJsonAsync(ProfilesUrl, claude with { JobTemplate = AgentRoutingSettingsFactory.KiroTemplate }, Json);

        moved.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReasonAsync(moved)).Should()
            .StartWith($"Agent profile 'Claude agent' cannot move to job template {AgentRoutingSettingsFactory.KiroTemplate}")
            .And.Contain("Global · Decomposition: Kiro does not accept effort 'xhigh'")
            .And.NotContain("Repository routing-api", "Kiro accepts high");
        (await Profiles.LoadAgentProfilesAsync(Ct)).Single(p => p.Id == ClaudeProfileId).JobTemplate
            .Should().Be(AgentRoutingSettingsFactory.ClaudeTemplate);
    }

    [Fact]
    public async Task AProfileNoRouteNames_CanBeDisabledAndDeleted()
    {
        await SeedAsync();
        var id = NewProfileId();
        var profile = AgentRoutingTestData.Profile(id, AgentRoutingSettingsFactory.KiroTemplate, $"Spare {id[..8]}");
        await Profiles.SaveAgentProfileAsync(profile, Ct);

        (await Admin.PutAsJsonAsync(ProfilesUrl, profile with { Enabled = false }, Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Admin.DeleteAsync($"{ProfilesUrl}/{id}")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Profiles.LoadAgentProfilesAsync(Ct)).Should().NotContain(p => p.Id == id);
    }

    // ── Repository rows (req 3.1, 3.5, 3.6, 10.4) ───────────────────────────────────

    [Fact]
    public async Task TemplateSave_StoresValidRows_AndAnEmptyTableClearsThem()
    {
        await SeedAsync();
        var template = await StoredTemplateAsync();
        var rows = Rows(WorkItemTaskType.Review, AgentRoutingTestData.Route(ClaudeProfileId, "claude-opus-5-5", "high"));

        (await Admin.PutAsJsonAsync(TemplatesUrl, template with { AgentRoutes = rows }, Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredTemplateAsync()).AgentRoutes.Should().BeEquivalentTo(rows);

        (await Admin.PutAsJsonAsync(TemplatesUrl, template with { AgentRoutes = new Dictionary<WorkItemTaskType, AgentRoute>() }, Json))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredTemplateAsync()).AgentRoutes.Should().BeEmpty();
    }

    [Fact]
    public async Task TemplateSave_RefusesAnInvalidRow_AndKeepsTheStoredRows()
    {
        var rows = Rows(WorkItemTaskType.Implementation, AgentRoutingTestData.Route(ClaudeProfileId));
        await SeedAsync(templateRows: rows);
        var template = await StoredTemplateAsync();
        var unknown = NewProfileId();

        var response = await Admin.PutAsJsonAsync(
            TemplatesUrl, template with { AgentRoutes = Rows(WorkItemTaskType.Decomposition, AgentRoutingTestData.Route(unknown)) }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReasonAsync(response)).Should().Be($"Decomposition: agent profile '{unknown}' does not exist");
        (await StoredTemplateAsync()).AgentRoutes.Should().BeEquivalentTo(rows);
    }

    [Fact]
    public async Task TemplateSave_WithRows_NeedsTheGlobalAdmin()
    {
        await SeedAsync();
        var template = await StoredTemplateAsync();
        var rows = Rows(WorkItemTaskType.Review, AgentRoutingTestData.Route(ClaudeProfileId));

        (await Client(TestUserTokens.Global(AccessRole.Operator)).PutAsJsonAsync(TemplatesUrl, template with { AgentRoutes = rows }, Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // #3577: a project admin saves the Behaviour card; the rows of the body are not taken.
        (await Client(TestUserTokens.Scoped(ProjectId, AccessRole.Admin)).PutAsJsonAsync(TemplatesUrl, template with { AgentRoutes = rows }, Json))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredTemplateAsync()).AgentRoutes.Should().BeNull();
    }

    [IdorCase("PUT /api/ui/settings/projects/{projectId}/templates")]
    [Fact]
    public async Task TemplateSave_OfAnotherProjectsTemplate_IsNotFound_AndChangesNothing()
    {
        await SeedAsync();
        var other = await StoredTemplateAsync(OtherProjectId, OtherTemplateId);
        var rows = Rows(WorkItemTaskType.Implementation, AgentRoutingTestData.Route(ClaudeProfileId));

        var response = await Admin.PutAsJsonAsync(TemplatesUrl, other with { AgentRoutes = rows }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await StoredTemplateAsync(OtherProjectId, OtherTemplateId)).AgentRoutes.Should().BeNull();
        (await Projects.LoadTemplatesForProjectAsync(ProjectId, Ct)).Should().NotContain(t => t.Id == OtherTemplateId);
    }

    [Fact]
    public async Task TemplateMove_KeepsTheRows()
    {
        var rows = Rows(WorkItemTaskType.Triage, AgentRoutingTestData.Route(ClaudeProfileId, "claude-opus-5-5"));
        await SeedAsync(templateRows: rows);

        (await Admin.PostAsJsonAsync("/api/ui/settings/templates/move", new UiMoveTemplateRequest(ProjectId, OtherProjectId, TemplateId), Json))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await StoredTemplateAsync(OtherProjectId, TemplateId)).AgentRoutes.Should().BeEquivalentTo(rows);
    }

    [Fact]
    public async Task ProviderSave_OfKindAgent_IsRefused()
    {
        var config = new ProviderConfig
        {
            Id = Guid.NewGuid().ToString(), Kind = ProviderKind.Agent, ProviderType = ProviderTypes.KiroCli, DisplayName = "Old agent config",
        };

        var response = await Admin.PutAsJsonAsync("/api/ui/settings/provider-configs", config, Json);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReasonAsync(response)).Should().Be(ConfigEndpoints.AgentProviderConfigsRetired);
    }

    // ── Resolved routes on the Pipelines page (req 3.3, 3.4, 10.2, D17) ─────────────

    [Theory]
    [InlineData(AccessRole.Admin, true)]
    [InlineData(AccessRole.Operator, false)]
    [InlineData(AccessRole.ReadOnly, false)]
    public async Task PipelinesPage_GivesProfilesToTheGlobalAdminOnly_AndResolvedRoutesToEveryReader(AccessRole role, bool seesProfiles)
    {
        await SeedAsync(
            AgentRoutingTestData.Routing(KiroProfileId, "auto", "high"),
            Rows(WorkItemTaskType.Decomposition, AgentRoutingTestData.Route(ClaudeProfileId, "claude-opus-5-5", "max")));

        var page = await Client(TestUserTokens.Global(role)).GetFromJsonAsync<UiPipelinesPage>("/api/ui/pipelines", Json);

        page!.AgentProfiles.Should().HaveCount(seesProfiles ? (await Profiles.LoadAgentProfilesAsync(Ct)).Count : 0);
        UiResolvedRoute Kiro(WorkItemTaskType taskType) => new(
            taskType, "Kiro agent", JobTemplateProviderType.Kiro, "auto", "high", AgentRouteSource.GlobalDefault,
            AgentRoutingSettingsFactory.KiroTemplate, null);
        page.Routes[TemplateId].Should().Equal(
            Kiro(WorkItemTaskType.Implementation),
            Kiro(WorkItemTaskType.Review),
            new UiResolvedRoute(
                WorkItemTaskType.Decomposition, "Claude agent", JobTemplateProviderType.Claude, "claude-opus-5-5", "max",
                AgentRouteSource.Repository, AgentRoutingSettingsFactory.ClaudeTemplate, null),
            Kiro(WorkItemTaskType.Triage),
            Kiro(WorkItemTaskType.Consolidation));
    }

    [Fact]
    public async Task PipelinesPage_ForAProjectReader_HasOnlyTheirTemplatesRoutes_AndNoProfiles()
    {
        await SeedAsync();

        var page = await Client(TestUserTokens.Scoped(ProjectId, AccessRole.ReadOnly)).GetFromJsonAsync<UiPipelinesPage>("/api/ui/pipelines", Json);

        page!.Routes.Keys.Should().Equal(TemplateId);
        page.AgentProfiles.Should().BeEmpty();
    }

    [Fact]
    public async Task PipelinesPage_WithoutADefaultRoute_NamesTheRefusal()
    {
        await SeedAsync(new RoutingTable());

        var page = await Admin.GetFromJsonAsync<UiPipelinesPage>("/api/ui/pipelines", Json);

        page!.Routes[TemplateId].Should().HaveCount(5)
            .And.OnlyContain(r => r.ProfileName == null && r.Problem == AgentRouteRefusalReason.NoRouteText);
    }

    // ── Hostile bodies (req 10.5) ───────────────────────────────────────────────────

    private static string Route(string profileId, string model = "auto", string effort = "auto") =>
        JsonSerializer.Serialize(new { profileId, model, effort });

    /// <summary>A routing table body with a raw rows object, so keys the serializer would never write can be sent.</summary>
    private static string Table(string defaultRoute, string rows = "{}") => $"{{\"default\":{defaultRoute},\"byTaskType\":{rows}}}";

    private static string TemplateBody(string agentRoutes) =>
        $"{{\"id\":\"{TemplateId}\",\"name\":\"routing-api\",\"issueProviderId\":\"issues-531\",\"repoProviderId\":\"repo-531\",\"agentRoutes\":{agentRoutes}}}";

    public static TheoryData<string, string> HostileBodies
    {
        get
        {
            var kiro = Route(KiroProfileId);
            return new()
            {
                { RoutingUrl, "null" },
                { RoutingUrl, "\"routing\"" },
                { RoutingUrl, "{\"default\":" },
                { RoutingUrl, Table(kiro, $"{{\"NotATaskType\":{kiro}}}") },
                { RoutingUrl, Table(kiro, $"{{\"99\":{kiro}}}") },
                { RoutingUrl, Table(kiro, $"{{\"Implementation\":{kiro},\"Implementation\":{kiro}}}") },
                { RoutingUrl, Table(Route(Guid.Empty.ToString())) },
                { RoutingUrl, Table(Route(DisabledProfileId)) },
                { RoutingUrl, Table(Route(KiroProfileId, new string('m', 201))) },
                { RoutingUrl, Table(Route(KiroProfileId, "sonnet; rm -rf /")) },
                { RoutingUrl, Table(Route(OpenCodeProfileId, effort: "high")) },
                { RoutingUrl, Table("null", $"{{\"Review\":{kiro}}}") },
                { RoutingUrl, Table(kiro, "{\"Review\":null}") },
                { TemplatesUrl, TemplateBody($"{{\"NotATaskType\":{kiro}}}") },
                { TemplatesUrl, TemplateBody($"{{\"Review\":{Route(OpenCodeProfileId, effort: "max")}}}") },
                { ProfilesUrl, "{\"displayName\":\"Hostile\",\"jobTemplate\":\"dotnet,kiro\",\"settings\":\"not-an-object\"}" },
                { ProfilesUrl, "{\"displayName\":null,\"jobTemplate\":\"dotnet,kiro\",\"settings\":{}}" },
            };
        }
    }

    [Theory]
    [MemberData(nameof(HostileBodies))]
    public async Task HostileBodies_AreRefusedWith4xx_AndChangeNothing(string url, string body)
    {
        await SeedAsync();
        var routingBefore = AgentRoutingStore.Serialize((await Routing.LoadAsync(Ct))!);
        var profilesBefore = JsonSerializer.Serialize((await Profiles.LoadAgentProfilesAsync(Ct)).OrderBy(p => p.Id), Json);

        using var response = await Admin.PutAsync(url, new StringContent(body, Encoding.UTF8, "application/json"));

        ((int)response.StatusCode).Should().BeInRange(400, 499, body);
        AgentRoutingStore.Serialize((await Routing.LoadAsync(Ct))!).Should().Be(routingBefore);
        (await StoredTemplateAsync()).AgentRoutes.Should().BeNull();
        JsonSerializer.Serialize((await Profiles.LoadAgentProfilesAsync(Ct)).OrderBy(p => p.Id), Json)
            .Should().Be(profilesBefore, "a refused profile body stores no profile");
    }
}
