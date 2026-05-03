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
        context.Callbacks.TransitionTo(PipelineStep.GeneratingCode);
        try
        {
            var brainContextWritten = await WriteBrainContextIfNeededAsync(run, ct);

            var brainWriteInstructions = PromptBuilder.BuildBrainWriteInstructions(
                run.BrainContextLoaded, run.RunId, run.IssueIdentifier, config.BrainReadOnly);

            var prompt = promptOverride
                ?? PromptBuilder.BuildPrompt(config.ImplementationPrompt, context.Issue, context.ParsedIssue, brainWriteInstructions, brainContextWritten);
            _logger.Debug("Pipeline {RunId} implementation prompt:\n{Prompt}", run.RunId, prompt);

            AgentResult agentResult;
            agentResult = await AgentStallMonitor.ExecuteWithMonitoringAsync(
                context.AgentProvider,
                new AgentRequest
                {
                    Prompt = prompt,
                    WorkspacePath = run.WorkspacePath!,
                    Timeout = config.AgentTimeout,
                    UseResume = true
                },
                run, config, "Code generation agent", context.Callbacks.NotifyChange, _logger, ct,
                line =>
                {
                    run.OutputLines.Enqueue(line);
                    context.Callbacks.EmitOutputLine(line);
                });

            var outputSummary = agentResult.OutputLines.Count > 0
                ? string.Join(Environment.NewLine, agentResult.OutputLines.TakeLast(10))
                : "(no output)";

            run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.Agent, Content = outputSummary });

            _logger.Information("Pipeline {RunId} initial code generation completed with exit code {ExitCode} after {Elapsed}",
                run.RunId, agentResult.ExitCode, DateTime.UtcNow - run.StartedAt);

            await context.Callbacks.UpdateFileChangeStats(run);

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
                    return await FailPhaseAsync(run,
                        $"Agent timed out after {config.AgentTimeout}. Implementation is incomplete.",
                        AgentLabels.Error, PipelineStep.Failed, context.IssueOps, context.Callbacks, ct);
                }
            }
        }
        catch (OperationCanceledException) when (context.OrchestratorCts?.IsCancellationRequested == true)
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
            return await FailPhaseAsync(run,
                $"Agent timed out after {config.AgentTimeout}. Implementation is incomplete.",
                AgentLabels.Error, PipelineStep.Failed, context.IssueOps, context.Callbacks, ct);
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
