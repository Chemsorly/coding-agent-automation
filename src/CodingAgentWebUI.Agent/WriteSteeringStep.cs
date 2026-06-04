using System.Text.RegularExpressions;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Steps;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Writes pipeline-managed steering files to the workspace before the agent starts.
/// Branches on agent provider type: Kiro CLI gets .kiro/steering/ files, OpenCode gets AGENTS.md.
/// </summary>
internal sealed class WriteSteeringStep : IPipelineStep
{
    private const string BeginMarker = "<!-- BEGIN PIPELINE STEERING (auto-generated, do not commit) -->";
    private const string EndMarker = "<!-- END PIPELINE STEERING -->";

    private static readonly Regex SteeringBlockRegex = new(
        @"^" + Regex.Escape(BeginMarker) + @"\r?\n.*?" + Regex.Escape(EndMarker) + @"\r?\n?",
        RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.Compiled);

    private readonly JobAssignmentMessage _job;

    public WriteSteeringStep(JobAssignmentMessage job)
    {
        _job = job;
    }

    public Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_job.ProjectSteeringContent) && string.IsNullOrEmpty(_job.RepoSteeringContent))
            return Task.FromResult(StepResult.Continue);

        try
        {
            var workspacePath = context.Run.WorkspacePath!;

            if (context.AgentProvider.ProviderType == AgentProviderType.KiroCli)
                WriteKiroSteeringFiles(workspacePath, context);
            else
                WriteOpenCodeSteeringFile(workspacePath, context);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            context.Logger.Warning(ex, "Failed to write steering files to workspace, continuing without them");
        }

        return Task.FromResult(StepResult.Continue);
    }

    private void WriteKiroSteeringFiles(string workspacePath, PipelineStepContext context)
    {
        var steeringDir = Path.Combine(workspacePath, ".kiro", "steering");
        Directory.CreateDirectory(steeringDir);

        var written = new List<string>();

        if (!string.IsNullOrEmpty(_job.ProjectSteeringContent))
        {
            var path = Path.Combine(workspacePath, AgentWorkspacePaths.KiroSteeringProjectFilePath);
            File.WriteAllText(path, FormatKiroFile(_job.ProjectSteeringContent));
            written.Add("project");
        }

        if (!string.IsNullOrEmpty(_job.RepoSteeringContent))
        {
            var path = Path.Combine(workspacePath, AgentWorkspacePaths.KiroSteeringRepoFilePath);
            File.WriteAllText(path, FormatKiroFile(_job.RepoSteeringContent));
            written.Add("repo");
        }

        context.Callbacks.EmitOutputLine($"📋 Wrote pipeline steering ({string.Join("+", written)}) to .kiro/steering/");
    }

    private void WriteOpenCodeSteeringFile(string workspacePath, PipelineStepContext context)
    {
        var agentsPath = Path.Combine(workspacePath, AgentWorkspacePaths.OpenCodeAgentsFilePath);

        // Read existing content and strip previous pipeline block
        var existingContent = File.Exists(agentsPath) ? File.ReadAllText(agentsPath) : string.Empty;
        existingContent = SteeringBlockRegex.Replace(existingContent, string.Empty);

        // Build pipeline steering block
        var block = BuildOpenCodeBlock();

        // Prepend pipeline block, preserve existing content below
        var combined = string.IsNullOrEmpty(existingContent)
            ? block
            : block + "\n" + existingContent;

        File.WriteAllText(agentsPath, combined);
        context.Callbacks.EmitOutputLine("📋 Wrote pipeline steering to AGENTS.md");
    }

    private string BuildOpenCodeBlock()
    {
        var parts = new List<string> { BeginMarker };

        if (!string.IsNullOrEmpty(_job.ProjectSteeringContent))
        {
            parts.Add("# Project Instructions");
            parts.Add(string.Empty);
            parts.Add(_job.ProjectSteeringContent);
        }

        if (!string.IsNullOrEmpty(_job.RepoSteeringContent))
        {
            if (!string.IsNullOrEmpty(_job.ProjectSteeringContent))
                parts.Add(string.Empty);
            parts.Add("# Repository Instructions");
            parts.Add(string.Empty);
            parts.Add(_job.RepoSteeringContent);
        }

        parts.Add(EndMarker);
        return string.Join("\n", parts) + "\n";
    }

    private static string FormatKiroFile(string content) =>
        $"""
        ---
        inclusion: always
        ---

        <!-- Written by automation pipeline. Do not edit manually. -->

        {content}
        """;
}
