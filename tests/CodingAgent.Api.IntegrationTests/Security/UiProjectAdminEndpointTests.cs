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
/// #3577: a project admin (admin bound to project P, no global role) loads and saves P's project page and the
/// Behaviour of P's repositories, through the requests the web client sends. The operator's fields keep their stored
/// values whatever the request carries, and nothing of project Q can be read or changed.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class UiProjectAdminEndpointTests(ApiWebApplicationFactory factory)
{
    private const string ProjectP = "6f1c2a9e-0000-0000-0000-0000000000e1";
    private const string ProjectQ = "6f1c2a9e-0000-0000-0000-0000000000e2";
    private const string TemplateP = "6f1c2a9e-0000-0000-0000-0000000000e3";
    private const string TemplateQ = "6f1c2a9e-0000-0000-0000-0000000000e4";
    private const string SecretCanary = "canary-project-admin-secret-61dd";

    private static readonly System.Text.Json.JsonSerializerOptions Json = PipelineJsonOptions.Default;

    private HttpClient Client(AccessGrant grant)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestUserTokens.For(grant));
        return client;
    }

    private HttpClient AdminOfP => Client(TestUserTokens.Scoped(ProjectP, AccessRole.Admin));

    private IProjectStore Store => factory.Services.GetRequiredService<IProjectStore>();

    private static PipelineJobTemplate Template(string id, string name) => new()
    {
        Id = id,
        Name = name,
        IssueProviderId = "issue-" + id[^2..],
        RepoProviderId = "repo-" + id[^2..],
        BrainProviderId = "brain-1",
        BrainReadOnly = false,
        PipelineProviderId = "ci-1",
        RequiredLabels = ["gpu"],
        Enabled = true,
        TriageEnabled = false,
    };

    private async Task SeedAsync()
    {
        foreach (var (projectId, templateId, name) in new[] { (ProjectP, TemplateP, "Papa"), (ProjectQ, TemplateQ, "Quebec") })
        {
            await Store.SaveProjectAsync(new PipelineProject
            {
                Id = projectId,
                Name = name,
                Enabled = true,
                EpicIssueProviderId = "epics-1",
                BrainReadOnly = false,
                BrainConsolidationReviewEnabled = false,
                Secrets = new Dictionary<string, string> { ["API_TOKEN"] = SecretCanary },
            }, CancellationToken.None);
            await Store.SaveTemplateAsync(projectId, Template(templateId, name + " repo"), CancellationToken.None);
        }
    }

    private async Task<PipelineProject> StoredProjectAsync(string projectId) =>
        (await Store.GetProjectByIdAsync(projectId, CancellationToken.None))!;

    private async Task<PipelineJobTemplate> StoredTemplateAsync(string projectId, string templateId) =>
        (await Store.LoadTemplatesForProjectAsync(projectId, CancellationToken.None)).Single(t => t.Id == templateId);

    [Fact]
    public async Task ProjectAdmin_LoadsTheProjectPage_WithSecretsMasked()
    {
        await SeedAsync();

        var project = await AdminOfP.GetAsync($"/api/ui/settings/projects/{ProjectP}");
        var defaults = await AdminOfP.GetAsync("/api/ui/settings/pipeline");
        var pipelines = await AdminOfP.GetAsync("/api/ui/pipelines");

        project.StatusCode.Should().Be(HttpStatusCode.OK);
        defaults.StatusCode.Should().Be(HttpStatusCode.OK, "the Settings tab shows the global default of each override");
        pipelines.StatusCode.Should().Be(HttpStatusCode.OK, "the token carries the project admin role, which covers read");
        var body = await project.Content.ReadAsStringAsync();
        body.Should().NotContain(SecretCanary);
        System.Text.Json.JsonSerializer.Deserialize<PipelineProject>(body, Json)!.Name.Should().Be("Papa");
    }

    [Fact]
    public async Task ProjectAdmin_SavesTheProject_AndTheOperatorsFieldsKeepTheirStoredValues()
    {
        await SeedAsync();
        var loaded = await AdminOfP.GetFromJsonAsync<PipelineProject>($"/api/ui/settings/projects/{ProjectP}", Json);

        var response = await AdminOfP.PutAsJsonAsync("/api/ui/settings/projects", loaded! with
        {
            Name = "Papa renamed",
            Description = "Team P",
            SteeringContent = "Project steering.",
            MaxRetries = 2,
            Secrets = new Dictionary<string, string> { ["API_TOKEN"] = "****", ["NEW_KEY"] = "new-value" },
            Enabled = false,
            EpicIssueProviderId = "epics-other",
            BrainReadOnly = true,
            BrainConsolidationReviewEnabled = true,
        }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await StoredProjectAsync(ProjectP);
        stored.Name.Should().Be("Papa renamed");
        stored.Description.Should().Be("Team P");
        stored.SteeringContent.Should().Be("Project steering.");
        stored.MaxRetries.Should().Be(2);
        stored.Secrets.Should().Equal(new Dictionary<string, string> { ["API_TOKEN"] = SecretCanary, ["NEW_KEY"] = "new-value" });
        stored.Enabled.Should().BeTrue();
        stored.EpicIssueProviderId.Should().Be("epics-1");
        stored.BrainReadOnly.Should().BeFalse();
        stored.BrainConsolidationReviewEnabled.Should().BeFalse();
    }

    [Fact]
    public async Task ProjectAdmin_SavesTheBehaviour_AndTheBindingAndConnectionsKeepTheirStoredValues()
    {
        await SeedAsync();
        // The repository page loads its template from the Pipelines page.
        var page = await AdminOfP.GetFromJsonAsync<UiPipelinesPage>("/api/ui/pipelines", Json);
        var template = page!.Templates.Single(t => t.Id == TemplateP);

        var response = await AdminOfP.PutAsJsonAsync($"/api/ui/settings/projects/{ProjectP}/templates", template with
        {
            Name = "Papa repo renamed",
            ImplementationEnabled = false,
            HousekeepingEnabled = true,
            HousekeepingConcurrencyLimit = 2,
            SteeringContent = "Be brief.",
            SetupSteps = [new SetupStep { Name = "restore", Command = "dotnet restore" }],
            Secrets = new Dictionary<string, string> { ["NUGET_TOKEN"] = "nuget-value" },
            BlacklistedPaths = ["secrets/"],
            IssueProviderId = "issue-other",
            RepoProviderId = "repo-other",
            BrainProviderId = "brain-other",
            BrainReadOnly = true,
            PipelineProviderId = "ci-other",
            RequiredLabels = ["cpu"],
            Enabled = false,
            TriageEnabled = true,
            AgentRoutes = new Dictionary<WorkItemTaskType, AgentRoute>
            {
                [WorkItemTaskType.Implementation] = new("profile-of-the-team", "auto", "high"),
            },
        }, Json);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await StoredTemplateAsync(ProjectP, TemplateP);
        stored.Name.Should().Be("Papa repo renamed");
        stored.ImplementationEnabled.Should().BeFalse();
        stored.HousekeepingEnabled.Should().BeTrue();
        stored.HousekeepingConcurrencyLimit.Should().Be(2);
        stored.SteeringContent.Should().Be("Be brief.");
        stored.SetupSteps!.Single().Command.Should().Be("dotnet restore");
        stored.Secrets.Should().Equal(new Dictionary<string, string> { ["NUGET_TOKEN"] = "nuget-value" });
        stored.BlacklistedPaths.Should().Equal("secrets/");
        stored.Should().BeEquivalentTo(Template(TemplateP, "Papa repo"), o => o
            .Including(t => t.IssueProviderId).Including(t => t.RepoProviderId)
            .Including(t => t.BrainProviderId).Including(t => t.BrainReadOnly).Including(t => t.PipelineProviderId)
            .Including(t => t.RequiredLabels).Including(t => t.Enabled).Including(t => t.TriageEnabled),
            "connections, the Binding card and triage are the operator's");
        stored.AgentRoutes.Should().BeNull("repository routes are the operator's (spec 053)");
    }

    [Fact]
    public async Task ProjectAdmin_CannotCreateAProjectOrARepository()
    {
        await SeedAsync();
        var newProject = Guid.NewGuid().ToString();
        var newTemplate = Template(Guid.NewGuid().ToString(), "Papa second repo");

        (await AdminOfP.PutAsJsonAsync("/api/ui/settings/projects", new PipelineProject { Id = newProject, Name = "New" }, Json))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await AdminOfP.PutAsJsonAsync($"/api/ui/settings/projects/{ProjectP}/templates", newTemplate, Json))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        (await Store.GetProjectByIdAsync(newProject, CancellationToken.None)).Should().BeNull();
        (await Store.LoadTemplatesForProjectAsync(ProjectP, CancellationToken.None)).Select(t => t.Id).Should().Equal(TemplateP);
    }

    [Fact]
    public async Task ProjectAdmin_KeepsNoOtherSettingsWrite()
    {
        await SeedAsync();
        var config = await AdminOfP.GetFromJsonAsync<PipelineConfiguration>("/api/ui/settings/pipeline", Json);

        (await AdminOfP.PutAsJsonAsync("/api/ui/settings/pipeline", config, Json)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AdminOfP.GetAsync("/api/ui/settings/projects")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AdminOfP.GetAsync("/api/ui/settings/templates")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AdminOfP.DeleteAsync($"/api/ui/settings/projects/{ProjectP}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AdminOfP.DeleteAsync($"/api/ui/settings/projects/{ProjectP}/templates/{TemplateP}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AdminOfP.PostAsJsonAsync("/api/ui/settings/templates/move",
                new UiMoveTemplateRequest(ProjectQ, ProjectP, TemplateQ), Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(AccessRole.ReadOnly)]
    [InlineData(AccessRole.Operator)]
    public async Task AProjectReaderOrOperator_CannotLoadOrSaveTheProject(AccessRole role)
    {
        await SeedAsync();
        // Admin elsewhere, so the role policy passes and the project check in the handler decides.
        var client = Client(new AccessGrant(AccessRole.None,
            new Dictionary<string, AccessRole> { [ProjectP] = role, [ProjectQ] = AccessRole.Admin }, []));
        var template = await StoredTemplateAsync(ProjectP, TemplateP);

        (await client.GetAsync($"/api/ui/settings/projects/{ProjectP}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PutAsJsonAsync("/api/ui/settings/projects", new PipelineProject { Id = ProjectP, Name = "Hijacked" }, Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PutAsJsonAsync($"/api/ui/settings/projects/{ProjectP}/templates", template with { Name = "Hijacked" }, Json))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await StoredProjectAsync(ProjectP)).Name.Should().Be("Papa");
        (await StoredTemplateAsync(ProjectP, TemplateP)).Name.Should().Be("Papa repo");
    }

    [Fact]
    [IdorCase("GET /api/ui/settings/projects/{id}")]
    [IdorCase("PUT /api/ui/settings/projects/{projectId}/templates")]
    public async Task ProjectAdmin_CannotReadOrChangeAnotherProject_OrItsRepositories()
    {
        await SeedAsync();
        var templateQ = await StoredTemplateAsync(ProjectQ, TemplateQ);

        var read = await AdminOfP.GetAsync($"/api/ui/settings/projects/{ProjectQ}");
        var project = await AdminOfP.PutAsJsonAsync("/api/ui/settings/projects", new PipelineProject { Id = ProjectQ, Name = "Hijacked" }, Json);
        var template = await AdminOfP.PutAsJsonAsync($"/api/ui/settings/projects/{ProjectQ}/templates", templateQ with { Name = "Hijacked" }, Json);
        // Saving Q's repository through P's route would move it into P.
        var moved = await AdminOfP.PutAsJsonAsync($"/api/ui/settings/projects/{ProjectP}/templates", templateQ with { Name = "Hijacked" }, Json);

        read.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await read.Content.ReadAsStringAsync()).Should().NotContain("Quebec");
        project.StatusCode.Should().Be(HttpStatusCode.NotFound);
        template.StatusCode.Should().Be(HttpStatusCode.NotFound);
        moved.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await StoredProjectAsync(ProjectQ)).Name.Should().Be("Quebec");
        (await StoredTemplateAsync(ProjectQ, TemplateQ)).Name.Should().Be("Quebec repo");
        (await Store.LoadTemplatesForProjectAsync(ProjectP, CancellationToken.None)).Select(t => t.Id).Should().Equal(TemplateP);
    }

    [Fact]
    public async Task GlobalAdmin_StillSavesEveryField()
    {
        await SeedAsync();
        var admin = Client(TestUserTokens.Global(AccessRole.Admin));
        var template = await StoredTemplateAsync(ProjectP, TemplateP);
        var project = await admin.GetFromJsonAsync<PipelineProject>($"/api/ui/settings/projects/{ProjectP}", Json);

        (await admin.PutAsJsonAsync($"/api/ui/settings/projects/{ProjectP}/templates", template with { RequiredLabels = ["cpu"], Enabled = false }, Json))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await admin.PutAsJsonAsync("/api/ui/settings/projects", project! with { Enabled = false, EpicIssueProviderId = null }, Json))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var storedTemplate = await StoredTemplateAsync(ProjectP, TemplateP);
        storedTemplate.RequiredLabels.Should().Equal("cpu");
        storedTemplate.Enabled.Should().BeFalse();
        var storedProject = await StoredProjectAsync(ProjectP);
        storedProject.Enabled.Should().BeFalse();
        storedProject.EpicIssueProviderId.Should().BeNull();
    }
}
