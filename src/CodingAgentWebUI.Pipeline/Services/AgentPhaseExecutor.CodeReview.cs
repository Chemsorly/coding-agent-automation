using System.Diagnostics;
using CodingAgentWebUI.Pipeline.CodeReview;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Prompts;
using CodingAgentWebUI.Pipeline.Telemetry;
using Serilog.Context;

namespace CodingAgentWebUI.Pipeline.Services;

public partial class AgentPhaseExecutor
{
    /// <inheritdoc />
    public async Task<string> ExecuteFollowUpAsync(
        AgentPhaseContext context,
        ReviewerConfiguration reviewerConfig,
        string followUpPrompt,
        CancellationToken ct)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(reviewerConfig);

            if (string.IsNullOrEmpty(followUpPrompt))
                return string.Empty;

            var run = context.Run;
            var config = context.Config;

            _logger.Information(
                "Pipeline {RunId} executing follow-up prompt for reviewer '{ReviewerName}' ({PromptLength} chars)",
                run.RunId, reviewerConfig.DisplayName, followUpPrompt.Length);

            // Dispatch as a fresh prompt (no resume) to the agent provider
            var agentResult = await AgentStallMonitor.ExecuteWithMonitoringAsync(
                context.AgentProvider,
                new AgentRequest
                {
                    Prompt = followUpPrompt,
                    WorkspacePath = run.WorkspacePath!,
                    Timeout = config.AgentTimeout,
                    UseResume = false
                },
                run, config, $"Follow-up for reviewer '{reviewerConfig.DisplayName}'",
                context.Callbacks.NotifyChange, _logger, ct,
                line => context.Callbacks.EmitOutputLine(line));

            run.AccumulateTokenUsage(agentResult, phase: $"follow_up_{reviewerConfig.DisplayName}");

            // Collect the agent's response text from output lines
            var responseText = agentResult.OutputLines.Count > 0
                ? string.Join(Environment.NewLine, agentResult.OutputLines)
                : string.Empty;

            _logger.Information(
                "Pipeline {RunId} follow-up for reviewer '{ReviewerName}' completed ({ResponseLength} chars response)",
                run.RunId, reviewerConfig.DisplayName, responseText.Length);

