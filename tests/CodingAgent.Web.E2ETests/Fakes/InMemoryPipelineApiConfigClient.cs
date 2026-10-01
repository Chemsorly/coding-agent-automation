using System.Text.Json;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.E2ETests.Fakes;

/// <summary>
/// In-process stand-in for <see cref="IPipelineApiConfigClient"/>, backed by the same
/// <see cref="InMemoryConfigurationStore"/> the tests seed and assert against.
///
/// From Spec 045 the Blazor UI reads and writes all configuration through the Pipeline API
/// client rather than a config store, so replacing only the store leaves the UI talking to a
/// real HTTP client. Pointed at an unreachable address that does not fail fast: the config
/// stores retry and <c>AutoStartPipelineLoopAsync</c> retries for up to ten minutes, so the test
/// host hangs rather than erroring. Substituting the client here keeps the UI and the test
/// fixture reading the same state, which is what the store substitution was doing before 045.
/// </summary>
public sealed class InMemoryPipelineApiConfigClient : IPipelineApiConfigClient
{
    private readonly InMemoryConfigurationStore _store;
    private readonly Dictionary<string, string> _keyValues = [];

    public InMemoryPipelineApiConfigClient(InMemoryConfigurationStore store) => _store = store;

    // ── Pipeline config ──────────────────────────────────────────────────
    public Task<PipelineConfiguration> GetPipelineConfigAsync(CancellationToken ct = default)
        => _store.LoadPipelineConfigAsync(ct);

    public Task SavePipelineConfigAsync(PipelineConfiguration config, CancellationToken ct = default)
        => _store.SavePipelineConfigAsync(config, ct);

    public Task UpdatePipelineConfigAsync(
        Func<PipelineConfiguration, PipelineConfiguration> transform, CancellationToken ct = default)
        => _store.UpdatePipelineConfigAsync(transform, ct);

    // ── Provider configs ─────────────────────────────────────────────────
    // Both forms return the seeded values: the in-memory store never redacts. Redaction is the
    // API endpoint's behaviour and is covered by ConfigEndpointTests, not here.
    public Task<IReadOnlyList<ProviderConfig>> GetProviderConfigsAsync(
        ProviderKind kind, CancellationToken ct = default)
        => _store.LoadProviderConfigsAsync(kind, ct);

    public Task<IReadOnlyList<ProviderConfig>> GetProviderConfigsWithSecretsAsync(
        ProviderKind kind, CancellationToken ct = default)
        => _store.LoadProviderConfigsAsync(kind, ct);

    public Task SaveProviderConfigAsync(ProviderConfig config, CancellationToken ct = default)
        => _store.SaveProviderConfigAsync(config, ct);

    public Task DeleteProviderConfigAsync(string id, ProviderKind kind, CancellationToken ct = default)
        => _store.DeleteProviderConfigAsync(id, kind, ct);

    // ── Agent profiles ───────────────────────────────────────────────────
    public Task<IReadOnlyList<AgentProfile>> GetAgentProfilesAsync(CancellationToken ct = default)
        => _store.LoadAgentProfilesAsync(ct);

    public Task SaveAgentProfileAsync(AgentProfile profile, CancellationToken ct = default)
        => _store.SaveAgentProfileAsync(profile, ct);

    public Task DeleteAgentProfileAsync(string id, CancellationToken ct = default)
        => _store.DeleteAgentProfileAsync(id, ct);

    // ── Quality gates ────────────────────────────────────────────────────
    public Task<IReadOnlyList<QualityGateConfiguration>> GetQualityGateConfigsAsync(CancellationToken ct = default)
        => _store.LoadQualityGateConfigsAsync(ct);

    public Task SaveQualityGateConfigAsync(QualityGateConfiguration config, CancellationToken ct = default)
        => _store.SaveQualityGateConfigAsync(config, ct);

    public Task DeleteQualityGateConfigAsync(string id, CancellationToken ct = default)
        => _store.DeleteQualityGateConfigAsync(id, ct);

    // ── Reviewers ────────────────────────────────────────────────────────
    public Task<IReadOnlyList<ReviewerConfiguration>> GetReviewerConfigsAsync(CancellationToken ct = default)
        => _store.LoadReviewerConfigsAsync(ct);

    public Task SaveReviewerConfigAsync(ReviewerConfiguration config, CancellationToken ct = default)
        => _store.SaveReviewerConfigAsync(config, ct);

    public Task DeleteReviewerConfigAsync(string id, CancellationToken ct = default)
        => _store.DeleteReviewerConfigAsync(id, ct);

    public Task ResetReviewerConfigsToDefaultAsync(CancellationToken ct = default)
        => _store.ResetReviewerConfigsToDefaultAsync(ct);

