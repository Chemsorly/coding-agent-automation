using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Services;

/// <summary>
/// API-backed implementation of <see cref="IPipelineConfigStore"/> using <see cref="IPipelineApiConfigClient"/>.
/// Implements TTL caching (configurable via <see cref="CacheTtlSeconds"/>) to avoid excessive API
/// calls during the tight polling loop. (Spec 045 Req 4.2 Option B, Req 4.3)
/// Thread-safe: the lock is released before awaiting to avoid holding a lock across async I/O.
/// Two concurrent callers may both reach the API (double-fetch window) — acceptable trade-off.
/// </summary>
public sealed class ApiPipelineConfigStore : IPipelineConfigStore
{
    private readonly IPipelineApiConfigClient _client;
    private readonly Lock _cacheLock = new();
    private PipelineConfiguration? _cached;
    private DateTime _cacheExpiry = DateTime.MinValue;
    public int CacheTtlSeconds { get; set; } = 60;

    public ApiPipelineConfigStore(IPipelineApiConfigClient client) => _client = client;

    public async Task<PipelineConfiguration> LoadPipelineConfigAsync(CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (_cached is not null && DateTime.UtcNow <= _cacheExpiry)
                return _cached;
        }
        var fresh = await _client.GetPipelineConfigAsync(ct);
        lock (_cacheLock)
        {
            _cached = fresh;
            _cacheExpiry = DateTime.UtcNow.AddSeconds(CacheTtlSeconds);
        }
        return fresh;
    }

    public async Task SavePipelineConfigAsync(PipelineConfiguration config, CancellationToken ct)
    {
        await _client.SavePipelineConfigAsync(config, ct);
        lock (_cacheLock)
        {
            _cached = config;
            _cacheExpiry = DateTime.UtcNow.AddSeconds(CacheTtlSeconds);
        }
    }

    public async Task UpdatePipelineConfigAsync(Func<PipelineConfiguration, PipelineConfiguration> transform, CancellationToken ct)
    {
        await _client.UpdatePipelineConfigAsync(transform, ct);
        lock (_cacheLock)
        {
            _cached = null; // invalidate cache so next read fetches fresh
        }
    }
}

/// <summary>
/// API-backed implementation of <see cref="IProviderConfigStore"/> using <see cref="IPipelineApiConfigClient"/>.
/// Implements TTL caching to avoid excessive API calls. (Spec 045 Req 4.2 Option B, Req 4.3)
/// Thread-safe: the lock is released before awaiting to avoid holding a lock across async I/O.
/// </summary>
public sealed class ApiProviderConfigStore : IProviderConfigStore
{
    private readonly IPipelineApiConfigClient _client;
    private readonly Lock _cacheLock = new();
    private readonly ProviderConfigCache _providerCache = new();
    public int CacheTtlSeconds { get; set; } = 60;

    public ApiProviderConfigStore(IPipelineApiConfigClient client) => _client = client;

    public Task<IReadOnlyList<ProviderConfig>> LoadProviderConfigsAsync(ProviderKind kind, CancellationToken ct)
        => _providerCache.GetOrFetchAsync(_cacheLock, kind, CacheTtlSeconds, _client, ct);

    public async Task<ProviderConfig?> GetProviderConfigByIdAsync(string id, ProviderKind kind, CancellationToken ct)
    {
        var all = await LoadProviderConfigsAsync(kind, ct);
        return all.FirstOrDefault(p => p.Id == id);
    }

    public async Task SaveProviderConfigAsync(ProviderConfig config, CancellationToken ct)
    {
        await _client.SaveProviderConfigAsync(config, ct);
        lock (_cacheLock) _providerCache.Clear();
    }

    public async Task DeleteProviderConfigAsync(string id, ProviderKind kind, CancellationToken ct)
    {
        await _client.DeleteProviderConfigAsync(id, kind, ct);
        lock (_cacheLock) _providerCache.Clear();
    }
}

