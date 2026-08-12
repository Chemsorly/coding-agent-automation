using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Default implementation of <see cref="IDependencyChecker"/>.
/// Parses issue body for dependency references and checks each against the issue provider.
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

    public async Task<DependencyCheckResult> CheckAsync(
        IssueIdentifier issueIdentifier,
        string? issueBody,
        IIssueProvider issueProvider,
        Dictionary<int, bool> stateCache,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value, nameof(issueIdentifier));
        ArgumentNullException.ThrowIfNull(issueProvider);
        ArgumentNullException.ThrowIfNull(stateCache);

        if (string.IsNullOrEmpty(issueBody))
            return DependencyCheckResult.NoDependencies;

        int? selfId = int.TryParse(issueIdentifier, out var parsed) ? parsed : null;
        var dependencies = DependencyParser.Parse(issueBody, selfId);

        if (dependencies.Count == 0)
            return DependencyCheckResult.NoDependencies;

        var blockedBy = new List<int>();

        foreach (var depNumber in dependencies)
        {
            ct.ThrowIfCancellationRequested();

            var isClosed = await ResolveIssueStateAsync(depNumber, issueIdentifier, issueProvider, stateCache, ct);
            if (!isClosed)
                blockedBy.Add(depNumber);
        }

        var isReady = blockedBy.Count == 0;

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
            TotalDependencies = dependencies.Count
        };
    }

    /// <summary>
    /// Returns whether the given dependency issue is closed, using <paramref name="stateCache"/>
    /// to avoid redundant API calls. Treats API failures as "not closed" (unresolved).
    /// </summary>
    private async Task<bool> ResolveIssueStateAsync(
        int depNumber, string issueIdentifier,
        IIssueProvider issueProvider, Dictionary<int, bool> stateCache,
        CancellationToken ct)
    {
        if (stateCache.TryGetValue(depNumber, out var cached))
            return cached;

        bool isClosed;
        try
        {
            isClosed = await issueProvider.IsIssueClosedAsync(depNumber.ToString(), ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(
                "Failed to check dependency #{DependencyNumber} for issue #{Identifier}: {ErrorMessage}. Treating as unresolved.",
                depNumber, issueIdentifier, ex.Message);
            isClosed = false;
        }

        stateCache[depNumber] = isClosed;
        return isClosed;
    }
}
