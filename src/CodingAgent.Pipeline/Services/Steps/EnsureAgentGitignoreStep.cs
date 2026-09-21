using CodingAgent.Pipeline.Interfaces;
using Serilog;

namespace CodingAgent.Pipeline.Services.Steps;

/// <summary>
/// Ensures .agent/ is in the workspace's .gitignore at the start of any pipeline run.
/// Prevents accidental agent metadata (MCP configs, prompt files, analysis output)
/// from being committed by the agent or appearing in diffs.
/// </summary>
public sealed class EnsureAgentGitignoreStep : IPipelineStep
{
    public string StepName => "EnsureAgentGitignore";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        if (context.Run.WorkspacePath is null)
            return StepResult.Continue;

        var gitignorePath = Path.Combine(context.Run.WorkspacePath, ".gitignore");
        var content = File.Exists(gitignorePath)
            ? await File.ReadAllTextAsync(gitignorePath, ct)
            : "";

        // TODO: [WARNING] The trailing "/" is appended here at the call site rather than being a
        // named constant in AgentWorkspacePaths (e.g. a MetadataDirectoryPattern = ".agent/" const).
        // All other derived paths in AgentWorkspacePaths are compile-time constants; this concatenation
        // is a runtime heap allocation on every call and leaves the trailing-slash convention
        // undocumented in the constants class. If additional callers need the gitignore form of the
        // path, consider adding a dedicated const string MetadataDirectoryGitignoreEntry = ".agent/"
        // to AgentWorkspacePaths rather than repeating the `+ "/"` pattern at each call site.
        var updated = IBrainUpdateService.EnsureGitignoreEntry(content, AgentWorkspacePaths.MetadataDirectory + "/");
        if (updated != content)
        {
            await File.WriteAllTextAsync(gitignorePath, updated, ct);
            Log.Debug("Pipeline {RunId} added .agent/ to .gitignore", context.Run.RunId);
        }

        return StepResult.Continue;
    }
}
