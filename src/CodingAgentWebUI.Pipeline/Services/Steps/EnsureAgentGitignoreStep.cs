using CodingAgentWebUI.Pipeline.Interfaces;
using Serilog;

namespace CodingAgentWebUI.Pipeline.Services.Steps;

/// <summary>
/// Ensures .agent/ is in the workspace's .gitignore at the start of any pipeline run.
/// Prevents accidental agent metadata (MCP configs, prompt files, analysis output)
/// from being committed by the agent or appearing in diffs.
/// </summary>
internal sealed class EnsureAgentGitignoreStep : IPipelineStep
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

        var updated = IBrainUpdateService.EnsureGitignoreEntry(content, ".agent/");
        if (updated != content)
        {
            await File.WriteAllTextAsync(gitignorePath, updated, ct);
            Log.Debug("Pipeline {RunId} added .agent/ to .gitignore", context.Run.RunId);
        }

        return StepResult.Continue;
    }
}
