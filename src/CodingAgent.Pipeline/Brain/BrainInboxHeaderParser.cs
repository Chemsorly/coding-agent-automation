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

        var rawLines = text.Split('\n');
        var lines = rawLines.Select(l => l.TrimEnd('\r')).ToList();

        if (lines.Count == 0 || lines[0] != BrainLayout.InboxMarker)
        {
            error = "no marker";
            return false;
        }

        // Line 1: "# Run <id>"
        if (lines.Count < 2 || !lines[1].StartsWith("# Run ", StringComparison.Ordinal))
        {
            error = "broken header";
            return false;
        }
        var runId = lines[1]["# Run ".Length..].Trim();

        // Parse the dash-field lines after the title line
        string? runType = null, outcome = null, repository = null, project = null;
        var stackLabels = new List<string>();
        string? issue = null, pullRequest = null, skillsCommit = null;
        var skillsFiles = new List<string>();

        for (int i = 2; i < lines.Count; i++)
        {
            var l = lines[i];
            if (string.IsNullOrEmpty(l)) continue;
            if (l.StartsWith("## ", StringComparison.Ordinal)) break;

            if (TryGetField(l, "run type:", out var v)) runType = v;
            else if (TryGetField(l, "outcome:", out v)) outcome = v;
            else if (TryGetField(l, "repository:", out v)) repository = v;
            else if (TryGetField(l, "project:", out v)) project = v;
            else if (TryGetField(l, "stack labels:", out v))
            {
                stackLabels = v == "none"
                    ? []
                    : v.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
            }
            else if (TryGetField(l, "issue:", out v)) issue = v;
            else if (TryGetField(l, "pull request:", out v)) pullRequest = v;
            else if (TryGetField(l, "skills injected from:", out v))
            {
                if (v == "none")
                {
                    skillsCommit = null;
                    skillsFiles = [];
                }
                else
                {
                    // Format: "<sha> (file1, file2)" or "<sha>"
                    var parenIdx = v.IndexOf('(');
                    if (parenIdx > 0)
                    {
                        skillsCommit = v[..parenIdx].Trim();
                        var filesText = v[(parenIdx + 1)..].TrimEnd(')');
                        skillsFiles = filesText.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
                    }
                    else
                    {
                        skillsCommit = v.Trim();
                    }
                }
            }
        }

        if (repository is null)
        {
            error = "broken header";
            return false;
        }

        header = new BrainInboxHeader(
            runId,
            runType ?? "",
            outcome ?? "",
            repository,
            project,
            stackLabels,
            issue ?? "",
            pullRequest,
            skillsCommit,
            skillsFiles);
        return true;
    }

    private static bool TryGetField(string line, string prefix, out string value)
    {
        value = "";
        if (!line.StartsWith("- " + prefix, StringComparison.Ordinal)) return false;
        value = line[("- " + prefix).Length..].Trim();
        return true;
    }
}
