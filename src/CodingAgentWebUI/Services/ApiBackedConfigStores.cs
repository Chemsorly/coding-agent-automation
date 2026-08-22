using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Services;

/// <summary>
/// API-backed implementation of <see cref="IPipelineConfigStore"/> using <see cref="IPipelineApiConfigClient"/>.
/// Implements TTL caching (configurable via <see cref="CacheTtlSeconds"/>) to avoid excessive API
/// calls during the tight polling loop.
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

    /// <summary>Drops the cached configuration so the next load goes to the API.</summary>
    public void InvalidateCaches()
    {
        lock (_cacheLock)
        {
            _cached = null;
            _cacheExpiry = DateTime.MinValue;
        }
    }

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
/// Implements TTL caching to avoid excessive API calls.
/// Thread-safe: the lock is released before awaiting to avoid holding a lock across async I/O.
/// </summary>
public sealed class ApiProviderConfigStore : IProviderConfigStore
{
    private readonly IPipelineApiConfigClient _client;
    private readonly Lock _cacheLock = new();
    private readonly ProviderConfigCache _providerCache = new();
    public int CacheTtlSeconds { get; set; } = 60;

    public ApiProviderConfigStore(IPipelineApiConfigClient client) => _client = client;

    /// <summary>Drops every cached provider kind so the next load goes to the API.</summary>
    public void InvalidateCaches()
    {
        lock (_cacheLock) _providerCache.Clear();
    }

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
/// Implements TTL caching to avoid excessive API calls.
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

    /// <summary>Drops cached projects and templates so the next load goes to the API.</summary>
    public void InvalidateCaches()
    {
        lock (_cacheLock)
        {
            _cachedProjects = null;
            _projectsExpiry = DateTime.MinValue;
            _cachedTemplates = null;
            _templatesExpiry = DateTime.MinValue;
        }
    }

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
/// API-backed implementation of the composite <see cref="IConfigurationStore"/> interface, for
/// services that want the whole configuration surface through one dependency (<c>LabelService</c>,
/// <c>DispatchOrchestrationService</c>).
///
/// It composes the narrow stores rather than reimplementing them. The earlier version duplicated
/// every pipeline-config, provider-config, project and template method verbatim, differing only in
/// the names of its cache fields — and because DI registers all four as separate singletons over
/// the same client, that meant two independent caches of the same data. A save through
/// <see cref="IProviderConfigStore"/> left this store serving the pre-save list until its own TTL
/// lapsed. Delegating gives one cache per concern and makes an invalidation here reach everyone.
///
/// Agent profiles, quality gates and reviewers have no narrow store, so they are cached here.
/// Thread-safe: the lock is released before awaiting, never held across async I/O.
/// </summary>
public sealed class ApiConfigurationStore : IConfigurationStore
{
    private readonly IPipelineApiConfigClient _client;
    private readonly ApiPipelineConfigStore _pipeline;
    private readonly ApiProviderConfigStore _providers;
    private readonly ApiProjectStore _projects;

    private readonly Lock _cacheLock = new();
    public int CacheTtlSeconds { get; set; } = 60;

    // Only the three concerns with no narrow store of their own.
    private readonly TtlCache<IReadOnlyList<AgentProfile>> _profiles = new();
    private readonly TtlCache<IReadOnlyList<QualityGateConfiguration>> _qualityGates = new();
    private readonly TtlCache<IReadOnlyList<ReviewerConfiguration>> _reviewers = new();

    public ApiConfigurationStore(
        IPipelineApiConfigClient client,
        ApiPipelineConfigStore pipeline,
        ApiProviderConfigStore providers,
        ApiProjectStore projects)
    {
        _client = client;
        _pipeline = pipeline;
        _providers = providers;
        _projects = projects;
    }

    /// <summary>Drops every cached value, here and in the composed stores.</summary>
    public void InvalidateCaches()
    {
        lock (_cacheLock)
        {
            _profiles.Clear();
            _qualityGates.Clear();
            _reviewers.Clear();
        }

        _pipeline.InvalidateCaches();
        _providers.InvalidateCaches();
        _projects.InvalidateCaches();
    }

    // ── IPipelineConfigStore ─────────────────────────────────────────────
    public Task<PipelineConfiguration> LoadPipelineConfigAsync(CancellationToken ct)
        => _pipeline.LoadPipelineConfigAsync(ct);

    public Task SavePipelineConfigAsync(PipelineConfiguration config, CancellationToken ct)
        => _pipeline.SavePipelineConfigAsync(config, ct);

    public Task UpdatePipelineConfigAsync(Func<PipelineConfiguration, PipelineConfiguration> transform, CancellationToken ct)
        => _pipeline.UpdatePipelineConfigAsync(transform, ct);

    // ── IProviderConfigStore ─────────────────────────────────────────────
    public Task<IReadOnlyList<ProviderConfig>> LoadProviderConfigsAsync(ProviderKind kind, CancellationToken ct)
        => _providers.LoadProviderConfigsAsync(kind, ct);

    public Task<ProviderConfig?> GetProviderConfigByIdAsync(string id, ProviderKind kind, CancellationToken ct)
        => _providers.GetProviderConfigByIdAsync(id, kind, ct);

    public Task SaveProviderConfigAsync(ProviderConfig config, CancellationToken ct)
        => _providers.SaveProviderConfigAsync(config, ct);

    public Task DeleteProviderConfigAsync(string id, ProviderKind kind, CancellationToken ct)
        => _providers.DeleteProviderConfigAsync(id, kind, ct);