/// <summary>
/// TTL cache for provider configs keyed by <see cref="ProviderKind"/>.
///
/// Keyed per kind deliberately: <see cref="ProviderKind"/> has five members
/// (Issue, Repository, Agent, Pipeline, Brain). An earlier two-slot design bucketed every
/// non-Issue kind together, so a Repository load followed by an Agent load within the TTL
/// returned the Repository list for Agent — which silently mis-resolves dispatch, since
/// <c>DispatchInfrastructure</c> loads Repository, Agent and Pipeline back to back.
///
/// Callers hold the shared cache lock around <see cref="TryGet"/> / <see cref="Set"/> /
/// <see cref="Clear"/>; the lock is released across the awaited API call.
/// </summary>
internal sealed class ProviderConfigCache
{
    private readonly Dictionary<ProviderKind, (IReadOnlyList<ProviderConfig> Configs, DateTime Expiry)> _byKind = [];

    public bool TryGet(ProviderKind kind, out IReadOnlyList<ProviderConfig> configs)
    {
        if (_byKind.TryGetValue(kind, out var entry) && DateTime.UtcNow <= entry.Expiry)
        {
            configs = entry.Configs;
            return true;
        }
        configs = [];
        return false;
    }

    public void Set(ProviderKind kind, IReadOnlyList<ProviderConfig> configs, int ttlSeconds)
        => _byKind[kind] = (configs, DateTime.UtcNow.AddSeconds(ttlSeconds));

    public void Clear() => _byKind.Clear();

    /// <summary>
    /// Returns the cached list for <paramref name="kind"/>, or fetches it from the API and
    /// caches it under that kind. The lock is not held across the API call.
    /// </summary>
    public async Task<IReadOnlyList<ProviderConfig>> GetOrFetchAsync(
        Lock cacheLock,
        ProviderKind kind,
        int ttlSeconds,
        IPipelineApiConfigClient client,
        CancellationToken ct)
    {
        lock (cacheLock)
        {
            if (TryGet(kind, out var cached))
                return cached;
        }

        // WithSecrets: these adapters back IConfigurationStore / IProviderConfigStore, which the
        // dispatch path reads to build the job payload an agent executes with. The redacted form
        // would ship "****" as every repository token, agent key and base URL. Components that
        // render configs in the UI call IPipelineApiConfigClient directly and get the safe form.
        var fresh = await client.GetProviderConfigsWithSecretsAsync(kind, ct);

        lock (cacheLock)
        {
            Set(kind, fresh, ttlSeconds);
        }
        return fresh;
    }
}

/// <summary>
/// API-backed implementation of <see cref="IProjectStore"/> using <see cref="IPipelineApiConfigClient"/>.
/// Implements TTL caching to avoid excessive API calls. (Spec 045 Req 4.2 Option B, Req 4.3)
/// Thread-safe: the lock is released before awaiting to avoid holding a lock across async I/O.
/// </summary>
public sealed class ApiProjectStore : IProjectStore
{
    private readonly IPipelineApiConfigClient _client;
    private readonly Lock _cacheLock = new();
    private IReadOnlyList<PipelineProject>? _cachedProjects;
    private DateTime _projectsExpiry = DateTime.MinValue;
    private IReadOnlyList<PipelineJobTemplate>? _cachedTemplates;
    private DateTime _templatesExpiry = DateTime.MinValue;
    public int CacheTtlSeconds { get; set; } = 60;

    public ApiProjectStore(IPipelineApiConfigClient client) => _client = client;

