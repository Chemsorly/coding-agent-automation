using System.Text.Json.Nodes;

namespace CodingAgent.Agent;

/// <summary>
/// Writes pipeline steering for OpenCode outside the workspace, the way <see cref="ClaudeSteeringFiles"/>
/// does for Claude Code: <c>~/.opencode/pipeline-project.md</c> and <c>pipeline-repo.md</c>, listed
/// under <c>instructions</c> in <c>~/.opencode/opencode.json</c>. OpenCode reads that list when it first
/// serves a workspace and the files into every turn's system prompt. The repository's own
/// <c>AGENTS.md</c> is left untouched, so a run's edits to it are committed like any other.
/// </summary>
internal static class OpenCodeSteeringFiles
{
    internal const string ProjectFileName = "pipeline-project.md";
    internal const string RepoFileName = "pipeline-repo.md";
    internal const string ConfigFileName = "opencode.json";

    /// <summary><c>~/.opencode</c> of the user the agent runs as.</summary>
    internal static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".opencode");

    /// <summary>
    /// Deletes the pipeline's steering files left by an earlier job, writes the given content, and makes
    /// sure the config lists them. Null or empty content writes no file for that part.
    /// </summary>
    /// <returns>The parts written ("project", "repo").</returns>
    internal static IReadOnlyList<string> Write(string directory, string? projectContent, string? repoContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory.CreateDirectory(directory);

        // A long-lived agent runs many jobs: steering from the last job must not leak into this one.
        foreach (var fileName in new[] { ProjectFileName, RepoFileName })
        {
            var path = Path.Combine(directory, fileName);
            if (File.Exists(path))
                File.Delete(path);
        }

        var written = new List<string>();
        if (!string.IsNullOrEmpty(projectContent))
        {
            File.WriteAllText(Path.Combine(directory, ProjectFileName), $"# Project Instructions\n\n{projectContent}\n");
            written.Add("project");
        }
        if (!string.IsNullOrEmpty(repoContent))
        {
            File.WriteAllText(Path.Combine(directory, RepoFileName), $"# Repository Instructions\n\n{repoContent}\n");
            written.Add("repo");
        }

        var instructions = Path.Combine(directory, "pipeline-*.md");
        OpenCodeConfigFile.Update(Path.Combine(directory, ConfigFileName), root =>
        {
            if (root["instructions"] is not JsonArray list)
            {
                list = new JsonArray();
                root["instructions"] = list;
            }
            if (!list.Any(entry => entry is JsonValue value && value.TryGetValue<string>(out var path) && path == instructions))
                list.Add(instructions);
        });

        return written;
    }
}
