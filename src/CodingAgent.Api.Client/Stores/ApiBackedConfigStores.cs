using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Api.Client.Stores;

/// <summary>
/// API-backed implementation of <see cref="IPipelineConfigStore"/> using <see cref="IPipelineApiConfigClient"/>.
/// Implements TTL caching (configurable via <see cref="CacheTtlSeconds"/>) to avoid excessive API
/// calls during the tight polling loop.
/// Thread-safe: the lock is released before awaiting to avoid holding a lock across async I/O.
/// Two concurrent callers may both reach the API (double-fetch window) — acceptable trade-off.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(Justification = "HTTP-backed store — covered by integration tests, not unit tests.")]
public sealed class ApiPipelineConfigStore : IPipelineConfigStore
{
    private readonly IPipelineApiConfigClient _client;
    private readonly Lock _cacheLock = new();
    private readonly TtlCache<PipelineConfiguration> _cache = new();
    public int CacheTtlSeconds { get; set; } = 60;

    public ApiPipelineConfigStore(IPipelineApiConfigClient client) => _client = client;

    /// <summary>Drops the cached configuration so the next load goes to the API.</summary>
    public void InvalidateCaches()
    {
        lock (_cacheLock) { _cache.Clear(); }
    }

    public Task<PipelineConfiguration> LoadPipelineConfigAsync(CancellationToken ct)
        => LoadCachedAsync(_cache, _client.GetPipelineConfigAsync, ct);

    public async Task SavePipelineConfigAsync(PipelineConfiguration config, CancellationToken ct)
    {
        await _client.SavePipelineConfigAsync(config, ct);
        // Populate (not just invalidate) so the next read within the TTL window uses the
        // value we just saved rather than fetching it back from the API.
        lock (_cacheLock) { _cache.Set(config, CacheTtlSeconds); }
    }

    public async Task UpdatePipelineConfigAsync(Func<PipelineConfiguration, PipelineConfiguration> transform, CancellationToken ct)
    {
        await _client.UpdatePipelineConfigAsync(transform, ct);
        // Clear is the correct equivalent of the previous `_cached = null`: TtlCache.TryGet
        // gates on _value is not null first, so a null value causes a miss regardless of _expiry.
        lock (_cacheLock) { _cache.Clear(); }
    }

    private Task<T> LoadCachedAsync<T>(TtlCache<T> cache, Func<CancellationToken, Task<T>> fetch, CancellationToken ct)
        where T : class => cache.LoadAsync(_cacheLock, () => CacheTtlSeconds, fetch, ct);
}

/// <summary>
/// API-backed implementation of <see cref="IProviderConfigStore"/> using <see cref="IPipelineApiConfigClient"/>.
/// Implements TTL caching to avoid excessive API calls.
/// Thread-safe: the lock is released before awaiting to avoid holding a lock across async I/O.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(Justification = "HTTP-backed store — covered by integration tests, not unit tests.")]
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
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(Justification = "Internal cache helper — covered by integration tests.")]
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
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(Justification = "HTTP-backed store — covered by integration tests, not unit tests.")]
public sealed class ApiProjectStore : IProjectStore
{
    private readonly IPipelineApiConfigClient _client;
    private readonly Lock _cacheLock = new();
    private readonly TtlCache<IReadOnlyList<PipelineProject>> _projectsCache = new();
    private readonly TtlCache<IReadOnlyList<PipelineJobTemplate>> _templatesCache = new();
    public int CacheTtlSeconds { get; set; } = 60;

    public ApiProjectStore(IPipelineApiConfigClient client) => _client = client;

    /// <summary>Drops cached projects and templates so the next load goes to the API.</summary>
    public void InvalidateCaches()
    {
        lock (_cacheLock)
        {
            _projectsCache.Clear();
            _templatesCache.Clear();
        }
    }

    public Task<IReadOnlyList<PipelineProject>> LoadProjectsAsync(CancellationToken ct)
        => LoadCachedAsync(_projectsCache, _client.GetProjectsAsync, ct);

    public async Task<PipelineProject?> GetProjectByIdAsync(string id, CancellationToken ct)
        => await _client.GetProjectByIdAsync(id, ct);

