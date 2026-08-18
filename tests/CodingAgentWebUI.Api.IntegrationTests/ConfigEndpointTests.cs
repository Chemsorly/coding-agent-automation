using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Api.IntegrationTests;

/// <summary>
/// Integration tests for /api/config endpoints.
/// All endpoints require OperatorApiKey (raw master key with no agentId).
/// </summary>
[Collection(ApiIntegrationTestCollection.Name)]
public sealed class ConfigEndpointTests
{
    private readonly ApiWebApplicationFactory _factory;
    private readonly HttpClient _client;

    public ConfigEndpointTests(ApiWebApplicationFactory factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
        // Operator auth: master key, no agentId query parameter
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ApiWebApplicationFactory.ApiKey);
    }

    // ── AgentProfiles ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task AgentProfiles_TwoPutsWithSameId_ProduceOneRow()
    {
        var profileId = Guid.NewGuid().ToString();  // must be a parseable Guid (PostgresConfigurationStore validation)
        var profile = new AgentProfile
        {
            Id = profileId,
            DisplayName = "Test Agent",
            AgentProviderConfigId = "prov-test-1"
        };

        // Two PUTs with the same Id
        var r1 = await _client.PutAsJsonAsync("/api/config/agent-profiles", profile, PipelineJsonOptions.Default);
        r1.StatusCode.Should().Be(HttpStatusCode.OK);

        var profileUpdated = profile with { DisplayName = "Updated Agent" };
        var r2 = await _client.PutAsJsonAsync("/api/config/agent-profiles", profileUpdated, PipelineJsonOptions.Default);
        r2.StatusCode.Should().Be(HttpStatusCode.OK);

        // GET should return the profile (upsert — only one row)
        var response = await _client.GetAsync("/api/config/agent-profiles");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var profiles = await response.Content.ReadFromJsonAsync<List<AgentProfile>>(PipelineJsonOptions.Default);
        profiles.Should().NotBeNull();
        profiles!.Where(p => p.Id == profileId).Should().HaveCount(1,
            "two PUTs with the same ID must produce exactly one row (upsert semantics)");
    }

    // ── Key-value ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task KeyValue_GetPutGetRoundTrip()
    {
        var key = $"test-key-{Guid.NewGuid():N}";
        const string value = "hello-world";

        // Initially 404
        var get1 = await _client.GetAsync($"/api/config/key-value/{key}");
        get1.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // PUT
        var put = await _client.PutAsJsonAsync($"/api/config/key-value/{key}",
            new { value }, PipelineJsonOptions.Default);
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        // GET again — should return value
        var get2 = await _client.GetAsync($"/api/config/key-value/{key}");
        get2.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await get2.Content.ReadAsStringAsync();
        body.Should().Contain(value);
    }

    // ── ProviderConfigs ───────────────────────────────────────────────────────────

    [Fact]
    public async Task ProviderConfigs_SaveThenGetReturnsRedactedValues()
    {
        var config = new ProviderConfig
        {
            Id = Guid.NewGuid().ToString(),  // must be a parseable Guid (PostgresConfigurationStore validation)
            Kind = ProviderKind.Issue,
            DisplayName = "Test Provider",
            ProviderType = "github",
            Settings = new Dictionary<string, string>
            {
                ["apiToken"] = "super-secret-token",
                ["baseUrl"] = "https://api.github.com"
            },
            Secrets = new Dictionary<string, string>
            {
                ["privateKeyPem"] = "-----BEGIN RSA PRIVATE KEY-----\nMIIEowIBAAK...",
                ["webhookSecret"] = "wh-secret-value"
            }
        };

        // PUT with real values
        var put = await _client.PutAsJsonAsync("/api/config/provider-configs", config, PipelineJsonOptions.Default);
        put.StatusCode.Should().Be(HttpStatusCode.OK);

        // GET should return redacted values ("****")
        var response = await _client.GetAsync($"/api/config/provider-configs?kind={config.Kind}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var configs = await response.Content.ReadFromJsonAsync<List<ProviderConfig>>(PipelineJsonOptions.Default);
        configs.Should().NotBeNull();

        var saved = configs!.FirstOrDefault(c => c.Id == config.Id);
        saved.Should().NotBeNull();
        saved!.Settings.Should().NotBeNull();
        saved.Settings!.Values.Should().AllSatisfy(v => v.Should().Be("****"),
            "GET /api/config/provider-configs must redact all Settings values");
        saved.Settings.Keys.Should().BeEquivalentTo(config.Settings.Keys,
            "Settings key names must be preserved");
        saved.Secrets.Should().NotBeNull();
        saved.Secrets!.Values.Should().AllSatisfy(v => v.Should().Be("****"),
            "GET /api/config/provider-configs must redact all Secrets values");
        saved.Secrets.Keys.Should().BeEquivalentTo(config.Secrets.Keys,
            "Secrets key names must be preserved");
    }

    // ── Auth: operator vs agent key ────────────────────────────────────────────────

    [Fact]
    public async Task ConfigEndpoint_RequiresOperatorKey_RejectsAgentDerivedKey()
    {
        // Agent-derived key: agentId query param triggers HMAC derivation path in auth handler
        // which emits auth_kind=agent claim → OperatorApiKey policy rejects it
        using var agentClient = _factory.CreateClient();
        var agentId = "test-agent-123";
        var hmac = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes(ApiWebApplicationFactory.ApiKey));
        var derivedKey = Convert.ToHexString(
            hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(agentId))).ToLowerInvariant();

        agentClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", derivedKey);

        // Derived key without ?agentId would be treated as an unknown master key → 401
        // With ?agentId it becomes auth_kind=agent → 403 on OperatorApiKey policy
        var response = await agentClient.GetAsync($"/api/config/agent-profiles?agentId={agentId}");
        // Should be Forbidden (403) because auth_kind=agent is rejected by OperatorApiKey
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ConfigEndpoint_SucceedsWithMasterKey_NoAgentId()
    {
        // Master key with no agentId → auth_kind=operator → OperatorApiKey passes
        var response = await _client.GetAsync("/api/config/agent-profiles");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task WorkItemEndpoint_AllowsAgentDerivedKey()
    {
        // /api/work-items uses AgentApiKey policy — accepts both operator and agent keys
        var agentId = "work-item-agent-456";
        var hmac = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes(ApiWebApplicationFactory.ApiKey));
        var derivedKey = Convert.ToHexString(
            hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(agentId))).ToLowerInvariant();

        using var agentClient = _factory.CreateClient();
        agentClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", derivedKey);

        var response = await agentClient.GetAsync($"/api/work-items/pending?agentId={agentId}");
        // Should succeed (200) because AgentApiKey accepts auth_kind=agent
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
