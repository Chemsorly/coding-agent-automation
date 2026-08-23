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
    private static readonly System.Text.Json.JsonSerializerOptions CaseInsensitiveOptions =
        new() { PropertyNameCaseInsensitive = true };
    private static readonly System.Text.Json.JsonSerializerOptions CamelCaseEnumOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };
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

    /// <summary>
    /// Builds an HttpClient authenticated as <paramref name="agentId"/> with the key the Job
    /// Controller would derive for it — HMAC-SHA256(master, agentId), matching
    /// <c>AgentApiKeyAuthHandler</c>.
    /// </summary>
    private HttpClient CreateAgentClient(string agentId)
    {
        using var hmac = new System.Security.Cryptography.HMACSHA256(
            System.Text.Encoding.UTF8.GetBytes(ApiWebApplicationFactory.ApiKey));
        var derivedKey = Convert.ToHexString(
            hmac.ComputeHash(System.Text.Encoding.UTF8.GetBytes(agentId))).ToLowerInvariant();

        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", derivedKey);
        return client;
    }

    /// <summary>
    /// An agent-derived key reaches the assignment for the work item it was dispatched for.
    /// </summary>
    [Fact]
    public async Task WorkItemAssignment_AllowsAgentDerivedKey_ForItsOwnWorkItem()
    {
        var agentId = "work-item-agent-456";
        var workItemId = SeedWorkItemAssignedTo(agentId);

        using var agentClient = CreateAgentClient(agentId);

        var response = await agentClient.GetAsync(
            $"/api/work-items/{workItemId}/assignment?agentId={agentId}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// The same key must not reach another work item's assignment. The payload carries repository
    /// tokens and project secrets, so an unbound agent key would expose every credential in the
    /// system to any single compromised pod.
    /// </summary>
    [Fact]
    public async Task WorkItemAssignment_RejectsAgentDerivedKey_ForAnotherAgentsWorkItem()
    {
        var callerAgentId = "nosy-agent-1";
        var otherAgentId = "victim-agent-2";
        SeedWorkItemAssignedTo(callerAgentId);
        var foreignWorkItemId = SeedWorkItemAssignedTo(otherAgentId);

        using var agentClient = CreateAgentClient(callerAgentId);

        var response = await agentClient.GetAsync(
            $"/api/work-items/{foreignWorkItemId}/assignment?agentId={callerAgentId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "an agent key must only reach the work item it was dispatched for");
    }

    /// <summary>
    /// Control-plane routes are operator-only. An agent that could claim, requeue or enumerate
    /// work items could starve or hijack the whole queue.
    /// </summary>
    [Theory]
    [InlineData("/api/work-items/pending")]
    [InlineData("/api/work-items/active")]
    [InlineData("/api/work-items/staleness")]
    public async Task WorkItemControlPlaneEndpoints_RejectAgentDerivedKey(string path)
    {
        var agentId = "control-plane-agent-789";
        using var agentClient = CreateAgentClient(agentId);

        var response = await agentClient.GetAsync($"{path}?agentId={agentId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            $"{path} is control plane and must require the operator key");
    }

    [Fact]
    public async Task WorkItemControlPlaneEndpoints_AllowOperatorKey()
    {
        var response = await _client.GetAsync("/api/work-items/pending");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Seeds a Dispatched work item owned by <paramref name="agentId"/>, mirroring what the Job
    /// Controller records when it claims an item (AssignedAgentId = the K8s Job name, which the
    /// pod reports as its AGENT_ID).
    /// </summary>
    private Guid SeedWorkItemAssignedTo(string agentId)
    {
        var id = Guid.NewGuid();
        using var db = _factory.CreateDbContext();
        db.WorkItems.Add(new WorkItemEntity
        {
            Id = id,
            TaskType = WorkItemTaskType.Implementation,
            IssueIdentifier = $"issue-{id:N}",
            IssueProviderConfigId = "prov-1",
            Status = WorkItemStatus.Dispatched,
            AgentSelector = "",
            CreatedAt = DateTimeOffset.UtcNow,
            DispatchedAt = DateTimeOffset.UtcNow,
            AssignedAgentId = agentId,
            TimeoutSeconds = 3600,
            Payload = JsonSerializer.Serialize(new JobDistributionRequest
            {
                IssueIdentifier = new IssueIdentifier($"issue-{id:N}"),
                IssueProviderConfigId = "prov-1",
                RepoProviderConfigId = "repo-1",
                InitiatedBy = "test",
                TaskType = WorkItemTaskType.Implementation,
                AgentSelector = "",
                TimeoutSeconds = 3600
            }, PipelineJsonOptions.Default)
        });
        db.SaveChanges();
        return id;
    }

    // ── Provider config redaction ─────────────────────────────────────────────────

    /// <summary>
    /// The default read is redacted — this is what the Blazor settings pages receive.
    /// </summary>
    [Fact]
    public async Task GetProviderConfigs_RedactsByDefault()
    {
        SeedProviderConfig(ProviderKind.Repository, "redact-default", "tok-live-value");

        var configs = await _client.GetFromJsonAsync<List<ProviderConfig>>(
            $"/api/config/provider-configs?kind={ProviderKind.Repository}", PipelineJsonOptions.Default);

        var config = configs!.Single(c => c.Id == "redact-default");
        config.Secrets!["token"].Should().Be("****", "the UI must never receive live credentials");
        config.Settings!["baseUrl"].Should().Be("****");
    }

    /// <summary>
    /// includeSecrets=true returns live values — what
    /// <c>IPipelineApiConfigClient.GetProviderConfigsWithSecretsAsync</c> requests. The monolith's
    /// config-store adapters use that form because the configs they load are embedded verbatim in
    /// the job payload an agent executes with: RunEnvironmentSetupStep writes Secrets into the run
    /// environment and the provider resolvers read tokens and base URLs from Settings. Serving the
    /// dispatch path redacted ships every job with "****" in place of its credentials.
    /// </summary>
    [Fact]
    public async Task GetProviderConfigs_WithIncludeSecrets_ReturnsLiveValues()
    {
        SeedProviderConfig(ProviderKind.Agent, "redact-optin", "tok-live-value");

        var configs = await _client.GetFromJsonAsync<List<ProviderConfig>>(
            $"/api/config/provider-configs?kind={ProviderKind.Agent}&includeSecrets=true",
            PipelineJsonOptions.Default);

        var config = configs!.Single(c => c.Id == "redact-optin");
        config.Secrets!["token"].Should().Be("tok-live-value",
            "the dispatch path cannot build a working job payload from masked credentials");
        config.Settings!["baseUrl"].Should().Be("https://live.example");
    }

    /// <summary>
    /// The unredacted form stays behind the operator key, like the rest of /api/config.
    /// </summary>
    [Fact]
    public async Task GetProviderConfigs_WithIncludeSecrets_RejectsAgentDerivedKey()
    {
        var agentId = "secret-peeker-1";
        using var agentClient = CreateAgentClient(agentId);

        var response = await agentClient.GetAsync(
            $"/api/config/provider-configs?kind={ProviderKind.Repository}&includeSecrets=true&agentId={agentId}");

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private void SeedProviderConfig(ProviderKind kind, string id, string secretValue)
    {
        var config = new ProviderConfig
        {
            Id = id,
            Kind = kind,
            DisplayName = id,
            ProviderType = "github",
            Settings = new Dictionary<string, string> { ["baseUrl"] = "https://live.example" },
            Secrets = new Dictionary<string, string> { ["token"] = secretValue }
        };

        using var db = _factory.CreateDbContext();
        db.ProviderConfigs.Add(new ProviderConfigEntity
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            DisplayName = config.DisplayName,
            ProviderType = config.ProviderType,
            Enabled = true,
            Configuration = JsonSerializer.Serialize(config, PipelineJsonOptions.Default)
        });
        db.SaveChanges();
    }

    // ── Config export ─────────────────────────────────────────────────────────────

    /// <summary>
    /// GET /api/config/export must emit provider Settings and Secrets unredacted so that
    /// export → import restores a working configuration.
    ///
    /// POST /api/config/import writes the bundle verbatim, so redacting the export would make
    /// the documented backup/restore path silently replace every credential with a mask string.
    /// Redaction belongs on the read endpoints (<c>GET /api/config/providers*</c>), which are
    /// covered separately; export is operator-tier and is treated as a secret artefact.
    /// </summary>
    [Fact]
    public async Task ConfigExport_EmitsProviderSecretsAndSettingsUnredacted()
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

        // Deserialize the bundle and inspect the ProviderConfig blob
        var bundle = JsonSerializer.Deserialize<ConfigBundle>(json,
            CaseInsensitiveOptions);
        bundle.Should().NotBeNull();
        bundle!.ProviderConfigs.Should().NotBeNullOrEmpty();

        var exported = bundle.ProviderConfigs!.FirstOrDefault(c => c.Id == providerId);
        exported.Should().NotBeNull("the seeded provider config must appear in the export");

        // Parse the embedded Configuration blob
        var parsedConfig = JsonSerializer.Deserialize<ProviderConfig>(exported!.Configuration!,
            PipelineJsonOptions.Default);
        parsedConfig.Should().NotBeNull();

        parsedConfig!.Settings.Should().ContainKey("token");
        parsedConfig.Settings!["token"].Should().Be("my-secret-value",
            "an export that masks Settings values cannot be imported back into a working system");

        parsedConfig.Secrets.Should().ContainKey("apiKey");
        parsedConfig.Secrets!["apiKey"].Should().Be("real-key",
            "an export that masks Secrets values cannot be imported back into a working system");

        parsedConfig.Settings["token"].Should().NotBe("****");
        parsedConfig.Secrets["apiKey"].Should().NotBe("****");
    }

    /// <summary>
    /// The full backup/restore path: export the config, wipe a provider's credentials, import the
    /// exported bundle, and confirm the original values are back. This is the behaviour the
    /// redacted export silently broke — the round-trip completed "successfully" while replacing
    /// every credential with a mask string.
    /// </summary>
    [Fact]
    public async Task ConfigExportThenImport_RestoresProviderCredentials()
    {
        var providerId = Guid.NewGuid();
        var original = new ProviderConfig
        {
            Id = providerId.ToString(),
            Kind = ProviderKind.Repository,
            DisplayName = "Round Trip Provider",
            ProviderType = "github",
            Settings = new Dictionary<string, string> { ["baseUrl"] = "https://ghe.internal" },
            Secrets = new Dictionary<string, string> { ["pat"] = "ghp_roundtrip_value" }
        };

        using (var db = _factory.CreateDbContext())
        {
            db.ProviderConfigs.Add(new ProviderConfigEntity
            {
                Id = providerId,
                Kind = original.Kind,
                DisplayName = original.DisplayName,
                ProviderType = original.ProviderType,
                Enabled = true,
                Configuration = JsonSerializer.Serialize(original, PipelineJsonOptions.Default)
            });
            db.SaveChanges();
        }

        var exportResponse = await _client.GetAsync("/api/config/export");
        exportResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var exportedBytes = await exportResponse.Content.ReadAsByteArrayAsync();

        // Simulate a restore into a cluster whose credentials have been lost.
        using (var db = _factory.CreateDbContext())
        {
            var entity = db.ProviderConfigs.Single(p => p.Id == providerId);
            entity.Configuration = JsonSerializer.Serialize(
                new ProviderConfig
                {
                    Id = original.Id,
                    Kind = original.Kind,
                    DisplayName = original.DisplayName,
                    ProviderType = original.ProviderType,
                    Settings = new Dictionary<string, string> { ["baseUrl"] = "wiped" },
                    Secrets = new Dictionary<string, string> { ["pat"] = "wiped" }
                },
                PipelineJsonOptions.Default);
            db.SaveChanges();
        }

        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(exportedBytes);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        content.Add(fileContent, "file", "pipeline-config-export.json");

        var importResponse = await _client.PostAsync("/api/config/import", content);
        importResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        using (var db = _factory.CreateDbContext())
        {
            var restored = JsonSerializer.Deserialize<ProviderConfig>(
                db.ProviderConfigs.Single(p => p.Id == providerId).Configuration!,
                PipelineJsonOptions.Default);

            restored.Should().NotBeNull();
            restored!.Settings!["baseUrl"].Should().Be("https://ghe.internal",
                "import must restore the exported Settings values");
            restored.Secrets!["pat"].Should().Be("ghp_roundtrip_value",
                "import must restore the exported Secrets values");
        }
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

        var bundleJson = JsonSerializer.Serialize(bundle, CamelCaseEnumOptions);

        using var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(bundleJson))
        {
            Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") }
        }, "file", "import.json");

        var importResponse = await _client.PostAsync("/api/config/import", content);
        importResponse.StatusCode.Should().Be(HttpStatusCode.OK,
            "POST /api/config/import must succeed with a valid bundle");

        var importResult = await importResponse.Content.ReadFromJsonAsync<ImportExportResult>(
            CaseInsensitiveOptions);
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