    // ── Projects ─────────────────────────────────────────────────────────
    public Task<IReadOnlyList<PipelineProject>> GetProjectsAsync(CancellationToken ct = default)
        => _store.LoadProjectsAsync(ct);

    public Task<PipelineProject?> GetProjectByIdAsync(string id, CancellationToken ct = default)
        => _store.GetProjectByIdAsync(id, ct);

    public Task SaveProjectAsync(PipelineProject project, CancellationToken ct = default)
        => _store.SaveProjectAsync(project, ct);

    public Task DeleteProjectAsync(string id, CancellationToken ct = default)
        => _store.DeleteProjectAsync(id, ct);

    public Task<bool> HasEnabledTemplatesAsync(CancellationToken ct = default)
        => _store.HasEnabledTemplatesAsync(ct);

    // ── Templates ────────────────────────────────────────────────────────
    public Task<IReadOnlyList<PipelineJobTemplate>> GetAllTemplatesAsync(CancellationToken ct = default)
        => _store.LoadAllTemplatesAsync(ct);

    public Task<IReadOnlyList<PipelineJobTemplate>> GetTemplatesForProjectAsync(string projectId, CancellationToken ct = default)
        => _store.LoadTemplatesForProjectAsync(projectId, ct);

    public Task SaveTemplateAsync(string projectId, PipelineJobTemplate template, CancellationToken ct = default)
        => _store.SaveTemplateAsync(projectId, template, ct);

    public Task DeleteTemplateAsync(string projectId, string templateId, CancellationToken ct = default)
        => _store.DeleteTemplateAsync(projectId, new TemplateId(templateId), ct);

    public Task MoveTemplateAsync(ProjectId sourceProjectId, ProjectId targetProjectId, string templateId, CancellationToken ct = default)
        => _store.MoveTemplateAsync(sourceProjectId, targetProjectId, new TemplateId(templateId), ct);

    // ── Key-value ────────────────────────────────────────────────────────
    public Task<string?> GetKeyValueAsync(string key, CancellationToken ct = default)
        => Task.FromResult(_keyValues.TryGetValue(key, out var v) ? v : null);

    public Task SetKeyValueAsync(string key, string value, CancellationToken ct = default)
    {
        _keyValues[key] = value;
        return Task.CompletedTask;
    }

    public Task DeleteKeyValueAsync(string key, CancellationToken ct = default)
    {
        _keyValues.Remove(key);
        return Task.CompletedTask;
    }

    // ── Import / export ──────────────────────────────────────────────────
    // Implemented to support E2E tests for the Data Management Settings page (Issue #3105).
    // The bundle format matches the real API's ConfigBundle structure, produced by
    // ConfigEndpoints.ExportConfigAsync: camelCase property names, integer enum values for
    // ProviderKind (ExportOptions has no JsonStringEnumConverter), and each entity's domain
    // model object serialized as a JSON string in the "configuration" field.
    // The ConfigBundle, ProviderConfigDto, etc. DTOs live in CodingAgent.Api which is NOT
    // referenced from this project, so the bundle is assembled from anonymous types.

