namespace CodingAgent.Pipeline.Brain;

/// <summary>
/// Parses a brain inbox file header (spec 054, req 3.2, task 2B.6). Pure.
/// </summary>
public static class BrainInboxHeaderParser
{
    /// <summary>
    /// Tries to parse the header from an inbox file's text.
    /// </summary>
    public static bool TryParse(string text, out BrainInboxHeader header, out string? error)
    {
        header = null!;
        error = null;

        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        if (lines.Count == 0 || lines[0] != BrainLayout.InboxMarker)
        {
            error = "no marker"; return false;
        }
        if (lines.Count < 2 || !lines[1].StartsWith("# Run ", StringComparison.Ordinal))
        {
            error = "broken header"; return false;
        }
        var runId = lines[1]["# Run ".Length..].Trim();

        var fields = ParseFields(lines);
        if (!fields.TryGetValue("repository", out var repository) || repository is null)
        {
            error = "broken header"; return false;
        }

        var stackLabels = ParseStackLabels(fields.GetValueOrDefault("stack labels") ?? "none");
        var (skillsCommit, skillsFiles) = ParseSkillsInjected(fields.GetValueOrDefault("skills injected from") ?? "none");

        header = new BrainInboxHeader(
            runId,
            fields.GetValueOrDefault("run type") ?? "",
            fields.GetValueOrDefault("outcome") ?? "",
            repository,
            fields.GetValueOrDefault("project"),
            stackLabels,
            fields.GetValueOrDefault("issue") ?? "",
            fields.GetValueOrDefault("pull request"),
            skillsCommit,
            skillsFiles);
        return true;
    }

    private static Dictionary<string, string?> ParseFields(List<string> lines)
    {
        var fields = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 2; i < lines.Count; i++)
        {
            var l = lines[i];
            if (string.IsNullOrEmpty(l) || l.StartsWith("## ", StringComparison.Ordinal)) break;
            if (!l.StartsWith("- ", StringComparison.Ordinal)) continue;
            var colon = l.IndexOf(':', 2);
            if (colon < 0) continue;
            var key = l[2..colon].Trim();
            var value = l[(colon + 1)..].Trim();
            fields[key] = value;
        }
        return fields;
    }

    private static List<string> ParseStackLabels(string value) =>
        value == "none"
            ? []
            : value.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();

    private static (string? commit, List<string> files) ParseSkillsInjected(string value)
    {
        if (value == "none") return (null, []);
        var parenIdx = value.IndexOf('(');
        if (parenIdx > 0)
        {
            var commit = value[..parenIdx].Trim();
            var filesText = value[(parenIdx + 1)..].TrimEnd(')');
            var files = filesText.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
            return (commit, files);
        }
        return (value.Trim(), []);
    }
}
