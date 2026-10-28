using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services.Steps;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Agent;

/// <summary>
/// Writes (or removes) the brain skills steering file for the current agent provider.
/// Always runs, even when no brain is configured — ensures a stale file from an earlier run is removed.
/// Spec 054, req 2.1-2.2, 5.4.
/// </summary>
internal sealed class WriteBrainSkillsStep : IPipelineStep
{
    private readonly ILogger? _logger;
    private readonly string? _claudeRulesDirectory;
    private readonly string? _openCodeDirectory;

    public string StepName => "WriteBrainSkills";

    public WriteBrainSkillsStep(
        ILogger? logger = null,
        string? claudeRulesDirectory = null,
        string? openCodeDirectory = null)
    {
        _logger = logger;
        _claudeRulesDirectory = claudeRulesDirectory;
        _openCodeDirectory = openCodeDirectory;
    }

    public Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
    {
        var run = context.Run;

        try
        {
            var written = BrainSkillsWriter.Write(
                context.AgentProvider.ProviderType,
                run.WorkspacePath!,
                run.BrainSkills?.Text,
                _claudeRulesDirectory,
                _openCodeDirectory);

            if (written && run.BrainSkills is not null)
            {
                var lineCount = run.BrainSkills.Text
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Count(l => l.TrimStart().StartsWith("- [", StringComparison.Ordinal));
                var fileCount = run.BrainSkills.FilesInjected.Count;
                var result = run.BrainSkills.Result;
                context.Callbacks.EmitOutputLine(
                    $"📚 Brain skills: {lineCount} lines from {fileCount} file(s) ({result})");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            (_logger ?? Serilog.Log.Logger).Warning(
                ex, "Pipeline {RunId} failed to write brain skills steering file, continuing without it",
                run.RunId);
        }

        return Task.FromResult(StepResult.Continue);
    }
}
