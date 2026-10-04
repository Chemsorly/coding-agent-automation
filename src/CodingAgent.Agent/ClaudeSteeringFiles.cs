namespace CodingAgent.Agent;

/// <summary>
/// Writes pipeline steering for the Claude Code CLI as user-level rules
/// (<c>~/.claude/rules/*.md</c>). The CLI loads every rule file without a <c>paths:</c> header at
/// the start of each session, in every project — the counterpart of Kiro's
/// <c>.kiro/steering/*.md</c> with <c>inclusion: always</c>. Unlike Kiro's files these live outside
/// the workspace, so they can never be committed and do not touch the repository's own
/// <c>CLAUDE.md</c> or <c>.claude/rules/</c>, which the CLI loads as well.
/// See https://code.claude.com/docs/en/memory.
/// </summary>
internal static class ClaudeSteeringFiles
{
    internal const string ProjectFileName = "pipeline-project.md";
    internal const string RepoFileName = "pipeline-repo.md";

    /// <summary><c>~/.claude/rules</c> of the user the agent runs as.</summary>
    internal static string DefaultRulesDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "rules");

    /// <summary>
    /// Deletes the pipeline's rule files left by an earlier job, then writes the given content.
    /// Null or empty content writes no file for that part.
    /// </summary>
    /// <returns>The parts written ("project", "repo").</returns>
    internal static IReadOnlyList<string> Write(string rulesDirectory, string? projectContent, string? repoContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rulesDirectory);

        // A long-lived agent runs many jobs: steering from the last job must not leak into this one.
        foreach (var fileName in new[] { ProjectFileName, RepoFileName })
        {
            var path = Path.Combine(rulesDirectory, fileName);
            if (File.Exists(path))
                File.Delete(path);
        }

        var written = new List<string>();
        if (!string.IsNullOrEmpty(projectContent))
        {
            WriteRule(rulesDirectory, ProjectFileName, "Project Instructions", projectContent);
            written.Add("project");
        }

        if (!string.IsNullOrEmpty(repoContent))
        {
            WriteRule(rulesDirectory, RepoFileName, "Repository Instructions", repoContent);
            written.Add("repo");
        }

        return written;
    }

    private static void WriteRule(string rulesDirectory, string fileName, string heading, string content)
    {
        Directory.CreateDirectory(rulesDirectory);
        File.WriteAllText(Path.Combine(rulesDirectory, fileName),
            $"""
            <!-- Written by automation pipeline. Do not edit manually. -->

            # {heading}

            {content}
            """);
    }
}