    // ── IAgentProfileStore ───────────────────────────────────────────────
    public Task<IReadOnlyList<AgentProfile>> LoadAgentProfilesAsync(CancellationToken ct)
        => LoadCachedAsync(_profiles, _client.GetAgentProfilesAsync, ct);

    public Task SaveAgentProfileAsync(AgentProfile profile, CancellationToken ct)
        => WriteThenInvalidateAsync(_client.SaveAgentProfileAsync(profile, ct), _profiles);

    public Task DeleteAgentProfileAsync(string id, CancellationToken ct)
        => WriteThenInvalidateAsync(_client.DeleteAgentProfileAsync(id, ct), _profiles);

    // ── IQualityGateConfigStore ──────────────────────────────────────────
    public Task<IReadOnlyList<QualityGateConfiguration>> LoadQualityGateConfigsAsync(CancellationToken ct)
        => LoadCachedAsync(_qualityGates, _client.GetQualityGateConfigsAsync, ct);

    public Task SaveQualityGateConfigAsync(QualityGateConfiguration config, CancellationToken ct)
        => WriteThenInvalidateAsync(_client.SaveQualityGateConfigAsync(config, ct), _qualityGates);

    public Task DeleteQualityGateConfigAsync(string id, CancellationToken ct)
        => WriteThenInvalidateAsync(_client.DeleteQualityGateConfigAsync(id, ct), _qualityGates);

    // ── IReviewerConfigStore ─────────────────────────────────────────────
    public Task<IReadOnlyList<ReviewerConfiguration>> LoadReviewerConfigsAsync(CancellationToken ct)
        => LoadCachedAsync(_reviewers, _client.GetReviewerConfigsAsync, ct);

    public Task SaveReviewerConfigAsync(ReviewerConfiguration config, CancellationToken ct)
        => WriteThenInvalidateAsync(_client.SaveReviewerConfigAsync(config, ct), _reviewers);

    public Task DeleteReviewerConfigAsync(string id, CancellationToken ct)
        => WriteThenInvalidateAsync(_client.DeleteReviewerConfigAsync(id, ct), _reviewers);

    public Task ResetReviewerConfigsToDefaultAsync(CancellationToken ct)
        => WriteThenInvalidateAsync(_client.ResetReviewerConfigsToDefaultAsync(ct), _reviewers);

    // ── IProjectStore ────────────────────────────────────────────────────
    public Task<IReadOnlyList<PipelineProject>> LoadProjectsAsync(CancellationToken ct)
        => _projects.LoadProjectsAsync(ct);

    public Task<PipelineProject?> GetProjectByIdAsync(string id, CancellationToken ct)
        => _projects.GetProjectByIdAsync(id, ct);

    public Task SaveProjectAsync(PipelineProject project, CancellationToken ct)
        => _projects.SaveProjectAsync(project, ct);

    public Task DeleteProjectAsync(string id, CancellationToken ct)
        => _projects.DeleteProjectAsync(id, ct);

    public Task<IReadOnlyList<PipelineJobTemplate>> LoadTemplatesForProjectAsync(string projectId, CancellationToken ct)
        => _projects.LoadTemplatesForProjectAsync(projectId, ct);

    public Task<IReadOnlyList<PipelineJobTemplate>> LoadAllTemplatesAsync(CancellationToken ct)
        => _projects.LoadAllTemplatesAsync(ct);

    public Task SaveTemplateAsync(string projectId, PipelineJobTemplate template, CancellationToken ct)
        => _projects.SaveTemplateAsync(projectId, template, ct);

    public Task DeleteTemplateAsync(string projectId, TemplateId templateId, CancellationToken ct)
        => _projects.DeleteTemplateAsync(projectId, templateId, ct);

    public Task MoveTemplateAsync(string sourceProjectId, string targetProjectId, TemplateId templateId, CancellationToken ct)
        => _projects.MoveTemplateAsync(sourceProjectId, targetProjectId, templateId, ct);

    public Task<bool> HasEnabledTemplatesAsync(CancellationToken ct)
        => _projects.HasEnabledTemplatesAsync(ct);

    // ── Cache plumbing ───────────────────────────────────────────────────

    private async Task<T> LoadCachedAsync<T>(
        TtlCache<T> cache,
        Func<CancellationToken, Task<T>> fetch,
        CancellationToken ct) where T : class
    {
        lock (_cacheLock)
        {
            if (cache.TryGet(out var hit)) return hit!;
        }

        // Deliberately outside the lock: two concurrent callers may both fetch. That double-fetch
        // window is cheaper than holding a lock across network I/O.
        var fresh = await fetch(ct);

        lock (_cacheLock) cache.Set(fresh, CacheTtlSeconds);
        return fresh;
    }

    private async Task WriteThenInvalidateAsync<T>(Task write, TtlCache<T> cache) where T : class
    {
        await write;
        lock (_cacheLock) cache.Clear();
    }

    /// <summary>
    /// A single TTL-cached value. Callers hold the shared lock around every member; the lock is
    /// released across the awaited fetch.
    /// </summary>
    private sealed class TtlCache<T> where T : class
    {
        private T? _value;
        private DateTime _expiry = DateTime.MinValue;

        public bool TryGet(out T? value)
        {
            value = _value is not null && DateTime.UtcNow <= _expiry ? _value : null;
            return value is not null;
        }

        public void Set(T value, int ttlSeconds)
        {
            _value = value;
            _expiry = DateTime.UtcNow.AddSeconds(ttlSeconds);
        }

        public void Clear()
        {
            _value = null;
            _expiry = DateTime.MinValue;
        }
    }
}