    public async Task SaveProjectAsync(PipelineProject project, CancellationToken ct)
    {
        await _client.SaveProjectAsync(project, ct);
        lock (_cacheLock) { _projectsCache.Clear(); }
    }

    public async Task DeleteProjectAsync(string id, CancellationToken ct)
    {
        await _client.DeleteProjectAsync(id, ct);
        lock (_cacheLock) { _projectsCache.Clear(); }
    }

    public async Task<IReadOnlyList<PipelineJobTemplate>> LoadTemplatesForProjectAsync(string projectId, CancellationToken ct)
        => await _client.GetTemplatesForProjectAsync(projectId, ct);

    public Task<IReadOnlyList<PipelineJobTemplate>> LoadAllTemplatesAsync(CancellationToken ct)
        => LoadCachedAsync(_templatesCache, _client.GetAllTemplatesAsync, ct);

    public async Task SaveTemplateAsync(string projectId, PipelineJobTemplate template, CancellationToken ct)
    {
        await _client.SaveTemplateAsync(projectId, template, ct);
        lock (_cacheLock) { _templatesCache.Clear(); }
    }

    public async Task DeleteTemplateAsync(string projectId, TemplateId templateId, CancellationToken ct)
    {
        await _client.DeleteTemplateAsync(projectId, templateId.ToString(), ct);
        lock (_cacheLock) { _templatesCache.Clear(); }
    }

    public async Task MoveTemplateAsync(string sourceProjectId, string targetProjectId, TemplateId templateId, CancellationToken ct)
    {
        await _client.MoveTemplateAsync(sourceProjectId, targetProjectId, templateId.ToString(), ct);
        lock (_cacheLock)
        {
            _templatesCache.Clear();
            _projectsCache.Clear();
        }
    }

    public async Task<bool> HasEnabledTemplatesAsync(CancellationToken ct)
        => await _client.HasEnabledTemplatesAsync(ct);

    private Task<T> LoadCachedAsync<T>(TtlCache<T> cache, Func<CancellationToken, Task<T>> fetch, CancellationToken ct)
        where T : class => cache.LoadAsync(_cacheLock, () => CacheTtlSeconds, fetch, ct);
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
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(Justification = "HTTP-backed store — covered by integration tests, not unit tests.")]
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

    private Task<T> LoadCachedAsync<T>(TtlCache<T> cache, Func<CancellationToken, Task<T>> fetch, CancellationToken ct)
        where T : class => cache.LoadAsync(_cacheLock, () => CacheTtlSeconds, fetch, ct);

    private async Task WriteThenInvalidateAsync<T>(Task write, TtlCache<T> cache) where T : class
    {
        await write;
        lock (_cacheLock) cache.Clear();
    }
}

/// <summary>
/// A single TTL-cached value shared by <see cref="ApiPipelineConfigStore"/>,
/// <see cref="ApiProjectStore"/>, and <see cref="ApiConfigurationStore"/>.
///
/// Not thread-safe on its own — callers must hold a shared lock around every member call
/// (<see cref="TryGet"/>, <see cref="Set"/>, <see cref="Clear"/>). The lock is released
/// across any awaited fetch so it is never held across network I/O.
/// </summary>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(Justification = "Internal cache helper — covered by integration tests.")]
internal sealed class TtlCache<T> where T : class
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

    /// <summary>
    /// Returns the cached value if fresh, otherwise calls <paramref name="fetch"/>, caches the
    /// result, and returns it.
    /// Deliberately releases the lock across the async fetch: two concurrent callers may both
    /// reach the API (double-fetch window), which is cheaper than holding the lock across I/O.
    /// CacheTtlSeconds is read outside the lock via the <paramref name="getTtl"/> delegate; on
    /// x86/x64 a torn read of an aligned int cannot occur, but this is formally undefined under
    /// the C# memory model without volatile/Interlocked — acceptable for a best-effort TTL.
    /// </summary>
    public async Task<T> LoadAsync(
        Lock cacheLock,
        Func<int> getTtl,
        Func<CancellationToken, Task<T>> fetch,
        CancellationToken ct)
    {
        lock (cacheLock)
        {
            if (TryGet(out var hit)) return hit!;
        }

        var fresh = await fetch(ct);

        lock (cacheLock) { Set(fresh, getTtl()); }
        return fresh;
    }
}
