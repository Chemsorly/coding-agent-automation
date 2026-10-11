using System.Text;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services.Steps;

/// <summary>
/// Generates .agent/project-context.md listing all templates in the project
/// with their names, repository descriptions, and decomposition eligibility.
/// Only runs when the decomposition was triggered from a project-level EpicIssueProviderId.
/// </summary>
public sealed class WriteProjectContextStep : IPipelineStep
{
    public string StepName => "WriteProjectContext";

    public async Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        // Skip if not cross-repo decomposition (no project context needed)
        if (context.ProjectContext is null)
            return StepResult.Continue;

        var workspacePath = context.Run.WorkspacePath!;
        var agentDir = Path.Combine(workspacePath, AgentWorkspacePaths.MetadataDirectory);

        var sb = new StringBuilder();
        sb.AppendLine("# Project Context");
        sb.AppendLine();
        sb.AppendLine($"**Project:** {context.ProjectContext.ProjectName}");
        sb.AppendLine();
        var isTriage = context.Run.RunType == PipelineRunType.Triage;
        sb.AppendLine("## Available Repositories");
        sb.AppendLine();
        sb.AppendLine(isTriage
            ? "Investigate any of these repositories: the workspace root is one of them, the others are read-only clones. Assign each draft to the repository that must change using the `targetRepository` field. Values must EXACTLY match a repository name below (case-sensitive)."
            : "When proposing decomposed issues, assign each to the most appropriate repository using the `targetRepository` field. Values must EXACTLY match a repository name below (case-sensitive).");
        sb.AppendLine();

        foreach (var repo in context.ProjectContext.Repositories)
        {
            var status = repo.Available ? "✓" : "⚠️ unavailable";
            sb.AppendLine($"### {repo.TemplateName}");
            sb.AppendLine($"- **Description:** {repo.Description}");
            sb.AppendLine($"- **Decomposition enabled:** {repo.DecompositionEnabled}");
            sb.AppendLine($"- **Status:** {status}");
            if (repo.LocalPath is not null)
                sb.AppendLine($"- **Local path:** `{repo.LocalPath}/`");
            else if (repo.RepoProviderId == context.Run.RepoProviderConfigId)
                sb.AppendLine("- **Local path:** `.` (workspace root — primary repository)");
            if (repo.Labels.Count > 0)
                sb.AppendLine($"- **Labels:** {string.Join(", ", repo.Labels)}");
            sb.AppendLine();
        }

        sb.AppendLine("## Routing Instructions");
        sb.AppendLine();
        if (isTriage)
        {
            sb.AppendLine("- Set each draft's `targetRepository` to the exact name above of the repository that must change");
            sb.AppendLine("- If a fix spans repositories, propose one draft per repository");
            sb.AppendLine("- Never change, commit or push the read-only clones");
        }
        else
        {
            sb.AppendLine("- Set `targetRepository` in each sub-issue JSON file to the exact template name above");
            sb.AppendLine("- If an issue spans multiple repositories, assign to the PRIMARY repository and note cross-cutting dependencies in the issue body");
            sb.AppendLine("- Issues without `targetRepository` will be created in the default repository");
        }

        try
        {
            Directory.CreateDirectory(agentDir);
            await File.WriteAllTextAsync(Path.Combine(workspacePath, AgentWorkspacePaths.ProjectContextFilePath), sb.ToString(), ct);
            context.Callbacks.EmitOutputLine($"📋 Wrote {AgentWorkspacePaths.ProjectContextFilePath} with project repository context");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.Warning(ex,
                "Pipeline {RunId} failed to write project context file, continuing without it",
                context.Run.RunId);
        }

        return StepResult.Continue;
    }
}
