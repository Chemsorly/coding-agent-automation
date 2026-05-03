using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

internal partial class AgentExecutionOrchestrator
{
    /// <summary>
    /// Executes the code generation phase with stall monitoring.
    /// Returns true if the pipeline should continue to quality gates, false if it should stop (already failed).
    /// </summary>
    public async Task<bool> ExecuteCodeGenerationAsync(
        AgentPhaseContext context,
        CancellationToken ct,
        string? promptOverride = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var run = context.Run;
        var config = context.Config;
        var agentProvider = context.AgentProvider;
        var issue = context.Issue;
        var parsed = context.ParsedIssue;
        var orchestratorCts = context.OrchestratorCts;
        Action<PipelineStep> transitionTo = context.Callbacks.TransitionTo;
        Action<string> onOutputLine = context.Callbacks.EmitOutputLine;
        Action onChange = context.Callbacks.NotifyChange;
        Func<PipelineRun, Task> updateFileChangeStats = context.Callbacks.UpdateFileChangeStats;
        var issueOps = context.IssueOps;
        Action<PipelineRun> addRunToHistory = context.Callbacks.AddRunToHistory;
        transitionTo(PipelineStep.GeneratingCode);
        try
        {
            var brainContextSection = PromptBuilder.BuildBrainContextSection(
                run.BrainContextLoaded,
                run.RepositoryName?.Split('/').LastOrDefault());

            var brainContextWritten = !string.IsNullOrEmpty(brainContextSection);
            if (brainContextWritten)
            {
                var kiroDir = Path.Combine(run.WorkspacePath!, ".kiro");
                Directory.CreateDirectory(kiroDir);
                await File.WriteAllTextAsync(Path.Combine(run.WorkspacePath!, PromptBuilder.BrainContextFilePath), brainContextSection, ct);
                _logger.Debug("Pipeline {RunId} wrote brain context to {FilePath}", run.RunId, PromptBuilder.BrainContextFilePath);
            }

            var brainWriteInstructions = PromptBuilder.BuildBrainWriteInstructions(
                run.BrainContextLoaded, run.RunId, run.IssueIdentifier, config.BrainReadOnly);

            var prompt = promptOverride
                ?? PromptBuilder.BuildPrompt(config.ImplementationPrompt, issue, parsed, brainWriteInstructions, brainContextWritten);
            _logger.Debug("Pipeline {RunId} implementation prompt:\n{Prompt}", run.RunId, prompt);

            AgentResult agentResult;
            agentResult = await AgentStallMonitor.ExecuteWithMonitoringAsync(
                agentProvider,
                new AgentRequest
                {
                    Prompt = prompt,
                    WorkspacePath = run.WorkspacePath!,
                    Timeout = config.AgentTimeout,
                    UseResume = true
                },
                run, config, "Code generation agent", onChange, _logger, ct,
                line =>
                {
                    run.OutputLines.Enqueue(line);
                    onOutputLine(line);
                });

            var outputSummary = agentResult.OutputLines.Count > 0
                ? string.Join(Environment.NewLine, agentResult.OutputLines.TakeLast(10))
                : "(no output)";

            run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.Agent, Content = outputSummary });

            _logger.Information("Pipeline {RunId} initial code generation completed with exit code {ExitCode} after {Elapsed}",
                run.RunId, agentResult.ExitCode, DateTime.UtcNow - run.StartedAt);

            await updateFileChangeStats(run);

            if (agentResult.ExitCode != ExitCodes.Success)
            {
                _logger.Warning("Pipeline {RunId} agent exited with non-zero code {ExitCode}, continuing to quality gates",
                    run.RunId, agentResult.ExitCode);
                run.ChatHistory.Enqueue(new ChatEntry
                {
                    Role = ChatRole.System,
                    Content = $"Agent process exited with code {agentResult.ExitCode} after {(DateTime.UtcNow - run.StartedAt):hh\\:mm\\:ss}. " +
                              $"Output lines captured: {agentResult.OutputLines.Count}. " +
                              $"The process may have stopped unexpectedly."
                });

                if (agentResult.ExitCode == ExitCodes.Timeout)
                {
                    run.FailureReason = $"Agent timed out after {config.AgentTimeout}. Implementation is incomplete.";
                    run.CompletedAt = DateTime.UtcNow;
                    await issueOps.SwapLabelAsync(run.IssueIdentifier, AgentLabels.Error, ct);
                    transitionTo(PipelineStep.Failed);
                    addRunToHistory(run);
                    return false;
                }
            }
        }
        catch (OperationCanceledException) when (orchestratorCts?.IsCancellationRequested == true)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            _logger.Warning("Pipeline {RunId} agent timed out after {Duration}", run.RunId, config.AgentTimeout);
            run.ChatHistory.Enqueue(new ChatEntry
            {
                Role = ChatRole.System,
                Content = $"Agent timed out after {config.AgentTimeout}"
            });
            run.FailureReason = $"Agent timed out after {config.AgentTimeout}. Implementation is incomplete.";
            run.CompletedAt = DateTime.UtcNow;
            await issueOps.SwapLabelAsync(run.IssueIdentifier, AgentLabels.Error, ct);
            transitionTo(PipelineStep.Failed);
            addRunToHistory(run);
            return false;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Pipeline {RunId} code generation failed, continuing to quality gates", run.RunId);
            run.ChatHistory.Enqueue(new ChatEntry
            {
                Role = ChatRole.System,
                Content = $"Agent process failed: {ex.Message}."
            });
        }

        return true;
    }
}
