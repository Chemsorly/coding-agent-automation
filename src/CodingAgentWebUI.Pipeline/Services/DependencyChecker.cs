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
        string issueIdentifier,
        string? issueBody,
        IIssueProvider issueProvider,
        Dictionary<int, bool> stateCache,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);
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

            bool isClosed;

            if (stateCache.TryGetValue(depNumber, out var cached))
            {
                isClosed = cached;
            }
            else
            {
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
            }

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
}
