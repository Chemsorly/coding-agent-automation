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
/// Spec 051 2.7: the Pipelines page reads <c>GET /api/ui/pipelines</c> and changes templates through the admin
/// settings surface. A project-scoped user sees only their projects' templates, the providers those use and no
/// global configuration; secrets never leave the API.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class UiPipelinesEndpointTests(ApiWebApplicationFactory factory)
{
    private const string ProjectM = "6f1c2a9e-0000-0000-0000-0000000000e9";
    private const string ProjectN = "6f1c2a9e-0000-0000-0000-0000000000ea";
    private const string TemplateM = "7a000000-0000-0000-0000-0000000000e9";
    private const string TemplateN = "7a000000-0000-0000-0000-0000000000ea";
    private const string RepoM = "8b000000-0000-0000-0000-0000000000e9";
    private const string RepoN = "8b000000-0000-0000-0000-0000000000ea";

    private HttpClient Http(AccessGrant grant)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestUserTokens.For(grant));
        return client;
    }

    private async Task SeedAsync()
    {
        var projects = factory.Services.GetRequiredService<IProjectStore>();
        await projects.SaveProjectAsync(new PipelineProject { Id = ProjectM, Name = "M", Secrets = new Dictionary<string, string> { ["DB"] = "m-secret" } }, CancellationToken.None);
        await projects.SaveProjectAsync(new PipelineProject { Id = ProjectN, Name = "N" }, CancellationToken.None);
        await projects.SaveTemplateAsync(ProjectM, new PipelineJobTemplate { Id = TemplateM, Name = "m-api", IssueProviderId = "ip-m", RepoProviderId = RepoM }, CancellationToken.None);
        await projects.SaveTemplateAsync(ProjectN, new PipelineJobTemplate { Id = TemplateN, Name = "n-api", IssueProviderId = "ip-n", RepoProviderId = RepoN }, CancellationToken.None);

        using var scope = factory.Services.CreateScope();
        var providers = scope.ServiceProvider.GetRequiredService<IProviderConfigStore>();
        foreach (var (id, name) in new[] { (RepoM, "M repo"), (RepoN, "N repo") })
        {
            await providers.SaveProviderConfigAsync(new ProviderConfig
            {
                Id = id, Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = name,
                Secrets = new Dictionary<string, string> { [ProviderSettingKeys.Token] = "ghp-secret" },
            }, CancellationToken.None);
        }
    }

    [Fact]
    public async Task GlobalReader_SeesEveryProjectsTemplatesAndProviders_WithSecretsMasked()
    {
        await SeedAsync();

        var page = await Http(TestUserTokens.Global(AccessRole.ReadOnly)).GetFromJsonAsync<UiPipelinesPage>("/api/ui/pipelines", PipelineJsonOptions.Default);

        page!.Templates.Select(t => t.Id).Should().Contain([TemplateM, TemplateN]);
        page.RepoProviders.Select(p => p.Id).Should().Contain([RepoM, RepoN]);
        page.RepoProviders.SelectMany(p => p.Secrets?.Values.ToList() ?? []).Should().NotContain("ghp-secret");
        page.Projects.Single(p => p.Id == ProjectM).Secrets.Should().BeNull("projects carry only their id, name, flag and templates");
    }

    [Fact]
    public async Task ScopedReader_SeesOnlyTheirProject_TheProvidersItUses_AndNoGlobalConfiguration()
    {
        await SeedAsync();

        var page = await Http(TestUserTokens.Scoped(ProjectM, AccessRole.ReadOnly)).GetFromJsonAsync<UiPipelinesPage>("/api/ui/pipelines", PipelineJsonOptions.Default);

        page!.Projects.Select(p => p.Id).Should().Equal(ProjectM);
        page.Templates.Select(t => t.Id).Should().Equal(TemplateM);
        page.RepoProviders.Select(p => p.Id).Should().Equal(RepoM);
        page.QualityGateConfigs.Should().BeEmpty();
        page.ReviewerConfigs.Should().BeEmpty();
        page.AgentProfiles.Should().BeEmpty();
    }

    [Fact]
    public async Task TemplateWrites_NeedAdmin()
    {
        await SeedAsync();
        var template = new PipelineJobTemplate { Id = TemplateM, Name = "m-api renamed", IssueProviderId = "ip-m", RepoProviderId = RepoM };

        (await Http(TestUserTokens.Global(AccessRole.Operator)).PutAsJsonAsync($"/api/ui/settings/projects/{ProjectM}/templates", template, PipelineJsonOptions.Default))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Http(TestUserTokens.Global(AccessRole.Operator)).DeleteAsync($"/api/ui/settings/projects/{ProjectM}/templates/{TemplateM}"))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);

        (await Http(TestUserTokens.Global(AccessRole.Admin)).PutAsJsonAsync($"/api/ui/settings/projects/{ProjectM}/templates", template, PipelineJsonOptions.Default))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await factory.Services.GetRequiredService<IProjectStore>().LoadTemplatesForProjectAsync(ProjectM, CancellationToken.None);
        saved.Single(t => t.Id == TemplateM).Name.Should().Be("m-api renamed");
    }
}
