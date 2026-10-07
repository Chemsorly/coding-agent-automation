using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Serilog;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Default implementation of <see cref="IDependencyChecker"/>.
/// Parses issue body for dependency references and checks each against the issue provider(s).
/// Caches results in the provided dictionary to avoid redundant API calls within a cycle.
/// </summary>
public sealed class DependencyChecker : IDependencyChecker
{
    private readonly ILogger _logger;

    public DependencyChecker(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <inheritdoc/>
    public Task<DependencyCheckResult> CheckAsync(
        IssueIdentifier issueIdentifier,
        string? issueBody,
        IIssueProvider issueProvider,
        Dictionary<int, bool> stateCache,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value, nameof(issueIdentifier));
        ArgumentNullException.ThrowIfNull(issueProvider);
        ArgumentNullException.ThrowIfNull(stateCache);

        // Wrap the flat int-keyed cache into the per-provider structure expected by the new overload.
        // We use a dummy provider ID ("") so the existing single-provider cache is preserved correctly.
        // TODO: The empty-string sentinel key "" is also a technically valid defaultProviderId value
        // in the new overload. If a caller ever passes "" as defaultProviderId to the new overload
        // AND has a URL dependency matched to a provider also keyed "", their per-provider cache
        // buckets will collide. In practice all provider config IDs are non-empty GUIDs or slugs,
        // so this is unlikely — but consider using a truly internal sentinel (e.g. a private
        // const string) or switching the legacy overload to delegate via a distinct code path.
        const string defaultProviderId = "";
        var wrappedCaches = new Dictionary<string, Dictionary<int, bool>>
        {
            [defaultProviderId] = stateCache
        };

        return CheckAsync(
            issueIdentifier,
            issueBody,
            new DependencyRoutingContext
            {
                DefaultProvider = issueProvider,
                DefaultProviderId = defaultProviderId,
                AllProviders = new Dictionary<string, IIssueProvider>(),
                ProviderUrlPrefixes = new Dictionary<string, string>(),
                StateCaches = wrappedCaches
            },
            ct);
    }

    /// <inheritdoc/>
    public async Task<DependencyCheckResult> CheckAsync(
        IssueIdentifier issueIdentifier,
        string? issueBody,
        DependencyRoutingContext routing,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value, nameof(issueIdentifier));
        ArgumentNullException.ThrowIfNull(routing);
        var defaultProvider = routing.DefaultProvider;
        var defaultProviderId = routing.DefaultProviderId;
        var stateCaches = routing.StateCaches;
        ArgumentNullException.ThrowIfNull(defaultProvider);
        ArgumentNullException.ThrowIfNull(defaultProviderId);
        ArgumentNullException.ThrowIfNull(routing.AllProviders);
        ArgumentNullException.ThrowIfNull(routing.ProviderUrlPrefixes);
        ArgumentNullException.ThrowIfNull(stateCaches);

        if (string.IsNullOrEmpty(issueBody))
            return DependencyCheckResult.NoDependencies;

        int? selfId = int.TryParse(issueIdentifier, out var parsed) ? parsed : null;
        var dependencies = DependencyParser.Parse(issueBody, selfId);

        if (dependencies.Count == 0)
            return DependencyCheckResult.NoDependencies;

        var blockedBy = new List<int>();
        var blockedByUrls = new List<string>();

        // Ensure the default provider's cache bucket exists.
        if (!stateCaches.ContainsKey(defaultProviderId))
            stateCaches[defaultProviderId] = new Dictionary<int, bool>();

        foreach (var dep in dependencies)
        {
            ct.ThrowIfCancellationRequested();

            switch (dep)
            {
                case NumberRef(var number):
                {
                    var cache = stateCaches[defaultProviderId];
                    var isClosed = await ResolveIssueStateAsync(number.ToString(), issueIdentifier, defaultProvider, cache, ct);
                    if (!isClosed)
                        blockedBy.Add(number);
                    break;
                }

                case UrlRef(var url):
                {
                    var isClosed = await ResolveUrlDepAsync(url, issueIdentifier, routing, ct);
                    if (!isClosed)
                        blockedByUrls.Add(url);
                    break;
                }
            }
        }

        var isReady = blockedBy.Count == 0 && blockedByUrls.Count == 0;

        if (isReady)
        {
            _logger.Debug(
                "Issue #{Identifier} has {Count} dependencies, all satisfied. Eligible for dispatch.",
                issueIdentifier, dependencies.Count);
        }

