using System.Text.RegularExpressions;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services.Steps;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Agent;

/// <summary>
/// Writes pipeline-managed steering files before the agent starts.
/// Branches on agent provider type: Kiro CLI gets .kiro/steering/ files, OpenCode gets AGENTS.md,
/// Claude Code gets user-level rules in ~/.claude/rules/ (outside the workspace).
/// </summary>
internal sealed class WriteSteeringStep : IPipelineStep
{
    private const string BeginMarker = "<!-- BEGIN PIPELINE STEERING (auto-generated, do not commit) -->";
    private const string EndMarker = "<!-- END PIPELINE STEERING -->";

    private static readonly Regex SteeringBlockRegex = new(
        @"^" + Regex.Escape(BeginMarker) + @"\r?\n.*?" + Regex.Escape(EndMarker) + @"\r?\n?",
        RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.Compiled,
        matchTimeout: TimeSpan.FromSeconds(5));

    private readonly JobAssignmentMessage _job;
    private readonly ILogger _logger;
    private readonly string _claudeRulesDirectory;

    public string StepName => "WriteSteering";

    public WriteSteeringStep(JobAssignmentMessage job, ILogger? logger = null, string? claudeRulesDirectory = null)
    {
        _job = job;
        _logger = logger ?? Log.Logger;
        _claudeRulesDirectory = claudeRulesDirectory ?? ClaudeSteeringFiles.DefaultRulesDirectory;
    }

    public Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        var providerType = context.AgentProvider.ProviderType;

        // Claude Code steering lives outside the workspace, so stale files from an earlier job are
        // removed even when this job has no steering.
        if (string.IsNullOrEmpty(_job.ProjectSteeringContent) && string.IsNullOrEmpty(_job.RepoSteeringContent)
            && providerType != AgentProviderType.ClaudeCode)
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
                    WriteOpenCodeSteeringFile(workspacePath, context);
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
