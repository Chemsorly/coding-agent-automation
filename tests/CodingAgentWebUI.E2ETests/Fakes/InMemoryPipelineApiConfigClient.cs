using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.E2ETests.Fakes;

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

    public Task MoveTemplateAsync(string sourceProjectId, string targetProjectId, string templateId, CancellationToken ct = default)
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
    // Not modelled: the bundle format is the API's own DB projection, not something the
    // in-memory store can produce. No E2E test exercises these; a test that needs them should
    // drive the real endpoint against a real API host instead of extending this fake.
    public Task<byte[]> ExportConfigAsync(CancellationToken ct = default)
        => Task.FromResult(Array.Empty<byte>());

    public Task ImportConfigAsync(Stream jsonStream, string fileName, CancellationToken ct = default)
        => Task.CompletedTask;

    // ── Models ───────────────────────────────────────────────────────────
    public Task<(IReadOnlyList<AgentModelInfo> Models, string? Error)> GetModelsAsync(CancellationToken ct = default)
        => Task.FromResult<(IReadOnlyList<AgentModelInfo>, string?)>((Array.Empty<AgentModelInfo>(), null));

    /// <summary>Clears key-value state between tests. The backing store resets itself.</summary>
    public void Reset() => _keyValues.Clear();
}
