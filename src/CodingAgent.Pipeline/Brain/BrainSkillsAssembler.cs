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
    public static BrainSkillsAssembly Assemble(
        IReadOnlyDictionary<string, string> files,
        string? repositoryShortName,
        IReadOnlyList<string> stackLabels)
    {
        var (included, skipped) = ClassifyFiles(files, repositoryShortName, stackLabels);
        return BuildAssembly(included, skipped);
    }

    private static (List<(string path, string content)> included, List<BrainSkippedFile> skipped)
        ClassifyFiles(
            IReadOnlyDictionary<string, string> files,
            string? repositoryShortName,
            IReadOnlyList<string> stackLabels)
    {
        var candidates = BuildCandidateList(repositoryShortName, stackLabels);
        var included = new List<(string path, string content)>();
        var skipped = new List<BrainSkippedFile>();

        foreach (var path in candidates)
        {
            if (!files.TryGetValue(path, out var content)) continue;
            var check = BrainLineFormat.CheckSkillFile(content, BrainLineFormat.MaxLinesFor(path));
            if (!check.IsValid) { skipped.Add(new BrainSkippedFile(path, check.Reason!)); continue; }
            if (RequiresSharedContentCheck(path) &&
                check.Lines.Any(sl => BrainLineFormat.FindSharedContentViolation(sl.Body, []) is not null))
            {
                skipped.Add(new BrainSkippedFile(path, "shared_line_content")); continue;
            }
            if (check.Lines.Count > 0) included.Add((path, content));
        }
        return (included, skipped);
    }

    private static List<string> BuildCandidateList(string? repositoryShortName, IReadOnlyList<string> stackLabels)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrEmpty(repositoryShortName))
            candidates.Add(BrainLayout.RepositorySkillPath(repositoryShortName));
        var seenLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var label in stackLabels)
        {
            if (!seenLabels.Add(label)) continue;
            var techPath = BrainLayout.TechnologySkillPath(label);
            if (techPath is not null) candidates.Add(techPath);
        }
        candidates.Add(BrainLayout.GeneralSkillPath);
        return candidates;
    }

    private static bool RequiresSharedContentCheck(string path) =>
        !path.StartsWith("projects/", StringComparison.Ordinal);

    private static BrainSkillsAssembly BuildAssembly(
        List<(string path, string content)> included,
        List<BrainSkippedFile> skipped)
    {
        bool truncated = false;
        while (included.Count > 0 &&
               Encoding.UTF8.GetByteCount(BuildText(included)) > BrainLayout.MaxInjectedBytes)
        {
            included.RemoveAt(included.Count - 1);
            truncated = true;
        }

        if (included.Count == 0)
        {
            var emptyResult = skipped.Count > 0 ? BrainSkillsResults.SkippedInvalid : BrainSkillsResults.Empty;
            return new BrainSkillsAssembly(string.Empty, emptyResult, 0, [], skipped);
        }

        var finalText = BuildText(included);
        var resultValue = DetermineResult(truncated, skipped.Count > 0);
        return new BrainSkillsAssembly(
            finalText,
            resultValue,
            Encoding.UTF8.GetByteCount(finalText),
            included.Select(x => x.path).ToList(),
            skipped);
    }

    private static string DetermineResult(bool truncated, bool anySkipped)
    {
        if (truncated) return BrainSkillsResults.Truncated;
        if (anySkipped) return BrainSkillsResults.SkippedInvalid;
        return BrainSkillsResults.Injected;
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
            var fileContentLines = ExtractBodyLines(content);
            if (fileContentLines.Count > 0)
            {
                sb.Append('\n');
                sb.Append(string.Join("\n", fileContentLines));
            }
        }
        return sb.ToString();
    }

    private static List<string> ExtractBodyLines(string content)
    {
        var lines = content.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var result = lines
            .SkipWhile(l => l.TrimEnd() == BrainLayout.SkillMarker || string.IsNullOrEmpty(l))
            .ToList();
        // Trim marker if it was skipped differently
        if (result.Count > 0 && result[0].TrimEnd() == BrainLayout.SkillMarker) result.RemoveAt(0);
        while (result.Count > 0 && string.IsNullOrEmpty(result[0])) result.RemoveAt(0);
        while (result.Count > 0 && string.IsNullOrEmpty(result[^1])) result.RemoveAt(result.Count - 1);
        return result;
    }
}
