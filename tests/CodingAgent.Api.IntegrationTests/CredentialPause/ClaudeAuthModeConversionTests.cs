using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using CodingAgent.Api.IntegrationTests.AgentRouting;
using CodingAgent.Infrastructure;
using CodingAgent.Infrastructure.Persistence.Entities;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CodingAgent.Api.IntegrationTests.CredentialPause;

/// <summary>
/// Spec 056 DD16, D8 (task 9.3): the startup step converts every Claude Code profile whose auth mode is not
/// <c>apiKey</c> or <c>subscription</c> to <c>subscription</c>; it is idempotent and leaves other profiles alone. Stored
/// profiles with the removed <c>enabled</c> flag still load.
/// </summary>
[Collection(CredentialPauseCollection.Name)]
public sealed class ClaudeAuthModeConversionTests
{
    private const string ClaudeTemplate = "claude,dotnet";
    private const string KiroTemplate = "dotnet,kiro";
    private static readonly IReadOnlyDictionary<string, string> Tools = new Dictionary<string, string>
    {
        [ClaudeTemplate] = JobTemplateProviderType.Claude,
        [KiroTemplate] = JobTemplateProviderType.Kiro,
    };

    private readonly AgentRoutingTestSupport.InMemoryFactory _db = new(Guid.NewGuid().ToString());

    private async Task<Guid> SeedAsync(string json)
    {
        var id = Guid.NewGuid();
        await using var db = _db.CreateDbContext();
        db.AgentProfiles.Add(new AgentProfileEntity { Id = id, Name = $"p-{id:N}", Configuration = json.Replace("{ID}", id.ToString()) });
        await db.SaveChangesAsync();
        return id;
    }

    private static string Profile(string jobTemplate, string? authMode, bool withEnabled = false) =>
        JsonSerializer.Serialize(new JsonObject
        {
            ["id"] = "{ID}",
            ["displayName"] = $"Profile {jobTemplate} {authMode}",
            ["jobTemplate"] = jobTemplate,
            ["enabled"] = withEnabled ? false : null,
            ["settings"] = authMode is null ? new JsonObject() : new JsonObject { [ProviderSettingKeys.AuthMode] = authMode },
            ["mcpServers"] = new JsonArray(),
        });

    private async Task<AgentProfile> ReadAsync(Guid id)
    {
        await using var db = _db.CreateDbContext();
        var json = (await db.AgentProfiles.AsNoTracking().SingleAsync(p => p.Id == id)).Configuration!;
        return JsonSerializer.Deserialize<AgentProfile>(json, PipelineJsonOptions.Lenient)!;
    }

    private async Task<int> RunAsync()
    {
        await using var db = _db.CreateDbContext();
        return await ClaudeAuthModeConversion.RunAsync(db, Tools, Serilog.Core.Logger.None, CancellationToken.None);
    }

    [Theory]
    [InlineData("auto")]
    [InlineData("")]
    [InlineData("nonsense")]
    [InlineData(null)]
    public async Task AClaudeProfileWithoutAKnownMode_BecomesSubscription(string? authMode)
    {
        var id = await SeedAsync(Profile(ClaudeTemplate, authMode));

        (await RunAsync()).Should().Be(1);

        (await ReadAsync(id)).Settings![ProviderSettingKeys.AuthMode].Should().Be(ClaudeCodeAuthModes.Subscription);
    }

    [Theory]
    [InlineData("apiKey", "apiKey", 0)]
    [InlineData("subscription", "subscription", 0)]
    [InlineData("APIKEY", "apiKey", 1)] // a known mode in another case keeps its meaning
    public async Task AKnownMode_IsKept(string authMode, string expected, int changes)
    {
        var id = await SeedAsync(Profile(ClaudeTemplate, authMode));

        (await RunAsync()).Should().Be(changes);

        (await ReadAsync(id)).Settings![ProviderSettingKeys.AuthMode].Should().Be(expected);
    }

    [Fact]
    public async Task ASecondRun_ChangesNothing()
    {
        await SeedAsync(Profile(ClaudeTemplate, "auto"));
        await RunAsync();

        (await RunAsync()).Should().Be(0);
    }

    [Fact]
    public async Task NonClaudeProfiles_AreLeftAlone()
    {
        var kiro = await SeedAsync(Profile(KiroTemplate, null));

        (await RunAsync()).Should().Be(0);

        (await ReadAsync(kiro)).Settings.Should().BeEmpty();
    }

    [Fact]
    public async Task AProfileOnAnUnknownTemplate_IsClaudeWhenItHasAnAuthMode()
    {
        var withMode = await SeedAsync(Profile("gone,template", "auto"));
        var without = await SeedAsync(Profile("other,gone", null));

        (await RunAsync()).Should().Be(1);

        (await ReadAsync(withMode)).Settings![ProviderSettingKeys.AuthMode].Should().Be(ClaudeCodeAuthModes.Subscription);
        (await ReadAsync(without)).Settings.Should().BeEmpty();
    }

    [Fact]
    public async Task AStoredProfileWithTheRemovedEnabledFlag_Loads_AndTheConversionDropsTheFlag()
    {
        var id = await SeedAsync(Profile(ClaudeTemplate, "auto", withEnabled: true));

        await RunAsync();

        var profile = await ReadAsync(id);
        profile.JobTemplate.Should().Be(ClaudeTemplate);
        await using var db = _db.CreateDbContext();
        var json = (await db.AgentProfiles.AsNoTracking().SingleAsync(p => p.Id == id)).Configuration!;
        JsonNode.Parse(json)!["enabled"].Should().BeNull();
    }
}