            return responseText;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Propagate cancellation — the caller (PostReviewFindingsStep) handles this
            throw;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex,
                "Pipeline {RunId} follow-up for reviewer '{ReviewerName}' failed, returning empty string",
                context?.Run?.RunId, reviewerConfig?.DisplayName);
            return string.Empty;
        }
    }

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
        if (config.CodeReview.MaxIterations <= 0)
            return;

        // Determine which agents to run — skip review entirely if none resolved (Option B)
        IReadOnlyList<ReviewAgentConfig> agents;
        if (resolvedReviewerConfigs is { Count: > 0 })
        {
            agents = ReviewerResolver.FlattenAgents(resolvedReviewerConfigs);
        }
        else
        {
            return;
        }

        if (agents.Count == 0)
            return;

        // For review runs (PR review pipeline), force single iteration and skip fix prompts.
        // The review pipeline is read-only — it reports findings but never modifies code.
        var maxIterations = run.RunType == PipelineRunType.Review ? 1 : config.CodeReview.MaxIterations;
        var skipFixPrompt = run.RunType == PipelineRunType.Review;

        run.CodeReviewIterationsTotal = maxIterations;

        // Pre-compute diff artifacts so review agents don't need to run git diff themselves.
        // This saves context window space (agents read selectively) and eliminates the first
        // 2-3 tool-call rounds that every review agent would otherwise spend on git commands.
        await PreComputeDiffArtifactsAsync(run, _logger, ct);

        // Write issue context file for review agents (may not exist if analysis phase was skipped)
        try
        {
            var issueContextPath = Path.Combine(run.WorkspacePath!, AgentWorkspacePaths.IssueContextFilePath);
            if (!File.Exists(issueContextPath))
            {
                var issueContextContent = PromptBuilder.BuildIssueContextFileContent(
                    context.Issue, context.ParsedIssue, Array.Empty<IssueComment>());
                await File.WriteAllTextAsync(issueContextPath, issueContextContent, ct);
                _logger.Debug("Pipeline {RunId} wrote issue context to {FilePath} for review agents",
                    run.RunId, AgentWorkspacePaths.IssueContextFilePath);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Pipeline {RunId} failed to write issue context file, review agents may lack issue context", run.RunId);
        }

        // Determine if parallel execution is possible:
        // - Multiple agents (no point parallelizing 1 agent)
        // - Provider supports concurrent execution (SupportsParallelExecution)
        var useParallel = agents.Count > 1
                          && context.AgentProvider.SupportsParallelExecution;

        // Launch acceptance criteria compliance check in parallel with code reviewers.
        // On the first iteration this runs concurrently; results are awaited inside the loop
        // and non-compliant criteria are injected as CRITICAL findings into the fix prompt.
        Task<AgentResult?> acceptanceCriteriaTask = Task.FromResult<AgentResult?>(null);
        var acceptanceCriteriaConsumed = false;
        if (config.AcceptanceCriteriaEnabled)
        {
            acceptanceCriteriaTask = ExecuteAcceptanceCriteriaSafeAsync(context, ct);
        }

        if (useParallel)
        {
            _logger.Information(
                "Pipeline {RunId} parallel review enabled for {AgentCount} agents (provider={ProviderType})",
                run.RunId, agents.Count, context.AgentProvider.ProviderType);
        }

        AgentResult? acAgentResult = null;
        try
        {
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

                    // Await acceptance criteria results (first iteration only — AC doesn't need re-running after fixes)
                    if (!acceptanceCriteriaConsumed && config.AcceptanceCriteriaEnabled)
                    {
                        var acResult = await acceptanceCriteriaTask;
                        acceptanceCriteriaConsumed = true;

                        if (acResult is not null)
                        {
                            run.AccumulateTokenUsage(acResult, phase: "acceptance_criteria");
                            run.AcceptanceCriteriaReport = await AcceptanceCriteriaParser.ParseAsync(
                                run.WorkspacePath!, _logger, ct);

                            // Inject non-compliant criteria as CRITICAL findings so the fix agent addresses them
                            if (run.AcceptanceCriteriaReport is { Criteria.Count: > 0 })
                            {
                                var nonCompliant = run.AcceptanceCriteriaReport.Criteria
                                    .Where(c => c.Status == CriterionStatus.NonCompliant)
                                    .ToList();

                                if (nonCompliant.Count > 0)
                                {
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
                                        iterationCriticalCount++;
                                    }

                                    run.AddCodeReviewCounts(nonCompliant.Count, 0, 0);
                                    iterationFindingsText = iterationFindings.ToString();
                                }
                            }
                        }
                    }

                    // NOTE: [UX-16] CodeReview*Count fields are cumulative across iterations — per-iteration counters deferred to separate issue
                    context.Callbacks.EmitOutputLine($"📝 Code review: {run.CodeReviewCriticalCount} critical, {run.CodeReviewWarningCount} warning, {run.CodeReviewSuggestionCount} suggestion");

                    if (!skipFixPrompt && !string.IsNullOrEmpty(config.CodeReview.FixPrompt) && iterationCriticalCount > 0)
                    {
                        _logger.Information("Pipeline {RunId} code review iteration {Iteration}: {Critical} CRITICAL findings detected across {AgentCount} agent(s), sending fix prompt",
                            run.RunId, i + 1, iterationCriticalCount, agents.Count);
                        context.Callbacks.EmitOutputLine($"📝 Code review: {iterationCriticalCount} critical findings — sending fix prompt");

                        await SendFixPromptAsync(context, run, config, i, iterationFindingsText,
                            $"[Code review fix {i + 1}/{config.CodeReview.MaxIterations}] Applied CRITICAL fixes", ct);
                    }
                    else if (!skipFixPrompt && !string.IsNullOrEmpty(config.CodeReview.FixPrompt) && !string.IsNullOrEmpty(iterationFindingsText))
                    {
                        // No critical findings but warnings/suggestions exist — send fix prompt then exit.
                        // Warnings are actionable (TODO comments per fixPrompt instructions) but don't
                        // warrant another full review cycle since no code logic changes.
                        _logger.Information("Pipeline {RunId} code review iteration {Iteration}: no CRITICAL findings but warnings present, sending fix prompt then exiting review loop",
                            run.RunId, i + 1);
                        context.Callbacks.EmitOutputLine($"📝 Code review: no critical findings, applying warning fixes then completing review");

                        await SendFixPromptAsync(context, run, config, i, iterationFindingsText,
                            $"[Code review fix {i + 1}/{config.CodeReview.MaxIterations}] Applied WARNING fixes (TODO comments)", ct);
                        break;
                    }
                    else if (!skipFixPrompt && !string.IsNullOrEmpty(config.CodeReview.FixPrompt))
                    {
                        _logger.Information("Pipeline {RunId} code review iteration {Iteration}: no findings, exiting review loop",
                            run.RunId, i + 1);

                        // Early exit: no findings at all — re-reviewing won't produce different results.
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

            run.CodeReviewIterationInProgress = 0;

            // Await acceptance criteria task if it wasn't consumed inside the loop
            // (e.g., loop exited on first iteration with no findings before AC completed)
            if (!acceptanceCriteriaConsumed && config.AcceptanceCriteriaEnabled)
            {
                acAgentResult = await acceptanceCriteriaTask;
            }
        }
        finally
        {
            // Ensure the task is observed even if the loop threw (e.g., OperationCanceledException).
            // Awaiting an already-completed task is a no-op.
            try { await acceptanceCriteriaTask.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }

        if (!acceptanceCriteriaConsumed && acAgentResult is not null)
        {
            run.AccumulateTokenUsage(acAgentResult, phase: "acceptance_criteria");
            run.AcceptanceCriteriaReport = await AcceptanceCriteriaParser.ParseAsync(
                run.WorkspacePath!, _logger, ct);
        }

        // Generate review summary (non-fatal) — must be after AC handling, before method returns
        await GenerateReviewSummaryAsync(context, ct);
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
        var config = context.Config;
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

            run.AccumulateTokenUsage(result.AgentResult, phase: $"review_{agent.Name}");
            run.AddCodeReviewCounts(result.Severity.Critical, result.Severity.Warning, result.Severity.Suggestion);
            criticalCount += result.Severity.Critical;

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
        var config = context.Config;

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

            run.AccumulateTokenUsage(result.AgentResult, phase: $"review_{agent.Name}");
            run.AddCodeReviewCounts(result.Severity.Critical, result.Severity.Warning, result.Severity.Suggestion);
            localCriticalCount += result.Severity.Critical;

            if (!string.IsNullOrEmpty(result.FindingsText))
            {
                if (iterationFindings.Length > 0)
                    iterationFindings.AppendLine($"--- Agent: {agent.Name} ---");
                iterationFindings.AppendLine(result.FindingsText);
                run.CodeReviewAgentFindings[agent.Name] = result.FindingsText;
            }

            _logger.Information(
                "Pipeline {RunId} parallel review agent '{AgentName}' (iteration {Iteration}) completed with exit code {ExitCode}. " +
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
    /// Generates a review summary (change summary + verdict) from the review findings.
    /// Non-fatal: failures are logged and result in null summaries, pipeline continues.
    /// </summary>
    private async Task GenerateReviewSummaryAsync(AgentPhaseContext context, CancellationToken ct)
    {
        try
        {
            var run = context.Run;
            var config = context.Config;

            _logger.Information("Pipeline {RunId} generating review summary", run.RunId);
            context.Callbacks.EmitOutputLine("📝 Generating review summary...");

            // Read diff-stat (proceed with empty if unavailable)
            var diffStat = string.Empty;
            var diffStatPath = Path.Combine(run.WorkspacePath!, AgentWorkspacePaths.DiffStatFilePath);
            if (File.Exists(diffStatPath))
            {
                diffStat = await File.ReadAllTextAsync(diffStatPath, ct);
            }

            // Concatenate per-agent findings (cap at 8000 chars)
            // TODO: [REV-04] Double truncation — this caps at 8000 chars and BuildReviewSummaryPrompt also caps at 8000.
            //   When the first truncation fires, setting Length=8000 then appending "\n[truncated]" makes the string ~8012 chars,
            //   which triggers the second truncation in BuildReviewSummaryPrompt and silently loses the "[truncated]" marker.
            //   Consider removing one truncation point or adjusting the cap here to account for the marker length.
            // TODO: [REV-04] Setting StringBuilder.Length directly may cut a multi-byte UTF-16 surrogate pair in half
            //   if agent names or findings contain supplementary Unicode characters (e.g., emoji). Low probability but
            //   could cause garbled text in the prompt. Consider finding a safe boundary before truncating.
            var findingsBuilder = new System.Text.StringBuilder();
            foreach (var (agent, findings) in run.CodeReviewAgentFindings)
            {
                if (string.IsNullOrEmpty(findings)) continue;
                findingsBuilder.AppendLine($"### {agent}");
                findingsBuilder.AppendLine(findings);
                findingsBuilder.AppendLine();

                if (findingsBuilder.Length > 8000)
                {
                    findingsBuilder.Length = 8000;
                    findingsBuilder.AppendLine("\n[truncated]");
                    break;
                }
            }

            var issueTitle = context.Issue.Title ?? string.Empty;
            var prompt = PromptBuilder.BuildReviewSummaryPrompt(diffStat, issueTitle, findingsBuilder.ToString());

            var agentResult = await AgentStallMonitor.ExecuteWithMonitoringAsync(
                context.AgentProvider,
                new AgentRequest
                {
                    Prompt = prompt,
                    WorkspacePath = run.WorkspacePath!,
                    Timeout = config.AgentTimeout,
                    UseResume = false
                },
                run, config, "Review summary generation",
                context.Callbacks.NotifyChange, _logger, ct,
                line => context.Callbacks.EmitOutputLine($"[ReviewSummary] {line}"));

            run.AccumulateTokenUsage(agentResult, phase: "review_summary");

            // Parse the output from agent output lines
            var output = agentResult.OutputLines.Count > 0
                ? string.Join(Environment.NewLine, agentResult.OutputLines)
                : string.Empty;

            var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);
            run.CodeReviewChangeSummary = changeSummary;
            run.CodeReviewVerdictSummary = verdictSummary;

            if (changeSummary is null && verdictSummary is null)
            {
                _logger.Warning("Pipeline {RunId} review summary agent produced output but no valid sections found", run.RunId);
            }
            else
            {
                _logger.Information("Pipeline {RunId} review summary generated (changeSummary={HasChange}, verdict={HasVerdict})",
                    run.RunId, changeSummary is not null, verdictSummary is not null);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Propagate cancellation
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Pipeline {RunId} review summary generation failed, skipping", context.Run.RunId);
            context.Run.CodeReviewChangeSummary = null;
            context.Run.CodeReviewVerdictSummary = null;
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

        var reviewPrompt = PromptBuilder.BuildReviewPrompt(agent.Prompt, context.Issue, context.ParsedIssue, agentFindingsRelativePath, inlineCommentsEnabled: config.CodeReview.InlineComments.Enabled, hasLinkedPr: run.LinkedPullRequest is not null);
        _logger.Debug("Pipeline {RunId} review prompt (iteration {Iteration}, agent '{AgentName}'):\n{Prompt}", run.RunId, iterationIndex + 1, agent.Name, reviewPrompt);
        activity?.SetTag("pipeline.prompt_length_chars", reviewPrompt.Length);

        var reviewResult = await AgentStallMonitor.ExecuteWithMonitoringAsync(
            context.AgentProvider,
            new AgentRequest
            {
                Prompt = reviewPrompt,
                WorkspacePath = run.WorkspacePath!,
                Timeout = config.AgentTimeout,
                UseResume = false
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

    /// <summary>
    /// Pre-computes git diff artifacts (stat + full diff) and writes them to the workspace
    /// so review agents can read them selectively instead of running git diff themselves.
    /// This reduces context window usage and eliminates initial tool-call rounds.
    /// For review runs, diffs against the PR's target branch instead of origin/main.
    /// </summary>
    internal static async Task PreComputeDiffArtifactsAsync(PipelineRun run, Serilog.ILogger logger, CancellationToken ct)
    {
        if (run.WorkspacePath is null) return;

        // Guard: skip if workspace is not a git repository to avoid spawning processes in non-git contexts
        if (!Directory.Exists(Path.Combine(run.WorkspacePath, ".git")))
        {
            logger.Debug("Pipeline {RunId} workspace is not a git repository, skipping diff pre-computation", run.RunId);
            return;
        }

        var agentDir = Path.Combine(run.WorkspacePath, AgentWorkspacePaths.MetadataDirectory);
        Directory.CreateDirectory(agentDir);

        // Use the PR's target branch for review runs, otherwise default to origin/main
        var diffBase = run.RunType == PipelineRunType.Review && !string.IsNullOrEmpty(run.ReviewPrTargetBranch)
            ? $"origin/{run.ReviewPrTargetBranch}"
            : "origin/main";

        // Validate branch name to prevent git argument injection
        if (run.RunType == PipelineRunType.Review && run.ReviewPrTargetBranch != null &&
            (run.ReviewPrTargetBranch.StartsWith("-") ||
             !System.Text.RegularExpressions.Regex.IsMatch(run.ReviewPrTargetBranch, @"^[a-zA-Z0-9._/\-]+$")))
        {
            logger.Warning("Pipeline {RunId} has unsafe ReviewPrTargetBranch '{Branch}', falling back to origin/main",
                run.RunId, run.ReviewPrTargetBranch);
            diffBase = "origin/main";
        }

        try
        {
            // Mark untracked files as intent-to-add so they appear in git diff.
            // Respects .gitignore by default. No-op if no untracked files exist.
            await RunGitCommandAsync(run.WorkspacePath, "add -N .", ct);

            // Compute merge-base to align local diff with GitLab's MR diff view.
            // GitLab uses a merge-base (three-dot) diff for MR changes, while a plain
            // `git diff origin/target` is a two-dot diff that may include commits merged
            // into the target branch after this branch was created. Using merge-base
            // ensures we only see changes introduced by this branch.
            string effectiveDiffBase;
            try
            {
                var mergeBase = await RunGitCommandAsync(run.WorkspacePath, $"merge-base {diffBase} HEAD", ct);
                effectiveDiffBase = mergeBase.Trim();
                if (string.IsNullOrEmpty(effectiveDiffBase))
                    effectiveDiffBase = diffBase;
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not TimeoutException)
            {
                // If merge-base fails (e.g., unrelated histories), fall back to direct diff
                logger.Debug("Pipeline {RunId} merge-base computation failed, falling back to direct diff against {DiffBase}: {Error}", run.RunId, diffBase, ex.Message);
                effectiveDiffBase = diffBase;
            }

            // Generate diff stat (compact file list with line counts)
            var diffStatResult = await RunGitCommandAsync(run.WorkspacePath, $"diff --stat {effectiveDiffBase}", ct);
            var diffStatPath = Path.Combine(run.WorkspacePath, AgentWorkspacePaths.DiffStatFilePath);
            await File.WriteAllTextAsync(diffStatPath, diffStatResult, ct);
            logger.Debug("Pipeline {RunId} wrote diff stat to {FilePath} ({Length} chars, base: {DiffBase})",
                run.RunId, AgentWorkspacePaths.DiffStatFilePath, diffStatResult.Length, effectiveDiffBase);

            // Generate full diff
            var fullDiffResult = await RunGitCommandAsync(run.WorkspacePath, $"diff {effectiveDiffBase}", ct);
            var fullDiffPath = Path.Combine(run.WorkspacePath, AgentWorkspacePaths.FullDiffFilePath);
            await File.WriteAllTextAsync(fullDiffPath, fullDiffResult, ct);
            logger.Debug("Pipeline {RunId} wrote full diff to {FilePath} ({Length} chars, base: {DiffBase})",
                run.RunId, AgentWorkspacePaths.FullDiffFilePath, fullDiffResult.Length, effectiveDiffBase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning(ex, "Pipeline {RunId} failed to pre-compute diff artifacts, review agents will fall back to running git diff", run.RunId);
        }
        finally
        {
            // Reset intent-to-add entries to restore clean index state.
            // RunGitCommandAsync doesn't throw on non-zero exit codes — this catch
            // handles TimeoutException (30s timeout) only.
            try
            {
                await RunGitCommandAsync(run.WorkspacePath, "reset", ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.Warning(ex, "Pipeline {RunId} git reset after diff pre-computation failed (non-fatal, index may have stale ITA entries)", run.RunId);
            }
        }
    }

    private static Task<string> RunGitCommandAsync(string workingDirectory, string arguments, CancellationToken ct)
        => GitProcessRunner.RunAsync(workingDirectory, arguments, ct, throwOnNonZeroExit: false);
}
