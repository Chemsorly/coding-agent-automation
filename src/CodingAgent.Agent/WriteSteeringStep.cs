using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services.Steps;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Agent;

/// <summary>
/// Writes pipeline-managed steering files before the agent starts.
/// Branches on agent provider type: Kiro CLI gets .kiro/steering/ files; Claude Code gets user-level
/// rules in ~/.claude/rules/ and OpenCode instruction files in ~/.opencode/, both outside the workspace.
/// </summary>
internal sealed class WriteSteeringStep : IPipelineStep
{
    private readonly JobAssignmentMessage _job;
    private readonly ILogger _logger;
    private readonly string _claudeRulesDirectory;
    private readonly string _openCodeDirectory;

    public string StepName => "WriteSteering";

    public WriteSteeringStep(
        JobAssignmentMessage job, ILogger? logger = null, string? claudeRulesDirectory = null, string? openCodeDirectory = null)
    {
        _job = job;
        _logger = logger ?? Log.Logger;
        _claudeRulesDirectory = claudeRulesDirectory ?? ClaudeSteeringFiles.DefaultRulesDirectory;
        _openCodeDirectory = openCodeDirectory ?? OpenCodeSteeringFiles.DefaultDirectory;
    }

    public Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        var providerType = context.AgentProvider.ProviderType;

        // Claude Code and OpenCode steering lives outside the workspace, so stale files from an
        // earlier job are removed even when this job has no steering.
        if (string.IsNullOrEmpty(_job.ProjectSteeringContent) && string.IsNullOrEmpty(_job.RepoSteeringContent)
            && providerType is not (AgentProviderType.ClaudeCode or AgentProviderType.OpenCode))
        {
            _logger.Debug("Pipeline {RunId} no steering content configured, skipping", context.Run.RunId);
            return Task.FromResult(StepResult.Continue);
        }

        try
        {
            var workspacePath = context.Run.WorkspacePath!;

            switch (providerType)
            {
                case AgentProviderType.KiroCli:
                    WriteKiroSteeringFiles(workspacePath, context);
                    break;
                case AgentProviderType.ClaudeCode:
                    WriteClaudeSteeringFiles(context);
                    break;
                default:
                    WriteOpenCodeSteeringFiles(context);
                    break;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Pipeline {RunId} failed to write steering files to workspace, continuing without them",
                context.Run.RunId);
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

    private void WriteClaudeSteeringFiles(PipelineStepContext context)
    {
        var written = ClaudeSteeringFiles.Write(_claudeRulesDirectory, _job.ProjectSteeringContent, _job.RepoSteeringContent);
        if (written.Count > 0)
            context.Callbacks.EmitOutputLine($"📋 Wrote pipeline steering ({string.Join("+", written)}) to {_claudeRulesDirectory}");
    }

    private void WriteOpenCodeSteeringFiles(PipelineStepContext context)
    {
        var written = OpenCodeSteeringFiles.Write(_openCodeDirectory, _job.ProjectSteeringContent, _job.RepoSteeringContent);
        if (written.Count > 0)
            context.Callbacks.EmitOutputLine($"📋 Wrote pipeline steering ({string.Join("+", written)}) to {_openCodeDirectory}");
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
