using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure.Persistence.Entities;
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

    // ── Config export ─────────────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/config/export must redact Settings and Secrets values ("****") in the
    /// ProviderConfig.Configuration blob while preserving key names (Req 6.4a).
    /// </summary>
    [Fact]
    public async Task ConfigExport_RedactsProviderConfigSecretsAndSettings()
    {
        var providerId = Guid.NewGuid();
        var providerConfig = new ProviderConfig
        {
            Id = providerId.ToString(),
            Kind = ProviderKind.Issue,
            DisplayName = "Export Test Provider",
            ProviderType = "github",
            Settings = new Dictionary<string, string>
            {
                ["token"] = "my-secret-value"
            },
            Secrets = new Dictionary<string, string>
            {
                ["apiKey"] = "real-key"
            }
        };

        // Seed ProviderConfigEntity with Configuration = serialized ProviderConfig JSON
        using (var db = _factory.CreateDbContext())
        {
            db.ProviderConfigs.Add(new ProviderConfigEntity
            {
                Id = providerId,
                Kind = ProviderKind.Issue,
                DisplayName = providerConfig.DisplayName,
                ProviderType = providerConfig.ProviderType,
                Enabled = true,
                Configuration = JsonSerializer.Serialize(providerConfig, PipelineJsonOptions.Default)
            });
            db.SaveChanges();
        }

        var response = await _client.GetAsync("/api/config/export");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Verify Content-Type and Content-Disposition filename
        response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");
        response.Content.Headers.ContentDisposition.Should().NotBeNull();
        response.Content.Headers.ContentDisposition!.FileName.Should().NotBeNullOrEmpty();

        var json = await response.Content.ReadAsStringAsync();

        // Real secret values must NOT appear anywhere in the export
        json.Should().NotContain("my-secret-value",
            "Settings values must be redacted in export (Req 6.4a)");
        json.Should().NotContain("real-key",
            "Secrets values must be redacted in export (Req 6.4a)");

        // Deserialize the bundle and inspect the ProviderConfig blob
        var bundle = JsonSerializer.Deserialize<ConfigBundle>(json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        bundle.Should().NotBeNull();
        bundle!.ProviderConfigs.Should().NotBeNullOrEmpty();

        var exported = bundle.ProviderConfigs!.FirstOrDefault(c => c.Id == providerId);
        exported.Should().NotBeNull("the seeded provider config must appear in the export");

        // Parse the embedded Configuration blob
        var parsedConfig = JsonSerializer.Deserialize<ProviderConfig>(exported!.Configuration!,
            PipelineJsonOptions.Default);
        parsedConfig.Should().NotBeNull();

        // Key names preserved, values redacted to "****"
        parsedConfig!.Settings.Should().ContainKey("token",
            "Settings key names must be preserved in the export");
        parsedConfig.Settings!["token"].Should().Be("****",
            "Settings values must be replaced with '****' in the export");

        parsedConfig.Secrets.Should().ContainKey("apiKey",
            "Secrets key names must be preserved in the export");
        parsedConfig.Secrets!["apiKey"].Should().Be("****",
            "Secrets values must be replaced with '****' in the export");
    }

    [Fact]
    public async Task ConfigExport_RequiresOperatorKey_RejectsAgentDerivedKey()
    {
        var agentId = "export-agent-789";
        var hmac = new System.Security.Cryptography.HMACSHA256(
            Encoding.UTF8.GetBytes(ApiWebApplicationFactory.ApiKey));
        var derivedKey = Convert.ToHexString(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(agentId))).ToLowerInvariant();

        using var agentClient = _factory.CreateClient();
        agentClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", derivedKey);

        var response = await agentClient.GetAsync($"/api/config/export?agentId={agentId}");
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "GET /api/config/export must reject agent-derived keys (OperatorApiKey policy)");
    }

    // ── Config import ─────────────────────────────────────────────────────────────

    /// <summary>
    /// POST /api/config/import is destructive: it clears all existing config rows before
    /// inserting the bundle. Verify the original provider is gone and the imported one exists.
    /// </summary>
    [Fact]
    public async Task ConfigImport_IsDestructive_ClearsExistingAndInsertsBundle()
    {
        // Seed an existing ProviderConfig via the API so it has a valid Configuration blob
        // and is visible through GET /api/config/provider-configs
        var originalId = Guid.NewGuid().ToString();
        var originalConfig = new ProviderConfig
        {
            Id = originalId,
            Kind = ProviderKind.Issue,
            DisplayName = "Original Provider (should be deleted)",
            ProviderType = "github",
            Settings = new Dictionary<string, string>(),
            Secrets = new Dictionary<string, string>()
        };
        var seedResponse = await _client.PutAsJsonAsync("/api/config/provider-configs", originalConfig, PipelineJsonOptions.Default);
        seedResponse.StatusCode.Should().Be(HttpStatusCode.OK, "seeding original provider must succeed");

        // Verify original exists before import
        var beforeResponse = await _client.GetAsync($"/api/config/provider-configs?kind={ProviderKind.Issue}");
        beforeResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var beforeConfigs = await beforeResponse.Content.ReadFromJsonAsync<List<ProviderConfig>>(PipelineJsonOptions.Default);
        beforeConfigs.Should().Contain(c => c.Id == originalId,
            "seeded provider must exist before import");

        // Build an import bundle with a different ProviderConfig
        var newProviderId = Guid.NewGuid();
        var newProvider = new ProviderConfig
        {
            Id = newProviderId.ToString(),
            Kind = ProviderKind.Issue,
            DisplayName = "Imported Provider",
            ProviderType = "gitlab",
            Settings = new Dictionary<string, string> { ["url"] = "https://gitlab.example.com" },
            Secrets = new Dictionary<string, string>()
        };

        var bundle = new ConfigBundle
        {
            ProviderConfigs = new List<ProviderConfigDto>
            {
                new ProviderConfigDto
                {
                    Id = newProviderId,
                    Kind = ProviderKind.Issue,
                    DisplayName = newProvider.DisplayName,
                    ProviderType = newProvider.ProviderType,
                    Enabled = true,
                    Configuration = JsonSerializer.Serialize(newProvider, PipelineJsonOptions.Default)
                }
            }
        };

        var bundleJson = JsonSerializer.Serialize(bundle, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        });

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(bundleJson))
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") }
        }, "file", "import.json");

        var importResponse = await _client.PostAsync("/api/config/import", content);
        importResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "POST /api/config/import must succeed with a valid bundle");

        var importResult = await importResponse.Content.ReadFromJsonAsync<ImportExportResult>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        importResult.Should().NotBeNull();
        importResult!.Success.Should().BeTrue();

        // Original provider must be gone (destructive import)
        var afterResponse = await _client.GetAsync($"/api/config/provider-configs?kind={ProviderKind.Issue}");
        afterResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterConfigs = await afterResponse.Content.ReadFromJsonAsync<List<ProviderConfig>>(PipelineJsonOptions.Default);
        afterConfigs.Should().NotBeNull();

        afterConfigs!.Should().NotContain(c => c.Id == originalId,
            "import is destructive — the original provider config must be removed");
        afterConfigs.Should().Contain(c => c.Id == newProviderId.ToString(),
            "the imported provider config must be present after import");
    }

    [Fact]
    public async Task ConfigImport_RequiresOperatorKey_RejectsAgentDerivedKey()
    {
        var agentId = "import-agent-012";
        var hmac = new System.Security.Cryptography.HMACSHA256(
            Encoding.UTF8.GetBytes(ApiWebApplicationFactory.ApiKey));
        var derivedKey = Convert.ToHexString(
            hmac.ComputeHash(Encoding.UTF8.GetBytes(agentId))).ToLowerInvariant();

        using var agentClient = _factory.CreateClient();
        agentClient.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", derivedKey);

        // Send a minimal valid multipart body — auth rejection happens before body parsing
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("{}")), "file", "import.json");

        var response = await agentClient.PostAsync($"/api/config/import?agentId={agentId}", content);
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "POST /api/config/import must reject agent-derived keys (OperatorApiKey policy)");
    }

    [Fact]
    public async Task ConfigImport_EmptyFile_Returns400()
    {
        // POST with empty file body
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(Array.Empty<byte>()), "file", "empty.json");

        var response = await _client.PostAsync("/api/config/import", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "POST /api/config/import with an empty file must return 400");
    }

    [Fact]
    public async Task ConfigImport_MalformedJson_Returns400()
    {
        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("{ this is not valid json }")),
            "file", "bad.json");

        var response = await _client.PostAsync("/api/config/import", content);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "POST /api/config/import with malformed JSON must return 400");
    }
}
