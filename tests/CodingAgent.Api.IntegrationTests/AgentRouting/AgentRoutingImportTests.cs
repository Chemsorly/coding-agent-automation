using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using RoutingTable = CodingAgent.Pipeline.Models.AgentRouting;

namespace CodingAgent.Api.IntegrationTests.AgentRouting;

/// <summary>Its own database: an import that fails half-way on InMemory (which has no transactions) leaves rows behind.</summary>
public sealed class AgentRoutingImportFactory : ApiWebApplicationFactory;

/// <summary>
/// Spec 053 task 4.1, req 7.12: the config bundle carries the routing table; an import clears and restores it, runs the
/// move in its transaction for an older bundle, and refuses a bundle whose routing has the wrong shape before it
/// clears anything. A template save without routes keeps the stored ones (review-3 m4).
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class AgentRoutingImportTests : IClassFixture<AgentRoutingImportFactory>
{
    private const string ProfileId = "0e5e0000-0000-4000-8000-0000000001a1";

    private static readonly JsonSerializerOptions BundleOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AgentRoutingImportFactory _factory;
    private readonly HttpClient _operator;
    private readonly HttpClient _admin;

    public AgentRoutingImportTests(AgentRoutingImportFactory factory)
    {
        _factory = factory;
        _operator = factory.CreateClient();
        _operator.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);
        _admin = factory.CreateClient();
        _admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", Security.TestUserTokens.For(Security.TestUserTokens.Global(CodingAgent.Contracts.Auth.AccessRole.Admin)));
    }

    private AgentRoutingStore Routing => _factory.Services.GetRequiredService<AgentRoutingStore>();

    private static ConfigBundle Bundle(string? agentRouting, params AgentProfile[] profiles) => new()
    {
        AgentProfiles = profiles.Select(p => new NamedConfigDto
        {
            Id = Guid.Parse(p.Id), Name = p.DisplayName, Configuration = JsonSerializer.Serialize(p, PipelineJsonOptions.Default),
        }).ToList(),
        AgentRouting = agentRouting,
    };

    private Task<HttpResponseMessage> ImportAsync(ConfigBundle bundle) => ImportAsync(JsonSerializer.Serialize(bundle, BundleOptions));

    private async Task<HttpResponseMessage> ImportAsync(string bundleJson)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(bundleJson));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "file", "bundle.json");
        return await _operator.PostAsync("/api/config/import", content);
    }

    [Fact]
    public async Task ExportThenImport_RestoresTheRouting_AndKeepsTheOtherKeyValueRows()
    {
        var routing = AgentRoutingTestData.Routing(ProfileId, "opus", "high",
            (WorkItemTaskType.Review, AgentRoutingTestData.Route(ProfileId, "sonnet")));
        (await ImportAsync(Bundle(AgentRoutingStore.Serialize(routing), AgentRoutingTestData.Profile(ProfileId))))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        using (var db = _factory.CreateDbContext())
        {
            db.KeyValueStore.Add(new KeyValueEntity { Key = $"import-test-{Guid.NewGuid():N}", Value = "kept" });
            db.SaveChanges();
        }

        var exported = await _operator.GetStringAsync("/api/config/export");
        JsonDocument.Parse(exported).RootElement.GetProperty("agentRouting").GetString().Should().NotBeNullOrEmpty();
        await Routing.SaveAsync(RoutingTable.Empty, CancellationToken.None);
        (await ImportAsync(exported)).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Routing.LoadAsync(CancellationToken.None)).Should().BeEquivalentTo(routing);
        using var check = _factory.CreateDbContext();
        (await check.KeyValueStore.CountAsync(kv => kv.Value == "kept")).Should().Be(1,
            "of the key-value rows only the routing table is configuration (V5)");
    }

    [Theory]
    [InlineData("""{"default":{"profileId":"0e5e0000-0000-4000-8000-0000000001a1","model":"bad model!","effort":"auto"},"byTaskType":{}}""", "model")]
    [InlineData("""{"default":{"profileId":"0e5e0000-0000-4000-8000-0000000009ff","model":"auto","effort":"auto"},"byTaskType":{}}""", "is not in the bundle")]
    [InlineData("""{"default":null,"byTaskType":{"99":{"profileId":"0e5e0000-0000-4000-8000-0000000001a1","model":"auto","effort":"auto"}}}""", "Task type 99 is unknown")]
    [InlineData("{not json", "cannot be read")]
    public async Task ABundleWithAMalformedRouting_Is400_AndChangesNothing(string agentRouting, string reason)
    {
        var before = AgentRoutingTestData.Routing(ProfileId);
        (await ImportAsync(Bundle(AgentRoutingStore.Serialize(before), AgentRoutingTestData.Profile(ProfileId))))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var response = await ImportAsync(Bundle(agentRouting, AgentRoutingTestData.Profile(ProfileId, displayName: "changed")));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Invalid agent routing").And.Contain(reason);
        (await Routing.LoadAsync(CancellationToken.None)).Should().BeEquivalentTo(before);
        using var db = _factory.CreateDbContext();
        (await db.AgentProfiles.SingleAsync()).Name.Should().Be(ProfileId, "the bundle was refused before anything was cleared");
    }

    [Fact]
    public async Task AnOldBundleWhoseMoveFails_Answers500()
    {
        // A legacy profile the move cannot read throws inside the import's transaction. The rollback itself is pinned
        // on Postgres (AgentRoutingMoveConcurrencyTests); InMemory has no transactions.
        var bundle = new ConfigBundle
        {
            AgentProfiles = [new NamedConfigDto { Id = Guid.NewGuid(), Name = "broken", Configuration = "{\"agentProviderConfigId\": " }],
        };

        var response = await ImportAsync(bundle);

        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        using var db = _factory.CreateDbContext();
        db.AgentProfiles.RemoveRange(db.AgentProfiles);
        db.SaveChanges();
    }

    [Fact]
    public async Task ATemplateSaveWithoutRoutes_KeepsTheStoredRows_AndAnEmptySetClearsThem()
    {
        var projectId = Guid.NewGuid().ToString();
        (await _admin.PutAsJsonAsync("/api/ui/settings/projects", new PipelineProject { Id = projectId, Name = $"Rows {projectId[..8]}" },
            PipelineJsonOptions.Default)).StatusCode.Should().Be(HttpStatusCode.OK);
        var rows = new Dictionary<WorkItemTaskType, AgentRoute> { [WorkItemTaskType.Review] = AgentRoutingTestData.Route(ProfileId, "sonnet") };
        var template = new PipelineJobTemplate
        {
            Id = Guid.NewGuid().ToString(), Name = "rows", IssueProviderId = $"ip-{projectId}", RepoProviderId = $"rp-{projectId}",
            AgentRoutes = rows,
        };
        var path = $"/api/ui/settings/projects/{projectId}/templates";
        (await _admin.PutAsJsonAsync(path, template, PipelineJsonOptions.Default)).StatusCode.Should().Be(HttpStatusCode.OK);

        // A page that does not edit routes (Behaviour, Pipelines) sends none: the rows stay.
        (await _admin.PutAsJsonAsync(path, template with { AgentRoutes = null, Name = "renamed" }, PipelineJsonOptions.Default))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredAsync(projectId, template.Id)).Should().BeEquivalentTo(new { Name = "renamed", AgentRoutes = rows });

        (await _admin.PutAsJsonAsync(path, template with { AgentRoutes = new Dictionary<WorkItemTaskType, AgentRoute>() }, PipelineJsonOptions.Default))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredAsync(projectId, template.Id)).AgentRoutes.Should().BeNullOrEmpty();
    }

    private async Task<PipelineJobTemplate> StoredAsync(string projectId, string templateId) =>
        (await _factory.Services.GetRequiredService<CodingAgent.Pipeline.Interfaces.IProjectStore>()
            .LoadTemplatesForProjectAsync(projectId, CancellationToken.None)).Single(t => t.Id == templateId);

    /// <summary>The routing table as the store writes it.</summary>
    private static class RoutingStore
    {
        public static string Serialize(RoutingTable routing) => AgentRoutingStore.Serialize(routing);
    }
}