    public async Task<IReadOnlyList<PipelineProject>> LoadProjectsAsync(CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (_cachedProjects is not null && DateTime.UtcNow <= _projectsExpiry)
                return _cachedProjects;
        }
        var fresh = await _client.GetProjectsAsync(ct);
        lock (_cacheLock)
        {
            _cachedProjects = fresh;
            _projectsExpiry = DateTime.UtcNow.AddSeconds(CacheTtlSeconds);
        }
        return fresh;
    }

    public async Task<PipelineProject?> GetProjectByIdAsync(string id, CancellationToken ct)
        => await _client.GetProjectByIdAsync(id, ct);

    public async Task SaveProjectAsync(PipelineProject project, CancellationToken ct)
    {
        await _client.SaveProjectAsync(project, ct);
        lock (_cacheLock)
        {
            _cachedProjects = null;
        }
    }

    public async Task DeleteProjectAsync(string id, CancellationToken ct)
    {
        await _client.DeleteProjectAsync(id, ct);
        lock (_cacheLock)
        {
            _cachedProjects = null;
        }
    }

    public async Task<IReadOnlyList<PipelineJobTemplate>> LoadTemplatesForProjectAsync(string projectId, CancellationToken ct)
        => await _client.GetTemplatesForProjectAsync(projectId, ct);

    public async Task<IReadOnlyList<PipelineJobTemplate>> LoadAllTemplatesAsync(CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (_cachedTemplates is not null && DateTime.UtcNow <= _templatesExpiry)
                return _cachedTemplates;
        }
        var fresh = await _client.GetAllTemplatesAsync(ct);
        lock (_cacheLock)
        {
            _cachedTemplates = fresh;
            _templatesExpiry = DateTime.UtcNow.AddSeconds(CacheTtlSeconds);
        }
        return fresh;
    }

    public async Task SaveTemplateAsync(string projectId, PipelineJobTemplate template, CancellationToken ct)
    {
        await _client.SaveTemplateAsync(projectId, template, ct);
        lock (_cacheLock)
        {
            _cachedTemplates = null;
        }
    }

    public async Task DeleteTemplateAsync(string projectId, TemplateId templateId, CancellationToken ct)
    {
        await _client.DeleteTemplateAsync(projectId, templateId.ToString(), ct);
        lock (_cacheLock)
        {
            _cachedTemplates = null;
        }
    }

    public async Task MoveTemplateAsync(string sourceProjectId, string targetProjectId, TemplateId templateId, CancellationToken ct)
    {
        await _client.MoveTemplateAsync(sourceProjectId, targetProjectId, templateId.ToString(), ct);
        lock (_cacheLock)
        {
            _cachedTemplates = null;
            _cachedProjects = null;
        }
    }

    public async Task<bool> HasEnabledTemplatesAsync(CancellationToken ct)
        => await _client.HasEnabledTemplatesAsync(ct);
}

/// <summary>
/// API-backed implementation of the composite <see cref="IConfigurationStore"/> interface.
/// Delegates all operations to <see cref="IPipelineApiConfigClient"/> with the same TTL
/// caching as the individual adapters. Registered so that services requiring
/// <see cref="IConfigurationStore"/> (e.g. <see cref="LabelService"/>,
/// <see cref="DispatchOrchestrationService"/>) resolve correctly after the
/// Postgres-backed PostgresConfigurationStore was removed in Spec 045 Task 8.
/// Thread-safe: the lock is released before awaiting to avoid holding a lock across async I/O.
/// </summary>
public sealed class ApiConfigurationStore : IConfigurationStore
{
    private readonly IPipelineApiConfigClient _client;
    private readonly Lock _cacheLock = new();
    public int CacheTtlSeconds { get; set; } = 60;

    // ── cached state ─────────────────────────────────────────────────────
    private PipelineConfiguration? _cachedPipeline;
    private DateTime _pipelineExpiry = DateTime.MinValue;

    // Keyed per ProviderKind — see ProviderConfigCache for why a two-slot cache is wrong.
    private readonly ProviderConfigCache _providerCache = new();

    private IReadOnlyList<AgentProfile>? _cachedProfiles;
    private DateTime _profilesExpiry = DateTime.MinValue;

    private IReadOnlyList<QualityGateConfiguration>? _cachedQG;
    private DateTime _qgExpiry = DateTime.MinValue;

    private IReadOnlyList<ReviewerConfiguration>? _cachedReviewers;
    private DateTime _reviewersExpiry = DateTime.MinValue;

    private IReadOnlyList<PipelineProject>? _cachedProjects;
    private DateTime _projectsExpiry = DateTime.MinValue;

    private IReadOnlyList<PipelineJobTemplate>? _cachedTemplates;
    private DateTime _templatesExpiry = DateTime.MinValue;

    public ApiConfigurationStore(IPipelineApiConfigClient client) => _client = client;

    public void InvalidateCaches()
    {
        lock (_cacheLock)
        {
            _cachedPipeline = null; _pipelineExpiry = DateTime.MinValue;
            _providerCache.Clear();
            _cachedProfiles = null; _profilesExpiry = DateTime.MinValue;
            _cachedQG = null; _qgExpiry = DateTime.MinValue;
            _cachedReviewers = null; _reviewersExpiry = DateTime.MinValue;
            _cachedProjects = null; _projectsExpiry = DateTime.MinValue;
            _cachedTemplates = null; _templatesExpiry = DateTime.MinValue;
        }
    }

