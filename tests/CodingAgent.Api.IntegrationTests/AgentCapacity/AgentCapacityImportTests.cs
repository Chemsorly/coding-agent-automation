using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.AgentCapacity;

/// <summary>Its own database: an import that fails half-way on InMemory (no transactions) leaves rows behind.</summary>
public sealed class AgentCapacityImportFactory : ApiWebApplicationFactory;

/// <summary>
/// Spec 057 req 6.5: the config bundle carries the UI agent limits; an import clears and restores them and refuses a
/// bundle whose limits are invalid before it clears anything.
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class AgentCapacityImportTests : IClassFixture<AgentCapacityImportFactory>
{
    private static readonly JsonSerializerOptions BundleOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly AgentCapacityImportFactory _factory;
    private readonly HttpClient _operator;

    public AgentCapacityImportTests(AgentCapacityImportFactory factory)
    {
        _factory = factory;
        _operator = factory.CreateClient();
        _operator.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);
    }

    private AgentCapacityStore Store => _factory.Services.GetRequiredService<AgentCapacityStore>();

    private static AgentCapacityLimits Limits(int global, params (string Key, int Value)[] entries) => new()
    {
        MaxConcurrentAgents = global,
        MaxConcurrentByJobTemplate = entries.ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal),
    };

    private async Task<HttpResponseMessage> ImportAsync(string bundleJson)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(Encoding.UTF8.GetBytes(bundleJson));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(file, "file", "bundle.json");
        return await _operator.PostAsync("/api/config/import", content);
    }

    private Task<HttpResponseMessage> ImportAsync(ConfigBundle bundle) => ImportAsync(JsonSerializer.Serialize(bundle, BundleOptions));

    // req 6.5
    [Fact]
    public async Task ExportThenImport_RestoresTheLimits_IncludingKeysThisClusterDoesNotDeploy()
    {
        var limits = Limits(5, ("dotnet,kiro", 3), ("not,deployed,here", 2));
        await Store.SaveAsync(limits, CancellationToken.None);

        var exported = await _operator.GetStringAsync("/api/config/export");
        JsonDocument.Parse(exported).RootElement.GetProperty("agentCapacity").GetString().Should().NotBeNullOrEmpty();
        await Store.SaveAsync(AgentCapacityLimits.Empty, CancellationToken.None);

        (await ImportAsync(exported)).StatusCode.Should().Be(HttpStatusCode.OK);

        var loaded = await Store.LoadAsync(CancellationToken.None);
        loaded.MaxConcurrentAgents.Should().Be(5);
        loaded.MaxConcurrentByJobTemplate.Should().BeEquivalentTo(limits.MaxConcurrentByJobTemplate);
    }

    // req 6.5: an older bundle has no agentCapacity; the import leaves no limits.
    [Fact]
    public async Task ABundleWithoutLimits_ClearsTheStoredLimits()
    {
        await Store.SaveAsync(Limits(5), CancellationToken.None);

        (await ImportAsync(new ConfigBundle())).StatusCode.Should().Be(HttpStatusCode.OK);

        (await Store.LoadAsync(CancellationToken.None)).Should().BeSameAs(AgentCapacityLimits.Empty);
    }

    // req 6.3, 6.5
    [Theory]
    [InlineData("""{"maxConcurrentAgents":101}""", "The limit for all agents must be between 0 and 100 (was 101).")]
    [InlineData("""{"maxConcurrentByJobTemplate":{"dotnet;kiro":2}}""", "dotnet;kiro is not a valid job template key.")]
    [InlineData("{not json", "cannot be read")]
    public async Task ABundleWithInvalidLimits_Is400_AndChangesNothing(string agentCapacity, string reason)
    {
        var before = Limits(4, ("dotnet,kiro", 2));
        await Store.SaveAsync(before, CancellationToken.None);

        var response = await ImportAsync(new ConfigBundle { AgentCapacity = agentCapacity });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("Invalid agent capacity").And.Contain(reason);
        var after = await Store.LoadAsync(CancellationToken.None);
        after.MaxConcurrentAgents.Should().Be(4, "the bundle was refused before anything was cleared");
        after.MaxConcurrentByJobTemplate.Should().BeEquivalentTo(before.MaxConcurrentByJobTemplate);
    }
}
