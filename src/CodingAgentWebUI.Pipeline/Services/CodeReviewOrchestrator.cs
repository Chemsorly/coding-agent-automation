using System.Diagnostics;
using CodingAgentWebUI.Pipeline.CodeReview;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Prompts;
using CodingAgentWebUI.Pipeline.Telemetry;
using Serilog.Context;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Orchestrates the code review iteration loop: dispatches review agents (sequential or parallel),
/// merges findings, runs acceptance criteria checks, and sends fix prompts.
/// Extracted from <see cref="AgentPhaseExecutor"/> to reduce nesting and eliminate goto patterns.
/// </summary>
// TODO: Change to `internal class` — this class is only instantiated by AgentPhaseExecutor and should not be part of the public API surface
public class CodeReviewOrchestrator
{
    private readonly Serilog.ILogger _logger;

    public CodeReviewOrchestrator(Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Runs the review iteration loop. Each iteration dispatches review agents, collects findings,
    /// and decides whether to send a fix prompt and continue, exit early, or skip.
    /// </summary>
    /// <remarks>
    /// The method returns when the loop exits — either all iterations are exhausted, or an early-exit
    /// decision is made (no findings, or warnings-only with fix prompt sent). This replaces the
    /// <c>goto exitLoop</c> pattern that was previously used in the inlined loop.
    /// </remarks>
    // TODO: Add ArgumentNullException.ThrowIfNull for `context` and `agents` parameters to match constructor validation pattern
    public async Task RunReviewLoopAsync(
        AgentPhaseContext context,
        IReadOnlyList<ReviewAgentConfig> agents,
        int maxIterations,
        bool skipFixPrompt,
        bool useParallel,
        CancellationToken ct)
    {
        var run = context.Run;
        var config = context.Config;

        for (var i = 0; i < maxIterations; i++)
        {
            using var iterationActivity = PipelineTelemetry.ActivitySource.StartActivity("CodeReview.Iteration");
            iterationActivity?.SetTag("pipeline.run_id", run.RunId);
            iterationActivity?.SetTag("pipeline.issue", run.IssueIdentifier);
            iterationActivity?.SetTag("code_review.iteration", i + 1);
            iterationActivity?.SetTag("code_review.max_iterations", maxIterations);
            iterationActivity?.SetTag("code_review.parallel", useParallel);

            run.CodeReviewIterationInProgress = i + 1;
            context.Callbacks.TransitionTo(PipelineStep.ReviewingCode);
            _logger.Information("Pipeline {RunId} starting code review iteration {Iteration}/{MaxIterations}",
                run.RunId, i + 1, maxIterations);

            context.Callbacks.EmitOutputLine(useParallel
                ? $"🔍 Starting code review iteration {i + 1}/{maxIterations} — parallel (agents: {string.Join(", ", agents.Select(a => a.Name))})"
                : $"🔍 Starting code review iteration {i + 1}/{maxIterations} (agents: {string.Join(", ", agents.Select(a => a.Name))})");

            run.ChatHistory.Enqueue(new ChatEntry
            {
                Role = ChatRole.System,
                Content = $"Code review iteration {i + 1}/{config.CodeReview.MaxIterations} starting{(useParallel ? " (parallel)" : "")}..."
            });
            context.Callbacks.NotifyChange();

            // Re-compute diff artifacts every iteration (fix agent may have committed since last iteration)
            await AgentPhaseExecutor.PreComputeDiffArtifactsAsync(run, _logger, ct);

            // Delete consolidated findings from prior iteration (hygiene — prevents accidental prior-finding influence)
            var consolidatedFindingsPath = Path.Combine(run.WorkspacePath!, AgentWorkspacePaths.ReviewFindingsFilePath);
            if (File.Exists(consolidatedFindingsPath))
                File.Delete(consolidatedFindingsPath);

            var iterationFindings = new System.Text.StringBuilder();
            var iterationCriticalCount = 0;
            var agentsRun = new List<string>();

            try
            {
                if (useParallel)
                {
                    iterationCriticalCount = await ExecuteReviewAgentsParallelAsync(
                        context, agents, i,
                        iterationFindings, agentsRun, ct);
                }
                else
                {
                    iterationCriticalCount = await ExecuteReviewAgentsSequentialAsync(
                        context, agents, i,
                        iterationFindings, agentsRun, ct);
                }

                run.CodeReviewAgentsRun = agentsRun;
                var iterationFindingsText = iterationFindings.ToString();
                run.CodeReviewIterationsCompleted++;

                // Run acceptance criteria check on every iteration so the report reflects current code state
                if (config.AcceptanceCriteriaEnabled)
                {
                    iterationCriticalCount += await InjectAcceptanceCriteriaFindingsAsync(context, iterationFindings, ct);
                    iterationFindingsText = iterationFindings.ToString();
                }

                // NOTE: [UX-16] CodeReview*Count fields are cumulative across iterations — per-iteration counters deferred to separate issue
                context.Callbacks.EmitOutputLine($"📝 Code review: {run.CodeReviewCriticalCount} critical, {run.CodeReviewWarningCount} warning, {run.CodeReviewSuggestionCount} suggestion");

                var decision = AgentPhaseExecutor.DetermineFixPromptAction(skipFixPrompt, config.CodeReview.FixPrompt, iterationCriticalCount, iterationFindingsText);
                switch (decision)
                {
                    case AgentPhaseExecutor.FixPromptDecision.SendFixAndContinue:
                        _logger.Information("Pipeline {RunId} code review iteration {Iteration}: {Critical} CRITICAL findings detected across {AgentCount} agent(s), sending fix prompt",
                            run.RunId, i + 1, iterationCriticalCount, agents.Count);
                        context.Callbacks.EmitOutputLine($"📝 Code review: {iterationCriticalCount} critical findings — sending fix prompt");

                        await SendFixPromptAsync(context, run, config, i, iterationFindingsText,
                            $"[Code review fix {i + 1}/{config.CodeReview.MaxIterations}] Applied CRITICAL fixes", ct);
                        break;

                    case AgentPhaseExecutor.FixPromptDecision.SendFixAndBreak:
                        // No critical findings but warnings/suggestions exist — send fix prompt then exit.
                        // Warnings are actionable (TODO comments per fixPrompt instructions) but don't
                        // warrant another full review cycle since no code logic changes.
                        _logger.Information("Pipeline {RunId} code review iteration {Iteration}: no CRITICAL findings but warnings present, sending fix prompt then exiting review loop",
                            run.RunId, i + 1);
                        context.Callbacks.EmitOutputLine($"📝 Code review: no critical findings, applying warning fixes then completing review");

                        await SendFixPromptAsync(context, run, config, i, iterationFindingsText,
                            $"[Code review fix {i + 1}/{config.CodeReview.MaxIterations}] Applied WARNING fixes (TODO comments)", ct);
                        return; // Exit the loop — replaces goto exitLoop

                    case AgentPhaseExecutor.FixPromptDecision.NoFindingsBreak:
                        _logger.Information("Pipeline {RunId} code review iteration {Iteration}: no findings, exiting review loop",
                            run.RunId, i + 1);

                        // Early exit: no findings at all — re-reviewing won't produce different results.
                        return; // Exit the loop — replaces goto exitLoop

                    case AgentPhaseExecutor.FixPromptDecision.Skip:
                        // skipFixPrompt=true or no FixPrompt configured — continue to next iteration
                        break;
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
    }

    /// <summary>
    /// Runs acceptance criteria check and injects non-compliant criteria as CRITICAL findings.
    /// Returns the number of additional critical findings injected.
    /// </summary>
    // TODO: Add targeted unit tests for InjectAcceptanceCriteriaFindingsAsync branching logic
    // (acResult null, no criteria, no non-compliant criteria). Currently only covered at integration level.
    private async Task<int> InjectAcceptanceCriteriaFindingsAsync(
        AgentPhaseContext context,
        System.Text.StringBuilder iterationFindings,
        CancellationToken ct)
    {
        var run = context.Run;

        var acResult = await ExecuteAcceptanceCriteriaSafeAsync(context, ct);
        if (acResult is null)
            return 0;

        run.AccumulateTokenUsage(acResult, phase: "acceptance_criteria");
        run.AcceptanceCriteriaReport = await AcceptanceCriteriaParser.ParseAsync(
            run.WorkspacePath!, _logger, ct) ?? run.AcceptanceCriteriaReport;

        // Inject non-compliant criteria as CRITICAL findings so the fix agent addresses them
        if (run.AcceptanceCriteriaReport is not { Criteria.Count: > 0 })
            return 0;

        var nonCompliant = run.AcceptanceCriteriaReport.Criteria
            .Where(c => c.Status == CriterionStatus.NonCompliant)
            .ToList();

        if (nonCompliant.Count == 0)
            return 0;

        _logger.Information(
            "Pipeline {RunId} injecting {Count} non-compliant acceptance criteria as CRITICAL findings",
            run.RunId, nonCompliant.Count);
        context.Callbacks.EmitOutputLine($"📋 Acceptance criteria: {nonCompliant.Count} non-compliant → injected as CRITICAL");

        if (iterationFindings.Length > 0)
            iterationFindings.AppendLine("--- Agent: AcceptanceCriteria ---");

        foreach (var criterion in nonCompliant)
        {
            var reasoning = criterion.Reasoning ?? "No reasoning provided";
            iterationFindings.AppendLine($"[CRITICAL] — Acceptance criterion not met: \"{criterion.Criterion}\". {reasoning}");
        }

        run.AddCodeReviewCounts(nonCompliant.Count, 0, 0);
        return nonCompliant.Count;
    }

    /// <summary>
    /// Writes review findings to the workspace file, builds and executes the fix prompt,
    /// and records the fix action to chat history.
    /// </summary>
    private async Task SendFixPromptAsync(
        AgentPhaseContext context,
        PipelineRun run,
        PipelineConfiguration config,
        int iterationIndex,
        string iterationFindingsText,
        string fixDescription,
        CancellationToken ct)
    {
        // Write concatenated findings from all agents to the file so the fix agent can read it
        var findingsFileForFix = Path.Combine(run.WorkspacePath!, AgentWorkspacePaths.ReviewFindingsFilePath);
        await File.WriteAllTextAsync(findingsFileForFix, iterationFindingsText, ct);

        var fixPrompt = PromptBuilder.BuildFixPrompt(config.CodeReview.FixPrompt!);
        _logger.Debug("Pipeline {RunId} fix prompt (iteration {Iteration}):\n{Prompt}", run.RunId, iterationIndex + 1, fixPrompt);

        await AgentPhaseExecutor.ExecuteAgentAndRecordAsync(
            context.AgentProvider, fixPrompt, run, config,
            $"Code review fix agent (iteration {iterationIndex + 1})",
            context.Callbacks, _logger, ct,
            recordOutputToHistory: false,
            resumeSessionId: run.CodegenSessionId,
            phase: "fix");

        run.ChatHistory.Enqueue(new ChatEntry
        {
            Role = ChatRole.Agent,
            Content = fixDescription
        });
        context.Callbacks.NotifyChange();
    }

    /// <summary>
    /// Merges a single successful review agent result into the shared accumulation state:
    /// token usage, severity counts, findings text, agent findings dictionary, log, and chat history.
    /// Returns the agent's critical finding count for caller accumulation.
    /// </summary>
    /// <remarks>
    /// Callers are responsible for <c>agentsRun.Add</c> (placement differs between sequential/parallel)
    /// and <c>context.Callbacks.NotifyChange()</c> (per-agent in sequential, once-after-loop in parallel).
    /// </remarks>
    private int MergeReviewAgentResult(
        AgentPhaseContext context,
        ReviewAgentConfig agent,
        ReviewAgentResult result,
        int iterationIndex,
        System.Text.StringBuilder iterationFindings)
    {
        var run = context.Run;
        var config = context.Config;

        run.AccumulateTokenUsage(result.AgentResult, phase: $"review_{agent.Name}");
        run.AddCodeReviewCounts(result.Severity.Critical, result.Severity.Warning, result.Severity.Suggestion);

        if (!string.IsNullOrEmpty(result.FindingsText))
        {
            if (iterationFindings.Length > 0)
                iterationFindings.AppendLine($"--- Agent: {agent.Name} ---");
            iterationFindings.AppendLine(result.FindingsText);
            run.CodeReviewAgentFindings[agent.Name] = result.FindingsText;
        }

        _logger.Information(
            "Pipeline {RunId} review agent '{AgentName}' (iteration {Iteration}) completed with exit code {ExitCode}. " +
            "CodeReviewFindings: {Critical} critical, {Warning} warning, {Suggestion} suggestion",
            run.RunId, agent.Name, iterationIndex + 1, result.AgentResult.ExitCode,
            result.Severity.Critical, result.Severity.Warning, result.Severity.Suggestion);

        run.ChatHistory.Enqueue(new ChatEntry
        {
            Role = ChatRole.Agent,
            Content = $"[Code review {iterationIndex + 1}/{config.CodeReview.MaxIterations} — {agent.Name}] " +
                      (result.AgentResult.OutputLines.Count > 0
                          ? string.Join(Environment.NewLine, result.AgentResult.OutputLines.TakeLast(PipelineConstants.OutputTailLineCount))
                          : PipelineConstants.NoOutputFallback)
        });

        return result.Severity.Critical;
    }

    /// <summary>
    /// Executes review agents sequentially (original behavior).
    /// Used for Kiro CLI or when parallel review is disabled.
    /// Returns the number of critical findings in this iteration.
    /// </summary>
    private async Task<int> ExecuteReviewAgentsSequentialAsync(
        AgentPhaseContext context,
        IReadOnlyList<ReviewAgentConfig> agents,
        int iterationIndex,
        System.Text.StringBuilder iterationFindings,
        List<string> agentsRun,
        CancellationToken ct)
    {
        var run = context.Run;
        var criticalCount = 0;

        for (var a = 0; a < agents.Count; a++)
        {
            var agent = agents[a];
            _logger.Information("Pipeline {RunId} iteration {Iteration}: running review agent '{AgentName}' ({AgentIndex}/{AgentCount})",
                run.RunId, iterationIndex + 1, agent.Name, a + 1, agents.Count);

            run.ChatHistory.Enqueue(new ChatEntry
            {
                Role = ChatRole.System,
                Content = $"Review agent '{agent.Name}' ({a + 1}/{agents.Count}) starting..."
            });
            context.Callbacks.NotifyChange();

            var result = await ExecuteSingleReviewAgentAsync(context, agent, iterationIndex, ct);
            agentsRun.Add(agent.Name);

            criticalCount += MergeReviewAgentResult(context, agent, result, iterationIndex, iterationFindings);
            context.Callbacks.NotifyChange();
        }

        return criticalCount;
    }

    /// <summary>
    /// Executes review agents in parallel (OpenCode only).
    /// Each agent runs in its own fresh session since UseResume=false creates
    /// independent server-side sessions via the OpenCode HTTP API.
    /// Returns the number of critical findings in this iteration.
    /// </summary>
    private async Task<int> ExecuteReviewAgentsParallelAsync(
        AgentPhaseContext context,
        IReadOnlyList<ReviewAgentConfig> agents,
        int iterationIndex,
        System.Text.StringBuilder iterationFindings,
        List<string> agentsRun,
        CancellationToken ct)
    {
        var run = context.Run;

        _logger.Information(
            "Pipeline {RunId} iteration {Iteration}: launching {AgentCount} review agents in parallel (provider={ProviderType})",
            run.RunId, iterationIndex + 1, agents.Count, context.AgentProvider.ProviderType);
        context.Callbacks.EmitOutputLine($"⚡ Running {agents.Count} review agents in parallel...");

        var sw = Stopwatch.StartNew();

        // Launch all agents concurrently
        var tasks = agents.Select(agent => ExecuteSingleReviewAgentSafeAsync(context, agent, iterationIndex, ct)).ToList();
        var results = await Task.WhenAll(tasks);

        sw.Stop();
        var successCount = results.Count(r => !r.Failed);
        var failedCount = results.Count(r => r.Failed);
        _logger.Information(
            "Pipeline {RunId} iteration {Iteration}: parallel review completed in {Duration}s — {SuccessCount} succeeded, {FailedCount} failed",
            run.RunId, iterationIndex + 1, sw.Elapsed.TotalSeconds.ToString("F1"), successCount, failedCount);

        // Merge results sequentially (deterministic ordering by agent index)
        var localCriticalCount = 0;
        for (var a = 0; a < agents.Count; a++)
        {
            var agent = agents[a];
            var result = results[a];

            agentsRun.Add(agent.Name);

            if (result.Failed)
            {
                _logger.Warning("Pipeline {RunId} parallel review agent '{AgentName}' failed: {Error}",
                    run.RunId, agent.Name, result.Error);
                run.ChatHistory.Enqueue(new ChatEntry
                {
                    Role = ChatRole.System,
                    Content = $"Review agent '{agent.Name}' failed: {result.Error}"
                });
                continue;
            }

            localCriticalCount += MergeReviewAgentResult(context, agent, result, iterationIndex, iterationFindings);
        }

        context.Callbacks.NotifyChange();

        return localCriticalCount;
    }

    /// <summary>
    /// Executes the acceptance criteria compliance agent in isolation.
    /// Returns the AgentResult for token accumulation, or null on failure.
    /// Never throws — failures are logged and result in a null report.
    /// </summary>
    private async Task<AgentResult?> ExecuteAcceptanceCriteriaSafeAsync(
        AgentPhaseContext context, CancellationToken ct)
    {
        try
        {
            var run = context.Run;
            var config = context.Config;

            _logger.Information("Pipeline {RunId} launching acceptance criteria compliance check", run.RunId);
            context.Callbacks.EmitOutputLine("📋 Launching acceptance criteria compliance check...");

            var prompt = PromptBuilder.BuildAcceptanceCriteriaPrompt(config.AcceptanceCriteriaPrompt);

            var agentResult = await AgentStallMonitor.ExecuteWithMonitoringAsync(
                context.AgentProvider,
                new AgentRequest
                {
                    Prompt = prompt,
                    WorkspacePath = run.WorkspacePath!,
                    Timeout = config.AgentTimeout,
                    UseResume = false
                },
                run, config, "Acceptance criteria compliance",
                context.Callbacks.NotifyChange, _logger, ct,
                line => context.Callbacks.EmitOutputLine($"[AcceptanceCriteria] {line}"));

            _logger.Information(
                "Pipeline {RunId} acceptance criteria check completed with exit code {ExitCode}",
                run.RunId, agentResult.ExitCode);

            return agentResult;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Propagate cancellation
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Pipeline {RunId} acceptance criteria check failed, skipping", context.Run.RunId);
            return null;
        }
    }

    /// <summary>
    /// Wraps <see cref="ExecuteSingleReviewAgentAsync"/> with exception handling for parallel execution.
    /// Returns a failed result instead of throwing, so one agent failure doesn't cancel others.
    /// </summary>
    private async Task<ReviewAgentResult> ExecuteSingleReviewAgentSafeAsync(
        AgentPhaseContext context, ReviewAgentConfig agent, int iterationIndex, CancellationToken ct)
    {
        try
        {
            return await ExecuteSingleReviewAgentAsync(context, agent, iterationIndex, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Propagate cancellation — all parallel tasks should stop
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Pipeline {RunId} parallel review agent '{AgentName}' threw exception",
                context.Run.RunId, agent.Name);
            return ReviewAgentResult.Failure(ex.Message);
        }
    }

    /// <summary>
    /// Executes a single review agent and returns its result.
    /// Shared by both sequential and parallel paths.
    /// </summary>
    private async Task<ReviewAgentResult> ExecuteSingleReviewAgentAsync(
        AgentPhaseContext context, ReviewAgentConfig agent, int iterationIndex, CancellationToken ct)
    {
        using var activity = PipelineTelemetry.ActivitySource.StartActivity("CodeReview.Agent");
        activity?.SetTag("pipeline.run_id", context.Run.RunId);
        activity?.SetTag("pipeline.issue", context.Run.IssueIdentifier);
        activity?.SetTag("code_review.agent_name", agent.Name);
        activity?.SetTag("code_review.iteration", iterationIndex + 1);

        using var _logCtxAgent = LogContext.PushProperty("ReviewAgentName", agent.Name);
        using var _logCtxIter = LogContext.PushProperty("ReviewIteration", iterationIndex + 1);

        var run = context.Run;
        var config = context.Config;

        _logger.Information("Pipeline {RunId} iteration {Iteration}: starting review agent '{AgentName}'",
            run.RunId, iterationIndex + 1, agent.Name);

        var agentFindingsRelativePath = AgentWorkspacePaths.GetReviewFindingsFilePath(agent.Name);
        var findingsFilePath = Path.Combine(run.WorkspacePath!, agentFindingsRelativePath);
        if (File.Exists(findingsFilePath))
            File.Delete(findingsFilePath);

        var reviewPrompt = PromptBuilder.BuildReviewPrompt(agent.Prompt, context.Issue, context.ParsedIssue, agentFindingsRelativePath, inlineCommentsEnabled: config.CodeReview.InlineComments.Enabled, hasLinkedPr: run.LinkedPullRequest is not null, imageCount: context.DownloadedImages?.Count ?? 0);
        _logger.Debug("Pipeline {RunId} review prompt (iteration {Iteration}, agent '{AgentName}'):\n{Prompt}", run.RunId, iterationIndex + 1, agent.Name, reviewPrompt);
        activity?.SetTag("pipeline.prompt_length_chars", reviewPrompt.Length);

        var reviewResult = await AgentStallMonitor.ExecuteWithMonitoringAsync(
            context.AgentProvider,
            new AgentRequest
            {
                Prompt = reviewPrompt,
                WorkspacePath = run.WorkspacePath!,
                Timeout = config.AgentTimeout,
                UseResume = false,
                ImagePaths = context.DownloadedImages?.Select(d => d.LocalPath).ToList()
            },
            run, config, $"Code review agent '{agent.Name}'", context.Callbacks.NotifyChange, _logger, ct,
            line => context.Callbacks.EmitOutputLine($"[{agent.Name}] {line}"));

        if (reviewResult.ExitCode != 0)
        {
            var tailLines = reviewResult.OutputLines.TakeLast(20);
            _logger.Warning(
                "Pipeline {RunId} review agent '{AgentName}' (iteration {Iteration}) exited with code {ExitCode}. Last output:\n{Output}",
                run.RunId, agent.Name, iterationIndex + 1, reviewResult.ExitCode,
                string.Join(Environment.NewLine, tailLines));
            activity?.SetStatus(ActivityStatusCode.Error, $"Exit code {reviewResult.ExitCode}");
        }

        activity?.SetTag("code_review.exit_code", reviewResult.ExitCode);

        string findingsText;
        IReadOnlyList<string> findingsLines;
        if (File.Exists(findingsFilePath))
        {
            findingsText = await File.ReadAllTextAsync(findingsFilePath, ct);
            findingsLines = findingsText.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            _logger.Information("Pipeline {RunId} review agent '{AgentName}' wrote findings to {FindingsFile} ({Length} chars)",
                run.RunId, agent.Name, findingsFilePath, findingsText.Length);
        }
        else
        {
            _logger.Warning("Pipeline {RunId} review agent '{AgentName}' did not write findings file at {FindingsFile}",
                run.RunId, agent.Name, findingsFilePath);
            findingsText = "";
            findingsLines = Array.Empty<string>();
        }

        var severityCounts = CodeReview.SeverityParser.Parse(findingsLines);

        activity?.SetTag("code_review.findings_critical", severityCounts.Critical);
        activity?.SetTag("code_review.findings_warning", severityCounts.Warning);
        activity?.SetTag("code_review.findings_suggestion", severityCounts.Suggestion);
        activity?.SetTag("code_review.has_findings_file", File.Exists(findingsFilePath));

        return new ReviewAgentResult
        {
            AgentResult = reviewResult,
            FindingsText = findingsText,
            Severity = severityCounts
        };
    }

    /// <summary>Result of a single review agent execution.</summary>
    private sealed class ReviewAgentResult
    {
        public required AgentResult AgentResult { get; init; }
        public required string FindingsText { get; init; }
        public required SeverityCounts Severity { get; init; }
        public string? Error { get; init; }
        public bool Failed => Error is not null;

        public static ReviewAgentResult Failure(string error) => new()
        {
            AgentResult = new AgentResult { ExitCode = -1, OutputLines = [] },
            FindingsText = "",
            Severity = new SeverityCounts(0, 0, 0),
            Error = error
        };
    }
}