    // ── IPipelineConfigStore ─────────────────────────────────────────────
    public async Task<PipelineConfiguration> LoadPipelineConfigAsync(CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (_cachedPipeline is not null && DateTime.UtcNow <= _pipelineExpiry)
                return _cachedPipeline;
        }
        var fresh = await _client.GetPipelineConfigAsync(ct);
        lock (_cacheLock)
        {
            _cachedPipeline = fresh;
            _pipelineExpiry = DateTime.UtcNow.AddSeconds(CacheTtlSeconds);
        }
        return fresh;
    }

    public async Task SavePipelineConfigAsync(PipelineConfiguration config, CancellationToken ct)
    {
        await _client.SavePipelineConfigAsync(config, ct);
        lock (_cacheLock)
        {
            _cachedPipeline = config;
            _pipelineExpiry = DateTime.UtcNow.AddSeconds(CacheTtlSeconds);
        }
    }

    public async Task UpdatePipelineConfigAsync(Func<PipelineConfiguration, PipelineConfiguration> transform, CancellationToken ct)
    {
        await _client.UpdatePipelineConfigAsync(transform, ct);
        lock (_cacheLock)
        {
            _cachedPipeline = null;
        }
    }

    // ── IProviderConfigStore ─────────────────────────────────────────────
    public Task<IReadOnlyList<ProviderConfig>> LoadProviderConfigsAsync(ProviderKind kind, CancellationToken ct)
        => _providerCache.GetOrFetchAsync(_cacheLock, kind, CacheTtlSeconds, _client, ct);

    public async Task<ProviderConfig?> GetProviderConfigByIdAsync(string id, ProviderKind kind, CancellationToken ct)
    {
        var all = await LoadProviderConfigsAsync(kind, ct);
        return all.FirstOrDefault(p => p.Id == id);
    }

    public async Task SaveProviderConfigAsync(ProviderConfig config, CancellationToken ct)
    {
        await _client.SaveProviderConfigAsync(config, ct);
        lock (_cacheLock) _providerCache.Clear();
    }

    public async Task DeleteProviderConfigAsync(string id, ProviderKind kind, CancellationToken ct)
    {
        await _client.DeleteProviderConfigAsync(id, kind, ct);
        lock (_cacheLock) _providerCache.Clear();
    }

    // ── IAgentProfileStore ───────────────────────────────────────────────
    public async Task<IReadOnlyList<AgentProfile>> LoadAgentProfilesAsync(CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (_cachedProfiles is not null && DateTime.UtcNow <= _profilesExpiry)
                return _cachedProfiles;
        }
        var fresh = await _client.GetAgentProfilesAsync(ct);
        lock (_cacheLock)
        {
            _cachedProfiles = fresh;
            _profilesExpiry = DateTime.UtcNow.AddSeconds(CacheTtlSeconds);
        }
        return fresh;
    }

    public async Task SaveAgentProfileAsync(AgentProfile profile, CancellationToken ct)
    {
        await _client.SaveAgentProfileAsync(profile, ct);
        lock (_cacheLock)
        {
            _cachedProfiles = null;
        }
    }

    public async Task DeleteAgentProfileAsync(string id, CancellationToken ct)
    {
        await _client.DeleteAgentProfileAsync(id, ct);
        lock (_cacheLock)
        {
            _cachedProfiles = null;
        }
    }

    // ── IQualityGateConfigStore ──────────────────────────────────────────
    public async Task<IReadOnlyList<QualityGateConfiguration>> LoadQualityGateConfigsAsync(CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (_cachedQG is not null && DateTime.UtcNow <= _qgExpiry)
                return _cachedQG;
        }
        var fresh = await _client.GetQualityGateConfigsAsync(ct);
        lock (_cacheLock)
        {
            _cachedQG = fresh;
            _qgExpiry = DateTime.UtcNow.AddSeconds(CacheTtlSeconds);
        }
        return fresh;
    }

    public async Task SaveQualityGateConfigAsync(QualityGateConfiguration config, CancellationToken ct)
    {
        await _client.SaveQualityGateConfigAsync(config, ct);
        lock (_cacheLock)
        {
            _cachedQG = null;
        }
    }

    public async Task DeleteQualityGateConfigAsync(string id, CancellationToken ct)
    {
        await _client.DeleteQualityGateConfigAsync(id, ct);
        lock (_cacheLock)
        {
            _cachedQG = null;
        }
    }

    // ── IReviewerConfigStore ─────────────────────────────────────────────
    public async Task<IReadOnlyList<ReviewerConfiguration>> LoadReviewerConfigsAsync(CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (_cachedReviewers is not null && DateTime.UtcNow <= _reviewersExpiry)
                return _cachedReviewers;
        }
        var fresh = await _client.GetReviewerConfigsAsync(ct);
        lock (_cacheLock)
        {
            _cachedReviewers = fresh;
            _reviewersExpiry = DateTime.UtcNow.AddSeconds(CacheTtlSeconds);
        }
        return fresh;
    }

    public async Task SaveReviewerConfigAsync(ReviewerConfiguration config, CancellationToken ct)
    {
        await _client.SaveReviewerConfigAsync(config, ct);
        lock (_cacheLock)
        {
            _cachedReviewers = null;
        }
    }

    public async Task DeleteReviewerConfigAsync(string id, CancellationToken ct)
    {
        await _client.DeleteReviewerConfigAsync(id, ct);
        lock (_cacheLock)
        {
            _cachedReviewers = null;
        }
    }

    public async Task ResetReviewerConfigsToDefaultAsync(CancellationToken ct)
    {
        await _client.ResetReviewerConfigsToDefaultAsync(ct);
        lock (_cacheLock)
        {
            _cachedReviewers = null;
        }
    }

    // ── IProjectStore ────────────────────────────────────────────────────
    public async Task<IReadOnlyList<PipelineProject>> LoadProjectsAsync(CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (_cachedProjects is not null && DateTime.UtcNow <= _projectsExpiry)
                return _cachedProjects;
        }
        var fresh = await _client.GetProjectsAsync(ct);
        lock (_cacheLock)
        {
            _cachedProjects = fresh;
            _projectsExpiry = DateTime.UtcNow.AddSeconds(CacheTtlSeconds);
        }
        return fresh;
    }

    public async Task<PipelineProject?> GetProjectByIdAsync(string id, CancellationToken ct)
        => await _client.GetProjectByIdAsync(id, ct);

    public async Task SaveProjectAsync(PipelineProject project, CancellationToken ct)
    {
        await _client.SaveProjectAsync(project, ct);
        lock (_cacheLock)
        {
            _cachedProjects = null;
        }
    }

    public async Task DeleteProjectAsync(string id, CancellationToken ct)
    {
        await _client.DeleteProjectAsync(id, ct);
        lock (_cacheLock)
        {
            _cachedProjects = null;
        }
    }

    public async Task<IReadOnlyList<PipelineJobTemplate>> LoadTemplatesForProjectAsync(string projectId, CancellationToken ct)
        => await _client.GetTemplatesForProjectAsync(projectId, ct);

    public async Task<IReadOnlyList<PipelineJobTemplate>> LoadAllTemplatesAsync(CancellationToken ct)
    {
        lock (_cacheLock)
        {
            if (_cachedTemplates is not null && DateTime.UtcNow <= _templatesExpiry)
                return _cachedTemplates;
        }
        var fresh = await _client.GetAllTemplatesAsync(ct);
        lock (_cacheLock)
        {
            _cachedTemplates = fresh;
            _templatesExpiry = DateTime.UtcNow.AddSeconds(CacheTtlSeconds);
        }
        return fresh;
    }

    public async Task SaveTemplateAsync(string projectId, PipelineJobTemplate template, CancellationToken ct)
    {
        await _client.SaveTemplateAsync(projectId, template, ct);
        lock (_cacheLock)
        {
            _cachedTemplates = null;
        }
    }

    public async Task DeleteTemplateAsync(string projectId, TemplateId templateId, CancellationToken ct)
    {
        await _client.DeleteTemplateAsync(projectId, templateId.ToString(), ct);
        lock (_cacheLock)
        {
            _cachedTemplates = null;
        }
    }

    public async Task MoveTemplateAsync(string sourceProjectId, string targetProjectId, TemplateId templateId, CancellationToken ct)
    {
        await _client.MoveTemplateAsync(sourceProjectId, targetProjectId, templateId.ToString(), ct);
        lock (_cacheLock)
        {
            _cachedTemplates = null;
            _cachedProjects = null;
        }
    }

    public async Task<bool> HasEnabledTemplatesAsync(CancellationToken ct)
        => await _client.HasEnabledTemplatesAsync(ct);
}
