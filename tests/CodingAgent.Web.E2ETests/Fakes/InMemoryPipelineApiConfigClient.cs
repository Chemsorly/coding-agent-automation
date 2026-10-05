using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.TestUtilities;

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
    // Produces a real bundle from the in-memory store so that E2E tests for the Data Management
    // UI section can exercise the full export → import round trip without a live API host.

    /// <summary>
    /// Serializes the current in-memory configuration to a JSON bundle that mirrors the shape
    /// produced by GET /api/config/export on the real API. Uses camelCase + JsonStringEnumConverter
    /// so the output is importable by <see cref="ImportConfigAsync"/>.
    /// </summary>
    public async Task<byte[]> ExportConfigAsync(CancellationToken ct = default)
    {
        var config = await _store.LoadPipelineConfigAsync(ct);
        var providerConfigs = new List<ProviderConfig>();
        foreach (var kind in Enum.GetValues<ProviderKind>())
        {
            var configs = await _store.LoadProviderConfigsAsync(kind, ct);
            providerConfigs.AddRange(configs);
        }

        var agentProfiles = await _store.LoadAgentProfilesAsync(ct);
        var qualityGateConfigs = await _store.LoadQualityGateConfigsAsync(ct);
        var reviewerConfigs = await _store.LoadReviewerConfigsAsync(ct);
        var projects = await _store.LoadProjectsAsync(ct);
        var templates = await _store.LoadAllTemplatesAsync(ct);

        var bundle = new ExportBundle
        {
            PipelineConfig = JsonSerializer.Serialize(config, PipelineJsonOptions.Default),
            // TODO [WARNING]: Guid.TryParse fallback silently replaces non-GUID IDs (e.g. seeded
            // string IDs) with freshly-generated random GUIDs on each export. The outer DTO Id
            // diverges from the original store Id. Currently harmless because ImportConfigAsync
            // re-deserializes the Id from the nested Configuration JSON, but any future logic that
            // reads the DTO Id during import (e.g. conflict detection) will see non-deterministic
            // values. Prefer Guid.Parse with an explicit error, or at minimum a Debug.Assert, so
            // the root cause is surfaced rather than hidden.
            ProviderConfigs = providerConfigs
                .Select(p => new ProviderConfigExportDto
                {
                    Id = Guid.TryParse(p.Id, out var g) ? g : Guid.NewGuid(),
                    Kind = p.Kind,
                    DisplayName = p.DisplayName,
                    ProviderType = p.ProviderType,
                    Enabled = true,
                    Configuration = JsonSerializer.Serialize(p, PipelineJsonOptions.Default)
                })
                .ToList(),
            AgentProfiles = agentProfiles
                .Select(a => new NamedConfigExportDto
                {
                    Id = Guid.TryParse(a.Id, out var g) ? g : Guid.NewGuid(),
                    Name = a.DisplayName,
                    Configuration = JsonSerializer.Serialize(a, PipelineJsonOptions.Default)
                })
                .ToList(),
            QualityGateConfigs = qualityGateConfigs
                .Select(q => new NamedConfigExportDto
                {
                    Id = Guid.TryParse(q.Id, out var g) ? g : Guid.NewGuid(),
                    Name = q.DisplayName,
                    Configuration = JsonSerializer.Serialize(q, PipelineJsonOptions.Default)
                })
                .ToList(),
            ReviewerConfigs = reviewerConfigs
                .Select(r => new NamedConfigExportDto
                {
                    Id = Guid.TryParse(r.Id, out var g) ? g : Guid.NewGuid(),
                    Name = r.DisplayName,
                    Configuration = JsonSerializer.Serialize(r, PipelineJsonOptions.Default)
                })
                .ToList(),
            Projects = projects
                .Select(p => new ProjectExportDto
                {
                    Id = Guid.TryParse(p.Id, out var g) ? g : Guid.NewGuid(),
                    Name = p.Name,
                    Enabled = p.Enabled,
                    Description = p.Description
                })
                .ToList(),
            JobTemplates = templates
                .Select(t => new JobTemplateExportDto
                {
                    Id = Guid.TryParse(t.Id, out var g) ? g : Guid.NewGuid(),
                    Name = t.Name,
                    Configuration = JsonSerializer.Serialize(t, PipelineJsonOptions.Default)
                })
                .ToList()
        };

        var json = JsonSerializer.Serialize(bundle, ExportJsonOptions);
        return Encoding.UTF8.GetBytes(json);
    }

    /// <summary>
    /// Deserializes the uploaded bundle and replaces the in-memory store contents.
    /// Mirrors the destructive semantics of POST /api/config/import on the real API.
    /// </summary>
    public async Task ImportConfigAsync(Stream jsonStream, string fileName, CancellationToken ct = default)
    {
        // TODO [WARNING]: Pass leaveOpen: true to avoid closing the caller's stream on reader disposal.
        // new StreamReader(stream) closes the underlying stream when disposed, violating the typical
        // interface contract if the caller holds and reuses the stream after this call returns.
        // Fix: new StreamReader(jsonStream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: -1, leaveOpen: true)
        using var reader = new StreamReader(jsonStream, Encoding.UTF8);
        var json = await reader.ReadToEndAsync(ct);

        if (string.IsNullOrWhiteSpace(json))
            throw new InvalidOperationException("Import file is empty.");

        ExportBundle bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<ExportBundle>(json, ImportJsonOptions)
                ?? throw new InvalidOperationException("Failed to deserialize import bundle.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"Invalid JSON in import file: {ex.Message}", ex);
        }

        // TODO [WARNING]: The import is non-atomic. Each entity type is cleared then re-inserted
        // independently. If a Save* call throws after some categories have already been deleted
        // (e.g. providers cleared, then an agent-profile deserialization failure), the store is
        // left in a partially-cleared state with no rollback. The "config unchanged" assertions in
        // the bad-file tests only check QualityGateConfigs count — provider, profile, and reviewer
        // counts could be zeroed if a later step throws after they were deleted.

        // Clear existing provider configs for all kinds and replace with imported ones
        foreach (var kind in Enum.GetValues<ProviderKind>())
        {
            var existing = await _store.LoadProviderConfigsAsync(kind, ct);
            foreach (var p in existing)
                await _store.DeleteProviderConfigAsync(p.Id, kind, ct);
        }

        if (bundle.ProviderConfigs is not null)
        {
            foreach (var dto in bundle.ProviderConfigs)
            {
                if (dto.Configuration is null) continue;
                var pc = JsonSerializer.Deserialize<ProviderConfig>(dto.Configuration, PipelineJsonOptions.Default);
                if (pc is not null)
                    await _store.SaveProviderConfigAsync(pc, ct);
            }
        }

        // Agent profiles
        var existingProfiles = await _store.LoadAgentProfilesAsync(ct);
        foreach (var p in existingProfiles)
            await _store.DeleteAgentProfileAsync(p.Id, ct);

        if (bundle.AgentProfiles is not null)
        {
            foreach (var dto in bundle.AgentProfiles)
            {
                if (dto.Configuration is null) continue;
                var profile = JsonSerializer.Deserialize<AgentProfile>(dto.Configuration, PipelineJsonOptions.Default);
                if (profile is not null)
                    await _store.SaveAgentProfileAsync(profile, ct);
            }
        }

        // Quality gate configs
        var existingQg = await _store.LoadQualityGateConfigsAsync(ct);
        foreach (var q in existingQg)
            await _store.DeleteQualityGateConfigAsync(q.Id, ct);

        if (bundle.QualityGateConfigs is not null)
        {
            foreach (var dto in bundle.QualityGateConfigs)
            {
                if (dto.Configuration is null) continue;
                var qg = JsonSerializer.Deserialize<QualityGateConfiguration>(dto.Configuration, PipelineJsonOptions.Default);
                if (qg is not null)
                    await _store.SaveQualityGateConfigAsync(qg, ct);
            }
        }

        // Reviewer configs
        var existingReviewers = await _store.LoadReviewerConfigsAsync(ct);
        foreach (var r in existingReviewers)
            await _store.DeleteReviewerConfigAsync(r.Id, ct);

        if (bundle.ReviewerConfigs is not null)
        {
            foreach (var dto in bundle.ReviewerConfigs)
            {
                if (dto.Configuration is null) continue;
                var reviewer = JsonSerializer.Deserialize<ReviewerConfiguration>(dto.Configuration, PipelineJsonOptions.Default);
                if (reviewer is not null)
                    await _store.SaveReviewerConfigAsync(reviewer, ct);
            }
        }

        // Pipeline config
        if (bundle.PipelineConfig is not null)
        {
            var pipelineConfig = JsonSerializer.Deserialize<PipelineConfiguration>(
                bundle.PipelineConfig, PipelineJsonOptions.Default);
            if (pipelineConfig is not null)
                await _store.SavePipelineConfigAsync(pipelineConfig, ct);
        }

        // Projects — clear existing and re-import from bundle
        var existingProjects = await _store.LoadProjectsAsync(ct);
        foreach (var p in existingProjects)
            await _store.DeleteProjectAsync(p.Id, ct);

        if (bundle.Projects is not null)
        {
            foreach (var dto in bundle.Projects)
            {
                // TODO [WARNING]: TemplateIds is always initialised as empty, discarding the
                // project↔template membership from the bundle. Any store query against
                // LoadTemplatesForProjectAsync(nonDefaultProjectId) after import will return empty,
                // diverging from the real API behaviour. Restore template membership here, or track
                // it via the owning project ID stored in JobTemplateExportDto.
                await _store.SaveProjectAsync(new PipelineProject
                {
                    Id = dto.Id.ToString(),
                    Name = dto.Name,
                    Enabled = dto.Enabled,
                    Description = dto.Description,
                    TemplateIds = new List<string>()
                }, ct);
            }
        }

        // Job templates — clear existing and re-import from bundle
        var existingTemplates = await _store.LoadAllTemplatesAsync(ct);
        foreach (var t in existingTemplates)
        {
            // TODO [WARNING]: existingProjects was captured before the project-delete loop ran.
            // DeleteProjectAsync reassigns the deleted project's templates to DefaultProjectId in
            // _templateProjects, so owningProjectId derived from the original snapshot may no longer
            // reflect the live store state. This is harmless for now because DeleteTemplateAsync
            // removes by template ID regardless of project ID, but is misleading for future readers.
            // Capture existingProjects before any mutations and document that it's a pre-delete snapshot.
            var owningProject = existingProjects.FirstOrDefault(p => p.TemplateIds?.Contains(t.Id) == true);
            var owningProjectId = owningProject?.Id ?? WellKnownIds.DefaultProjectId;
            await _store.DeleteTemplateAsync(owningProjectId, new TemplateId(t.Id), ct);
        }

        if (bundle.JobTemplates is not null)
        {
            foreach (var dto in bundle.JobTemplates)
            {
                if (dto.Configuration is null) continue;
                // TODO [WARNING]: All imported templates are unconditionally assigned to
                // WellKnownIds.DefaultProjectId, discarding their original owning project. A bundle
                // exported from a store with templates under non-default projects will not restore
                // those project associations after a round-trip. JobTemplateExportDto carries no
                // project ID; add one to the DTO to make the association round-trip correctly.
                var template = JsonSerializer.Deserialize<PipelineJobTemplate>(dto.Configuration, PipelineJsonOptions.Default);
                if (template is not null)
                    await _store.SaveTemplateAsync(WellKnownIds.DefaultProjectId, template, ct);
            }
        }
    }

    // ── JSON options for export/import ──────────────────────────────────
    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private static readonly JsonSerializerOptions ImportJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    // ── Local DTOs matching the API's ConfigBundle shape ──────────────────

    private sealed class ExportBundle
    {
        public string? PipelineConfig { get; set; }
        public List<ProviderConfigExportDto>? ProviderConfigs { get; set; }
        public List<NamedConfigExportDto>? AgentProfiles { get; set; }
        public List<NamedConfigExportDto>? QualityGateConfigs { get; set; }
        public List<NamedConfigExportDto>? ReviewerConfigs { get; set; }
        public List<ProjectExportDto>? Projects { get; set; }
        public List<JobTemplateExportDto>? JobTemplates { get; set; }
    }

    private sealed record ProviderConfigExportDto
    {
        public Guid Id { get; init; }
        public ProviderKind Kind { get; init; }
        public string DisplayName { get; init; } = "";
        public string ProviderType { get; init; } = "";
        public bool Enabled { get; init; }
        public string? Configuration { get; init; }
    }

    private sealed class NamedConfigExportDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string? Configuration { get; set; }
    }

    private sealed class ProjectExportDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public bool Enabled { get; set; }
        public string? Description { get; set; }
    }

    private sealed class JobTemplateExportDto
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "";
        public string? Configuration { get; set; }
    }

    // ── Models ───────────────────────────────────────────────────────────
    public Task<(IReadOnlyList<AgentModelInfo> Models, string? Error)> GetModelsAsync(CancellationToken ct = default)
        => Task.FromResult<(IReadOnlyList<AgentModelInfo>, string?)>((Array.Empty<AgentModelInfo>(), null));

    /// <summary>Clears key-value state between tests. The backing store resets itself.</summary>
    public void Reset() => _keyValues.Clear();
}
