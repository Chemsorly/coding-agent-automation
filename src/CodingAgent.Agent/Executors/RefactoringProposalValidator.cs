using System.Text;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Agent.Executors;

/// <summary>
/// Deterministic checks a refactoring proposal must pass before it becomes an issue. The prompts ask for
/// all of this, but an agent can still get it wrong, and the adversarial review is optional: these checks
/// catch excluded paths, files that do not exist, scope over the single-run limit, unknown categories,
/// and titles that duplicate an existing issue or another proposal of the same batch.
/// </summary>
internal static class RefactoringProposalValidator
{
    /// <summary>The per-proposal file limit the aggregation prompt states for one agent run.</summary>
    internal const int MaxAffectedFiles = 30;

    /// <summary>Pipeline scratch space and tooling: never project code a proposal may target.</summary>
    private static readonly string[] ExcludedDirectoryPrefixes =
    [
        AgentWorkspacePaths.MetadataDirectory + "/",
        AgentWorkspacePaths.BrainDirectory + "/",
        ".git/"
    ];

    internal sealed record Rejection(RefactoringProposal Proposal, string Reason);

    /// <summary>
    /// Splits <paramref name="proposals"/> into the ones that pass every check and the rejected ones with
    /// their reason, keeping the input order. A title counts as a duplicate when, after normalization, it
    /// equals one of <paramref name="existingIssueTitles"/> or the title of an earlier valid proposal.
    /// </summary>
    internal static (IReadOnlyList<RefactoringProposal> Valid, IReadOnlyList<Rejection> Rejected) Validate(
        IReadOnlyList<RefactoringProposal> proposals,
        string workspacePath,
        IEnumerable<string> existingIssueTitles)
    {
        var seenTitles = new HashSet<string>(existingIssueTitles.Select(NormalizeTitle), StringComparer.Ordinal);
        var valid = new List<RefactoringProposal>();
        var rejected = new List<Rejection>();

        foreach (var proposal in proposals)
        {
            var reason = GetRejectionReason(proposal, workspacePath, seenTitles);
            if (reason is null)
            {
                valid.Add(proposal);
                seenTitles.Add(NormalizeTitle(proposal.Title));
            }
            else
            {
                rejected.Add(new Rejection(proposal, reason));
            }
        }

        return (valid, rejected);
    }

    private static string? GetRejectionReason(RefactoringProposal proposal, string workspacePath, HashSet<string> seenTitles)
    {
        if (string.IsNullOrWhiteSpace(proposal.Title))
            return "empty title";

        var files = (proposal.AffectedFiles ?? [])
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(NormalizePath)
            .ToList();

        if (files.Count == 0)
            return "no affected files";

        if (files.Count > MaxAffectedFiles)
            return $"{files.Count} affected files exceed the limit of {MaxAffectedFiles}";

        var excluded = files.FirstOrDefault(IsExcluded);
        if (excluded is not null)
            return $"affected file '{excluded}' is pipeline scratch space, not project code";

        if (!files.Any(f => ExistsInWorkspace(workspacePath, f)))
            return "none of the affected files exist in the repository";

        if (proposal.Category is not null && !RefactoringCategories.IsKnown(proposal.Category))
            return $"unknown category '{proposal.Category}'";

        if (seenTitles.Contains(NormalizeTitle(proposal.Title)))
            return "title duplicates an existing issue or another proposal";

        return null;
    }

    private static string NormalizePath(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        while (normalized.StartsWith("./", StringComparison.Ordinal))
            normalized = normalized[2..];
        return normalized.TrimStart('/');
    }

    private static bool IsExcluded(string path) =>
        ExcludedDirectoryPrefixes.Any(prefix => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    private static bool ExistsInWorkspace(string workspacePath, string relativePath)
    {
        var root = Path.GetFullPath(workspacePath);
        var fullPath = Path.GetFullPath(Path.Combine(root, relativePath));
        var rootWithSeparator = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;

        return fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase) && File.Exists(fullPath);
    }

    /// <summary>Lower-cases the title and keeps only letters and digits, separated by single spaces.</summary>
    internal static string NormalizeTitle(string title)
    {
        var sb = new StringBuilder(title.Length);
        var pendingSpace = false;

        foreach (var c in title)
        {
            if (char.IsLetterOrDigit(c))
            {
                if (pendingSpace && sb.Length > 0)
                    sb.Append(' ');
                sb.Append(char.ToLowerInvariant(c));
                pendingSpace = false;
            }
            else
            {
                pendingSpace = true;
            }
        }

        return sb.ToString();
    }
}