        return new DependencyCheckResult
        {
            IsReady = isReady,
            BlockedBy = blockedBy,
            BlockedByUrls = blockedByUrls,
            TotalDependencies = dependencies.Count
        };
    }

    /// <summary>
    /// Resolves the state of a URL-based cross-tracker dependency reference.
    /// Matches the URL against <see cref="DependencyRoutingContext.ProviderUrlPrefixes"/> to find the correct provider,
    /// then calls <see cref="IIssueProvider.IsIssueClosedAsync"/> on that provider.
    /// If no matching provider prefix is found, logs a warning and treats the dependency as
    /// unresolved (returns false, blocking dispatch).
    /// </summary>
    private async Task<bool> ResolveUrlDepAsync(
        string url,
        string issueIdentifier,
        DependencyRoutingContext routing,
        CancellationToken ct)
    {
        var allProviders = routing.AllProviders;
        var stateCaches = routing.StateCaches;

        // Find the provider whose URL prefix matches the dependency URL.
        string? matchedProviderId = null;
        foreach (var (providerId, prefix) in routing.ProviderUrlPrefixes)
        {
            // TODO: StartsWith without a path-separator boundary check means a prefix like
            // "https://github.com/acme/api" also matches
            // "https://github.com/acme/api-internal/issues/1", silently routing the dependency
            // to the wrong provider. Fix: use `url.StartsWith(prefix.TrimEnd('/') + "/", ...)`
            // to enforce a separator boundary, or require that registered prefixes already end
            // with "/".
            if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                matchedProviderId = providerId;
                break;
            }
        }

        if (matchedProviderId is null || !allProviders.TryGetValue(matchedProviderId, out var provider))
        {
            _logger.Warning(
                "No configured issue provider matches URL dependency {Url} for issue #{Identifier}. Treating as unresolved.",
                url, issueIdentifier);
            return false;
        }

        // Extract the issue number from the URL — it's the last path segment after /issues/.
        var issueNumber = ExtractIssueNumberFromUrl(url);
        if (issueNumber is null)
        {
            _logger.Warning(
                "Could not extract issue number from URL dependency {Url} for issue #{Identifier}. Treating as unresolved.",
                url, issueIdentifier);
            return false;
        }

        // Use a per-provider cache bucket to avoid cross-tracker number collisions.
        if (!stateCaches.ContainsKey(matchedProviderId))
            stateCaches[matchedProviderId] = new Dictionary<int, bool>();

        var cache = stateCaches[matchedProviderId];
        return await ResolveIssueStateAsync(issueNumber, issueIdentifier, provider, cache, ct);
    }

    /// <summary>
    /// Extracts the issue number string from a GitHub or GitLab issue URL.
    /// Returns null if the URL does not match the expected pattern.
    /// </summary>
    private static string? ExtractIssueNumberFromUrl(string url)
    {
        // Both GitHub and GitLab issue URLs end with /issues/{digits}.
        // GitHub: https://github.com/{owner}/{repo}/issues/{number}
        // GitLab: https://gitlab.com/{namespace}/{project}/-/issues/{number}
        // In both cases LastIndexOf("/issues/") finds the correct terminal segment.
        // TODO: The comment above and this implementation both assume the URL was
        // already validated by the DependencyParser regex (which enforces the /-/ prefix
        // for GitLab). The method itself uses LastIndexOf("/issues/") without the /-/
        // guard, so it would extract the number from a structurally similar but invalid
        // URL too — but since callers only pass URLs that passed the parser regex, this
        // is safe as long as the two stay in sync.
        var issuesIndex = url.LastIndexOf("/issues/", StringComparison.OrdinalIgnoreCase);
        if (issuesIndex < 0)
            return null;

        var numberPart = url[(issuesIndex + "/issues/".Length)..];
        // Strip any trailing path segments or query strings
        var slashOrQuery = numberPart.IndexOfAny(['/', '?', '#']);
        if (slashOrQuery >= 0)
            numberPart = numberPart[..slashOrQuery];

        return numberPart.Length > 0 && numberPart.All(char.IsDigit) ? numberPart : null;
    }

    /// <summary>
    /// Returns whether the given dependency issue is closed, using <paramref name="stateCache"/>
    /// to avoid redundant API calls. Treats API failures as "not closed" (unresolved).
    /// </summary>
    private async Task<bool> ResolveIssueStateAsync(
        string issueNumber,
        string issueIdentifier,
        IIssueProvider issueProvider,
        Dictionary<int, bool> stateCache,
        CancellationToken ct)
    {
        // Cache key is the numeric value. Convert here; invalid (non-integer) numbers are treated
        // as uncached and checked directly — they won't match entries from numeric dep checks.
        if (!int.TryParse(issueNumber, out var numericKey))
        {
            // Non-numeric number string — check without caching.
            return await CallProviderAsync(issueNumber, issueIdentifier, issueProvider, ct);
        }

        if (stateCache.TryGetValue(numericKey, out var cached))
            return cached;

        var isClosed = await CallProviderAsync(issueNumber, issueIdentifier, issueProvider, ct);
        stateCache[numericKey] = isClosed;
        return isClosed;
    }

    private async Task<bool> CallProviderAsync(
        string issueNumber,
        string issueIdentifier,
        IIssueProvider issueProvider,
        CancellationToken ct)
    {
        try
        {
            return await issueProvider.IsIssueClosedAsync(issueNumber, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "Failed to check dependency #{DependencyNumber} for issue #{Identifier}: {ErrorMessage}. Treating as unresolved.",
                issueNumber, issueIdentifier, ex.Message);
            return false;
        }
    }
}