    /// <summary>
    /// Serializes the in-memory store into the same JSON bundle format produced by
    /// <c>GET /api/config/export</c>. Returns UTF-8 encoded JSON bytes.
    /// </summary>
    public async Task<byte[]> ExportConfigAsync(CancellationToken ct = default)
    {
        var pipelineConfig = await _store.LoadPipelineConfigAsync(ct);
        var pipelineConfigJson = JsonSerializer.Serialize(pipelineConfig, PipelineJsonOptions.Default);

        // Collect providers from all ProviderKind values — each kind is stored separately.
        var allProviders = new List<object>();
        foreach (var kind in Enum.GetValues<ProviderKind>())
        {
            var configs = await _store.LoadProviderConfigsAsync(kind, ct);
            foreach (var cfg in configs)
            {
                allProviders.Add(new
                {
                    id = Guid.TryParse(cfg.Id, out var g) ? g : Guid.NewGuid(),
                    // Integer — real ExportOptions has no JsonStringEnumConverter
                    kind = (int)cfg.Kind,
                    displayName = cfg.DisplayName,
                    providerType = cfg.ProviderType,
                    // TODO [WARNING]: The real ConfigEndpoints.ExportConfigAsync emits cfg.Enabled here.
                    // This fake hard-codes true, so a disabled provider would round-trip as enabled.
                    // Fix: replace 'true' with cfg.Enabled (requires exposing Enabled on ProviderConfig).
                    enabled = true,
                    configuration = JsonSerializer.Serialize(cfg, PipelineJsonOptions.Default)
                });
            }
        }

        var agentProfiles = await _store.LoadAgentProfilesAsync(ct);
        var qgConfigs = await _store.LoadQualityGateConfigsAsync(ct);
        var reviewerConfigs = await _store.LoadReviewerConfigsAsync(ct);

        // Build project → templates mapping for correct projectId on each template.
        var projects = await _store.LoadProjectsAsync(ct);
        var templatesByProject = new List<object>();
        foreach (var project in projects)
        {
            var templates = await _store.LoadTemplatesForProjectAsync(project.Id, ct);
            foreach (var t in templates)
            {
                templatesByProject.Add(new
                {
                    id = Guid.TryParse(t.Id, out var tg) ? tg : Guid.NewGuid(),
                    // TODO [WARNING]: When project.Id is not a GUID (e.g. a legacy string key), this falls back
                    // to Guid.Empty. On import, SaveTemplateAsync is called with "00000000-0000-0000-0000-000000000000"
                    // as the projectId, which may not match any restored project, leaving the template orphaned.
                    // Fix: emit project.Id as a string rather than a Guid so non-GUID IDs survive the round trip.
                    projectId = Guid.TryParse(project.Id, out var pg) ? pg : Guid.Empty,
                    name = t.Name,
                    configuration = JsonSerializer.Serialize(t, PipelineJsonOptions.Default)
                });
            }
        }

        var bundle = new
        {
            pipelineConfig = pipelineConfigJson,
            providerConfigs = allProviders,
            agentProfiles = agentProfiles.Select(a => new
            {
                id = Guid.TryParse(a.Id, out var g) ? g : Guid.NewGuid(),
                name = a.DisplayName,
                configuration = JsonSerializer.Serialize(a, PipelineJsonOptions.Default)
            }).ToList(),
            qualityGateConfigs = qgConfigs.Select(q => new
            {
                id = Guid.TryParse(q.Id, out var g) ? g : Guid.NewGuid(),
                name = q.DisplayName,
                configuration = JsonSerializer.Serialize(q, PipelineJsonOptions.Default)
            }).ToList(),
            reviewerConfigs = reviewerConfigs.Select(r => new
            {
                id = Guid.TryParse(r.Id, out var g) ? g : Guid.NewGuid(),
                name = r.DisplayName,
                configuration = JsonSerializer.Serialize(r, PipelineJsonOptions.Default)
            }).ToList(),
            projects = projects.Select(p => new
            {
                id = Guid.TryParse(p.Id, out var g) ? g : Guid.NewGuid(),
                name = p.Name,
                enabled = p.Enabled,
                description = p.Description,
                settings = (string?)null
            }).ToList(),
            jobTemplates = templatesByProject
        };

        // camelCase + indented, no converters — matches real ExportOptions
        // TODO [WARNING]: A new JsonSerializerOptions instance is allocated on every ExportConfigAsync call.
        // JsonSerializerOptions is not thread-safe to mutate after first use; creating a new instance per
        // call also bypasses the internal serializer metadata cache, causing repeated cache misses and
        // unnecessary allocations. Move this to a static readonly field.
        var exportOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };
        return System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(bundle, exportOptions));
    }

    /// <summary>
    /// Deserializes a bundle JSON stream and replaces all store contents with the bundle data.
    /// Uses <see cref="InMemoryConfigurationStore.ClearWithoutSeed"/> rather than
    /// <see cref="InMemoryConfigurationStore.Reset"/> to avoid mixing seeded defaults with
    /// imported data (Reset always calls SeedDefaults).
    /// </summary>
    public async Task ImportConfigAsync(Stream jsonStream, string fileName, CancellationToken ct = default)
    {
        string json;
        using (var reader = new System.IO.StreamReader(jsonStream))
            json = await reader.ReadToEndAsync(ct);

        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("Import file is empty or invalid.");

        // TODO [WARNING]: If _store.ClearWithoutSeed() or any subsequent Save* call throws synchronously
        // before the 'using (doc)' block starts, the JsonDocument leaks because it is not disposed on
        // that exception path. Place doc inside a single 'using var doc = JsonDocument.Parse(json)' at
        // the point of assignment rather than relying on the outer using block that starts after
        // ClearWithoutSeed(). Also: every JsonSerializer.Deserialize<T> call inside the using block
        // ignores the ct parameter for the actual deserialization (only Save* calls receive ct). If the
        // caller cancels the token between ClearWithoutSeed() and completion, the store is left partially
        // populated. A snapshot-and-restore pattern would prevent both the disposal and the torn-state
        // issues simultaneously.
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid JSON bundle: {ex.Message}", ex);
        }

        using (doc)
        {
            // Destructive: clear store without reseeding defaults before loading the bundle.
            // TODO [WARNING]: Inner JsonSerializer.Deserialize<T> calls below occur after ClearWithoutSeed.
            // If a typed field (e.g. pipelineConfig, or a configuration blob) contains structurally valid
            // JSON that fails to deserialize, the resulting JsonException is NOT caught here, leaving the
            // store partially populated after the clear. To fix, snapshot store state before clear and
            // restore it on any exception (copy-and-swap pattern), mirroring the real endpoint's
            // transactional behaviour.
            _store.ClearWithoutSeed();

            var root = doc.RootElement;

            // Restore pipeline config
            if (root.TryGetProperty("pipelineConfig", out var pcProp) && pcProp.ValueKind == JsonValueKind.String)
            {
                var pc = JsonSerializer.Deserialize<PipelineConfiguration>(pcProp.GetString()!, PipelineJsonOptions.Default);
                if (pc is not null) await _store.SavePipelineConfigAsync(pc, ct);
            }

            // Restore provider configs — read from the "configuration" blob, not the bundle envelope
            if (root.TryGetProperty("providerConfigs", out var providers) && providers.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in providers.EnumerateArray())
                {
                    if (!p.TryGetProperty("configuration", out var cfgProp) || cfgProp.ValueKind != JsonValueKind.String)
                        continue;
                    var cfg = JsonSerializer.Deserialize<ProviderConfig>(cfgProp.GetString()!, PipelineJsonOptions.Default);
                    if (cfg is not null) await _store.SaveProviderConfigAsync(cfg, ct);
                }
            }

            // Restore agent profiles
            if (root.TryGetProperty("agentProfiles", out var profiles) && profiles.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in profiles.EnumerateArray())
                {
                    if (!a.TryGetProperty("configuration", out var cfgProp) || cfgProp.ValueKind != JsonValueKind.String)
                        continue;
                    var profile = JsonSerializer.Deserialize<AgentProfile>(cfgProp.GetString()!, PipelineJsonOptions.Default);
                    if (profile is not null) await _store.SaveAgentProfileAsync(profile, ct);
                }
            }

            // Restore quality gate configs
            if (root.TryGetProperty("qualityGateConfigs", out var qgcs) && qgcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var q in qgcs.EnumerateArray())
                {
                    if (!q.TryGetProperty("configuration", out var cfgProp) || cfgProp.ValueKind != JsonValueKind.String)
                        continue;
                    var qg = JsonSerializer.Deserialize<QualityGateConfiguration>(cfgProp.GetString()!, PipelineJsonOptions.Default);
                    if (qg is not null) await _store.SaveQualityGateConfigAsync(qg, ct);
                }
            }

            // Restore reviewer configs
            if (root.TryGetProperty("reviewerConfigs", out var rcs) && rcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var r in rcs.EnumerateArray())
                {
                    if (!r.TryGetProperty("configuration", out var cfgProp) || cfgProp.ValueKind != JsonValueKind.String)
                        continue;
                    var rc = JsonSerializer.Deserialize<ReviewerConfiguration>(cfgProp.GetString()!, PipelineJsonOptions.Default);
                    if (rc is not null) await _store.SaveReviewerConfigAsync(rc, ct);
                }
            }

            // Restore projects — use bundle-level id/name/enabled fields to create the project
            if (root.TryGetProperty("projects", out var projs) && projs.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in projs.EnumerateArray())
                {
                    var name = p.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var enabled = p.TryGetProperty("enabled", out var e) && e.GetBoolean();
                    var description = p.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                        ? d.GetString() : null;
                    var idStr = p.TryGetProperty("id", out var i) ? i.ToString() : Guid.NewGuid().ToString();
                    var project = new PipelineProject
                    {
                        Id = idStr,
                        Name = name ?? "",
                        Enabled = enabled,
                        Description = description
                    };
                    await _store.SaveProjectAsync(project, ct);
                }
            }

            // Restore templates — associate with the project from the bundle's projectId field
            if (root.TryGetProperty("jobTemplates", out var templates) && templates.ValueKind == JsonValueKind.Array)
            {
                foreach (var t in templates.EnumerateArray())
                {
                    if (!t.TryGetProperty("configuration", out var cfgProp) || cfgProp.ValueKind != JsonValueKind.String)
                        continue;
                    var tmpl = JsonSerializer.Deserialize<PipelineJobTemplate>(cfgProp.GetString()!, PipelineJsonOptions.Default);
                    if (tmpl is null) continue;
                    var projectId = t.TryGetProperty("projectId", out var pid)
                        ? pid.ToString()
                        : WellKnownIds.DefaultProjectId;
                    await _store.SaveTemplateAsync(projectId, tmpl, ct);
                }
            }
        }
    }

    // ── Models ───────────────────────────────────────────────────────────
    public Task<(IReadOnlyList<AgentModelInfo> Models, string? Error)> GetModelsAsync(CancellationToken ct = default)
        => Task.FromResult<(IReadOnlyList<AgentModelInfo>, string?)>((Array.Empty<AgentModelInfo>(), null));

    /// <summary>Clears key-value state between tests. The backing store resets itself.</summary>
    public void Reset() => _keyValues.Clear();
}
