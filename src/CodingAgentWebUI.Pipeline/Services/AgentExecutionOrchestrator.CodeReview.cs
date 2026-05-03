using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

internal partial class AgentExecutionOrchestrator
{
    /// <summary>
    /// Executes the code review loop with multi-agent support and fix prompts.
    /// </summary>
    public async Task ExecuteCodeReviewAsync(
        AgentPhaseContext context,
        CancellationToken ct,
        IReadOnlyList<ReviewerConfiguration>? resolvedReviewerConfigs = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var run = context.Run;
        var config = context.Config;
        if (!config.CodeReview.Enabled || config.CodeReview.MaxIterations <= 0)
            return;

        run.CodeReviewIterationsTotal = config.CodeReview.MaxIterations;
        for (var i = 0; i < config.CodeReview.MaxIterations; i++)
        {
            run.CodeReviewIterationInProgress = i + 1;
            context.Callbacks.TransitionTo(PipelineStep.ReviewingCode);
            _logger.Information("Pipeline {RunId} starting code review iteration {Iteration}/{MaxIterations}",
                run.RunId, i + 1, config.CodeReview.MaxIterations);

            // Determine which agents to run:
            // 1. resolvedReviewerConfigs (entity-based routing)
            // 2. DefaultReviewAgents fallback
            IReadOnlyList<ReviewAgentConfig> agents;
            if (resolvedReviewerConfigs is { Count: > 0 })
            {
                agents = ReviewerResolver.FlattenAgents(resolvedReviewerConfigs);
            }
            else
            {
                agents = PipelineConfiguration.DefaultReviewAgents;
            }

            context.Callbacks.EmitOutputLine($"🔍 Starting code review iteration {i + 1}/{config.CodeReview.MaxIterations} (agents: {string.Join(", ", agents.Select(a => a.Name))})");

            run.ChatHistory.Enqueue(new ChatEntry
            {
                Role = ChatRole.System,
                Content = $"Code review iteration {i + 1}/{config.CodeReview.MaxIterations} starting..."
            });
            context.Callbacks.NotifyChange();

            var iterationFindings = new System.Text.StringBuilder();
            var iterationCriticalCount = 0;
            var agentsRun = new List<string>();

            try
            {
                for (var a = 0; a < agents.Count; a++)
                {
                    var agent = agents[a];
                    _logger.Information("Pipeline {RunId} iteration {Iteration}: running review agent '{AgentName}' ({AgentIndex}/{AgentCount})",
                        run.RunId, i + 1, agent.Name, a + 1, agents.Count);

                    run.ChatHistory.Enqueue(new ChatEntry
                    {
                        Role = ChatRole.System,
                        Content = $"Review agent '{agent.Name}' ({a + 1}/{agents.Count}) starting..."
                    });
                    context.Callbacks.NotifyChange();

                    var findingsFilePath = Path.Combine(run.WorkspacePath!, PromptBuilder.ReviewFindingsFilePath);
                    if (File.Exists(findingsFilePath))
                        File.Delete(findingsFilePath);

                    var reviewPrompt = PromptBuilder.BuildReviewPrompt(agent.Prompt, context.Issue, context.ParsedIssue);
                    _logger.Debug("Pipeline {RunId} review prompt (iteration {Iteration}, agent '{AgentName}'):\n{Prompt}", run.RunId, i + 1, agent.Name, reviewPrompt);

                    var reviewResult = await AgentStallMonitor.ExecuteWithMonitoringAsync(
                        context.AgentProvider,
                        new AgentRequest
                        {
                            Prompt = reviewPrompt,
                            WorkspacePath = run.WorkspacePath!,
                            Timeout = config.AgentTimeout,
                            UseResume = true
                        },
                        run, config, $"Code review agent '{agent.Name}'", context.Callbacks.NotifyChange, _logger, ct,
                        line =>
                        {
                            run.OutputLines.Enqueue(line);
                            context.Callbacks.EmitOutputLine(line);
                        });

                    agentsRun.Add(agent.Name);

                    string fullReviewText;
                    IReadOnlyList<string> findingsLines;
                    if (File.Exists(findingsFilePath))
                    {
                        fullReviewText = await File.ReadAllTextAsync(findingsFilePath, ct);
                        findingsLines = fullReviewText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                        _logger.Information("Pipeline {RunId} review agent '{AgentName}' wrote findings to {FindingsFile} ({Length} chars)",
                            run.RunId, agent.Name, findingsFilePath, fullReviewText.Length);
                    }
                    else
                    {
                        _logger.Warning("Pipeline {RunId} review agent '{AgentName}' did not write findings file at {FindingsFile}",
                            run.RunId, agent.Name, findingsFilePath);
                        fullReviewText = "";
                        findingsLines = Array.Empty<string>();
                    }

                    var severityCounts = SeverityParser.Parse(findingsLines);
                    Interlocked.Add(ref run.CodeReviewCriticalCount, severityCounts.Critical);
                    Interlocked.Add(ref run.CodeReviewWarningCount, severityCounts.Warning);
                    Interlocked.Add(ref run.CodeReviewSuggestionCount, severityCounts.Suggestion);
                    iterationCriticalCount += severityCounts.Critical;

                    if (!string.IsNullOrEmpty(fullReviewText))
                    {
                        if (iterationFindings.Length > 0)
                            iterationFindings.AppendLine($"--- Agent: {agent.Name} ---");
                        iterationFindings.AppendLine(fullReviewText);
                        run.CodeReviewAgentFindings[agent.Name] = fullReviewText;
                    }

                    _logger.Information(
                        "Pipeline {RunId} review agent '{AgentName}' (iteration {Iteration}) completed with exit code {ExitCode}. " +
                        "CodeReviewFindings: {Critical} critical, {Warning} warning, {Suggestion} suggestion",
                        run.RunId, agent.Name, i + 1, reviewResult.ExitCode,
                        severityCounts.Critical, severityCounts.Warning, severityCounts.Suggestion);

                    run.ChatHistory.Enqueue(new ChatEntry
                    {
                        Role = ChatRole.Agent,
                        Content = $"[Code review {i + 1}/{config.CodeReview.MaxIterations} — {agent.Name}] " +
                                  (reviewResult.OutputLines.Count > 0
                                      ? string.Join(Environment.NewLine, reviewResult.OutputLines.TakeLast(10))
                                      : "(no output)")
                    });
                    context.Callbacks.NotifyChange();
                }

                run.CodeReviewAgentsRun = agentsRun;
                var iterationFindingsText = iterationFindings.ToString();
                run.CodeReviewIterationsCompleted++;

                // NOTE: [UX-16] CodeReview*Count fields are cumulative across iterations — per-iteration counters deferred to separate issue
                context.Callbacks.EmitOutputLine($"📝 Code review: {run.CodeReviewCriticalCount} critical, {run.CodeReviewWarningCount} warning, {run.CodeReviewSuggestionCount} suggestion");

                if (!string.IsNullOrEmpty(config.CodeReview.FixPrompt) && iterationCriticalCount > 0)
                {
                    _logger.Information("Pipeline {RunId} code review iteration {Iteration}: {Critical} CRITICAL findings detected across {AgentCount} agent(s), sending fix prompt",
                        run.RunId, i + 1, iterationCriticalCount, agents.Count);
                    context.Callbacks.EmitOutputLine($"📝 Code review: {iterationCriticalCount} critical findings — sending fix prompt");

                    // Write concatenated findings from all agents to the file so the fix agent can read it
                    var findingsFileForFix = Path.Combine(run.WorkspacePath!, PromptBuilder.ReviewFindingsFilePath);
                    await File.WriteAllTextAsync(findingsFileForFix, iterationFindingsText, ct);

                    var fixPrompt = PromptBuilder.BuildFixPrompt(config.CodeReview.FixPrompt);
                    _logger.Debug("Pipeline {RunId} fix prompt (iteration {Iteration}):\n{Prompt}", run.RunId, i + 1, fixPrompt);

                    await AgentExecutionOrchestrator.ExecuteAgentAndRecordAsync(
                        context.AgentProvider, fixPrompt, run, config,
                        $"Code review fix agent (iteration {i + 1})",
                        context.Callbacks, _logger, ct,
                        recordOutputToHistory: false);

                    run.ChatHistory.Enqueue(new ChatEntry
                    {
                        Role = ChatRole.Agent,
                        Content = $"[Code review fix {i + 1}/{config.CodeReview.MaxIterations}] Applied CRITICAL fixes"
                    });
                    context.Callbacks.NotifyChange();
                }
                else if (!string.IsNullOrEmpty(config.CodeReview.FixPrompt))
                {
                    _logger.Information("Pipeline {RunId} code review iteration {Iteration}: no CRITICAL findings, skipping fix prompt",
                        run.RunId, i + 1);
                }
            }
            catch (OperationCanceledException) when (context.OrchestratorCts?.IsCancellationRequested == true)
            {
                run.CodeReviewAgentsRun = agentsRun;
                throw;
            }
            catch (Exception ex)
            {
                run.CodeReviewAgentsRun = agentsRun;
                _logger.Warning(ex, "Pipeline {RunId} code review iteration {Iteration} failed, skipping remaining reviews",
                    run.RunId, i + 1);
                run.ChatHistory.Enqueue(new ChatEntry
                {
                    Role = ChatRole.System,
                    Content = $"Code review iteration {i + 1} failed: {ex.Message}"
                });
                context.Callbacks.NotifyChange();
                break;
            }
        }

        run.CodeReviewIterationInProgress = 0;
    }
}
