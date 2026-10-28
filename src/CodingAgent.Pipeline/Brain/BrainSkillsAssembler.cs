using System.Text;

namespace CodingAgent.Pipeline.Brain;

/// <summary>Well-known result values for brain skills injection (req 5.6).</summary>
public static class BrainSkillsResults
{
    public const string Injected = "injected";
    public const string Empty = "empty";
    public const string Truncated = "truncated";
    public const string SkippedInvalid = "skipped_invalid";
    public const string NoBrain = "no_brain";
    public const string Unknown = "unknown";
}

/// <summary>A file that was skipped during skills assembly.</summary>
public sealed record BrainSkippedFile(string Path, string Reason);

/// <summary>Result of assembling brain skills into the injection text.</summary>
public sealed record BrainSkillsAssembly(
    string Text,
    string Result,
    int Bytes,
    IReadOnlyList<string> FilesInjected,
    IReadOnlyList<BrainSkippedFile> FilesSkipped);

/// <summary>
/// Assembles brain skill files into the text injected into each run (spec 054, req 5).
/// Pure — no I/O.
/// </summary>
public static class BrainSkillsAssembler
{
    private const string HeaderSentence =
        "Lessons this pipeline learned from earlier runs. Each line names the issues it comes from. Follow them unless this issue's requirements say otherwise.";

    /// <summary>
    /// Assembles the injection text from the given file map.
    /// </summary>
    /// <param name="files">Map from relative brain path to content.</param>
    /// <param name="repositoryShortName">Short name of the repository (null/empty to skip).</param>
    /// <param name="stackLabels">Stack labels in template order.</param>
    public static BrainSkillsAssembly Assemble(
        IReadOnlyDictionary<string, string> files,
        string? repositoryShortName,
        IReadOnlyList<string> stackLabels)
    {
        // Build ordered list of candidate paths
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(repositoryShortName))
            candidates.Add(BrainLayout.RepositorySkillPath(repositoryShortName));

        // Stack labels: de-duplicate case-insensitively, skip null paths
        var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in stackLabels)
        {
            if (!seenLabels.Add(label)) continue;
            var techPath = BrainLayout.TechnologySkillPath(label);
            if (techPath is not null)
                candidates.Add(techPath);
        }
        candidates.Add(BrainLayout.GeneralSkillPath);

        // Validate each present file
        var included = new List<(string path, string content)>();
        var skipped = new List<BrainSkippedFile>();

        foreach (var path in candidates)
        {
            if (!files.TryGetValue(path, out var content))
                continue;

            var maxLines = BrainLineFormat.MaxLinesFor(path);
            var check = BrainLineFormat.CheckSkillFile(content, maxLines);
            if (!check.IsValid)
            {
                skipped.Add(new BrainSkippedFile(path, check.Reason!));
                continue;
            }

            // Technology and general files: check for shared content in each line body
            if (path != BrainLayout.GeneralSkillPath && !path.StartsWith("projects/", StringComparison.Ordinal))
            {
                // technology path
                bool anyViolation = check.Lines.Any(sl =>
                    BrainLineFormat.FindSharedContentViolation(sl.Body, []) is not null);
                if (anyViolation)
                {
                    skipped.Add(new BrainSkippedFile(path, "shared_line_content"));
                    continue;
                }
            }
            else if (path == BrainLayout.GeneralSkillPath)
            {
                bool anyViolation = check.Lines.Any(sl =>
                    BrainLineFormat.FindSharedContentViolation(sl.Body, []) is not null);
                if (anyViolation)
                {
                    skipped.Add(new BrainSkippedFile(path, "shared_line_content"));
                    continue;
                }
            }

            if (check.Lines.Count == 0)
                continue; // skip silently — no lines to inject

            included.Add((path, content));
        }

        // Drop from the end until under MaxInjectedBytes
        bool truncated = false;
        while (included.Count > 0)
        {
            var text = BuildText(included);
            if (Encoding.UTF8.GetByteCount(text) <= BrainLayout.MaxInjectedBytes)
                break;
            included.RemoveAt(included.Count - 1);
            truncated = true;
        }

        if (included.Count == 0)
        {
            // Even the repository skill alone is over budget — emit empty
            var result = truncated || skipped.Count > 0
                ? (skipped.Count > 0 ? BrainSkillsResults.SkippedInvalid : BrainSkillsResults.Empty)
                : BrainSkillsResults.Empty;
            return new BrainSkillsAssembly(string.Empty, result, 0, [], skipped);
        }

        var finalText = BuildText(included);
        var bytes = Encoding.UTF8.GetByteCount(finalText);
        var resultValue = truncated
            ? BrainSkillsResults.Truncated
            : skipped.Count > 0
                ? BrainSkillsResults.SkippedInvalid
                : BrainSkillsResults.Injected;

        return new BrainSkillsAssembly(
            finalText,
            resultValue,
            bytes,
            included.Select(x => x.path).ToList(),
            skipped);
    }

    private static string BuildText(List<(string path, string content)> included)
    {
        var sb = new StringBuilder();
        sb.Append(HeaderSentence);
        sb.Append('\n');

        bool first = true;
        foreach (var (_, content) in included)
        {
            if (!first) sb.Append('\n');
            first = false;

            // Remove the marker line from the top of each file
            var fileLines = content.Split('\n');
            bool markerSkipped = false;
            bool hasContent = false;
            var fileContentLines = new List<string>();
            foreach (var line in fileLines)
            {
                var trimmed = line.TrimEnd('\r');
                if (!markerSkipped && trimmed == BrainLayout.SkillMarker)
                {
                    markerSkipped = true;
                    continue;
                }
                fileContentLines.Add(trimmed);
            }
            // Trim trailing empty lines
            while (fileContentLines.Count > 0 && string.IsNullOrEmpty(fileContentLines[^1]))
                fileContentLines.RemoveAt(fileContentLines.Count - 1);
            // Trim leading empty lines
            while (fileContentLines.Count > 0 && string.IsNullOrEmpty(fileContentLines[0]))
                fileContentLines.RemoveAt(0);

            if (fileContentLines.Count > 0)
            {
                sb.Append('\n');
                sb.Append(string.Join("\n", fileContentLines));
                hasContent = true;
            }
            _ = hasContent; // suppress unused warning
        }

        return sb.ToString();
    }
}
