using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

internal partial class AgentPhaseExecutor
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
                line =>
                {
                    run.OutputLines.Enqueue(line);
                    context.Callbacks.EmitOutputLine(line);
                });

            run.AccumulateTokenUsage(agentResult);

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

        run.CodeReviewIterationsTotal = config.CodeReview.MaxIterations;

        // For review runs (PR review pipeline), force single iteration and skip fix prompts.
        // The review pipeline is read-only — it reports findings but never modifies code.
        var maxIterations = run.RunType == PipelineRunType.Review ? 1 : config.CodeReview.MaxIterations;
        var skipFixPrompt = run.RunType == PipelineRunType.Review;

        // Pre-compute diff artifacts so review agents don't need to run git diff themselves.
        // This saves context window space (agents read selectively) and eliminates the first
        // 2-3 tool-call rounds that every review agent would otherwise spend on git commands.
        await PreComputeDiffArtifactsAsync(run, _logger, ct);

        for (var i = 0; i < maxIterations; i++)
        {
            run.CodeReviewIterationInProgress = i + 1;
            context.Callbacks.TransitionTo(PipelineStep.ReviewingCode);
            _logger.Information("Pipeline {RunId} starting code review iteration {Iteration}/{MaxIterations}",
                run.RunId, i + 1, maxIterations);

            context.Callbacks.EmitOutputLine($"🔍 Starting code review iteration {i + 1}/{maxIterations} (agents: {string.Join(", ", agents.Select(a => a.Name))})");

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

                    var agentFindingsRelativePath = PromptBuilder.GetReviewFindingsFilePath(agent.Name);
                    var findingsFilePath = Path.Combine(run.WorkspacePath!, agentFindingsRelativePath);
                    if (File.Exists(findingsFilePath))
                        File.Delete(findingsFilePath);

                    var isolated = config.CodeReview.ReviewIsolation == ReviewIsolation.Isolated;
                    var reviewPrompt = PromptBuilder.BuildReviewPrompt(agent.Prompt, context.Issue, context.ParsedIssue, agentFindingsRelativePath, isolated: isolated, inlineCommentsEnabled: config.CodeReview.InlineComments.Enabled);
                    _logger.Debug("Pipeline {RunId} review prompt (iteration {Iteration}, agent '{AgentName}'):\n{Prompt}", run.RunId, i + 1, agent.Name, reviewPrompt);

                    var reviewResult = await AgentStallMonitor.ExecuteWithMonitoringAsync(
                        context.AgentProvider,
                        new AgentRequest
                        {
                            Prompt = reviewPrompt,
                            WorkspacePath = run.WorkspacePath!,
                            Timeout = config.AgentTimeout,
                            UseResume = !isolated
                        },
                        run, config, $"Code review agent '{agent.Name}'", context.Callbacks.NotifyChange, _logger, ct,
                        line =>
                        {
                            run.OutputLines.Enqueue(line);
                            context.Callbacks.EmitOutputLine(line);
                        });

                    run.AccumulateTokenUsage(reviewResult);
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
                                      ? string.Join(Environment.NewLine, reviewResult.OutputLines.TakeLast(PipelineConstants.OutputTailLineCount))
                                      : PipelineConstants.NoOutputFallback)
                    });
                    context.Callbacks.NotifyChange();
                }

                run.CodeReviewAgentsRun = agentsRun;
                var iterationFindingsText = iterationFindings.ToString();
                run.CodeReviewIterationsCompleted++;

                // NOTE: [UX-16] CodeReview*Count fields are cumulative across iterations — per-iteration counters deferred to separate issue
                context.Callbacks.EmitOutputLine($"📝 Code review: {run.CodeReviewCriticalCount} critical, {run.CodeReviewWarningCount} warning, {run.CodeReviewSuggestionCount} suggestion");

                if (!skipFixPrompt && !string.IsNullOrEmpty(config.CodeReview.FixPrompt) && iterationCriticalCount > 0)
                {
                    _logger.Information("Pipeline {RunId} code review iteration {Iteration}: {Critical} CRITICAL findings detected across {AgentCount} agent(s), sending fix prompt",
                        run.RunId, i + 1, iterationCriticalCount, agents.Count);
                    context.Callbacks.EmitOutputLine($"📝 Code review: {iterationCriticalCount} critical findings — sending fix prompt");

                    // Write concatenated findings from all agents to the file so the fix agent can read it
                    var findingsFileForFix = Path.Combine(run.WorkspacePath!, PromptBuilder.ReviewFindingsFilePath);
                    await File.WriteAllTextAsync(findingsFileForFix, iterationFindingsText, ct);

                    var fixPrompt = PromptBuilder.BuildFixPrompt(config.CodeReview.FixPrompt);
                    _logger.Debug("Pipeline {RunId} fix prompt (iteration {Iteration}):\n{Prompt}", run.RunId, i + 1, fixPrompt);

                    await AgentPhaseExecutor.ExecuteAgentAndRecordAsync(
                        context.AgentProvider, fixPrompt, run, config,
                        $"Code review fix agent (iteration {i + 1})",
                        context.Callbacks, _logger, ct,
                        recordOutputToHistory: false,
                        resumeSessionId: run.CodegenSessionId);

                    run.ChatHistory.Enqueue(new ChatEntry
                    {
                        Role = ChatRole.Agent,
                        Content = $"[Code review fix {i + 1}/{config.CodeReview.MaxIterations}] Applied CRITICAL fixes"
                    });
                    context.Callbacks.NotifyChange();
                }
                else if (!skipFixPrompt && !string.IsNullOrEmpty(config.CodeReview.FixPrompt))
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

    /// <summary>
    /// Pre-computes git diff artifacts (stat + full diff) and writes them to the workspace
    /// so review agents can read them selectively instead of running git diff themselves.
    /// This reduces context window usage and eliminates initial tool-call rounds.
    /// For review runs, diffs against the PR's target branch instead of origin/main.
    /// </summary>
    private static async Task PreComputeDiffArtifactsAsync(PipelineRun run, Serilog.ILogger logger, CancellationToken ct)
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
            // Generate diff stat (compact file list with line counts)
            var diffStatResult = await RunGitCommandAsync(run.WorkspacePath, $"diff --stat {diffBase}", ct);
            var diffStatPath = Path.Combine(run.WorkspacePath, AgentWorkspacePaths.DiffStatFilePath);
            await File.WriteAllTextAsync(diffStatPath, diffStatResult, ct);
            logger.Debug("Pipeline {RunId} wrote diff stat to {FilePath} ({Length} chars, base: {DiffBase})",
                run.RunId, AgentWorkspacePaths.DiffStatFilePath, diffStatResult.Length, diffBase);

            // Generate full diff
            var fullDiffResult = await RunGitCommandAsync(run.WorkspacePath, $"diff {diffBase}", ct);
            var fullDiffPath = Path.Combine(run.WorkspacePath, AgentWorkspacePaths.FullDiffFilePath);
            await File.WriteAllTextAsync(fullDiffPath, fullDiffResult, ct);
            logger.Debug("Pipeline {RunId} wrote full diff to {FilePath} ({Length} chars, base: {DiffBase})",
                run.RunId, AgentWorkspacePaths.FullDiffFilePath, fullDiffResult.Length, diffBase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.Warning(ex, "Pipeline {RunId} failed to pre-compute diff artifacts, review agents will fall back to running git diff", run.RunId);
        }
    }

    private static async Task<string> RunGitCommandAsync(string workingDirectory, string arguments, CancellationToken ct)
    {
        using var process = new System.Diagnostics.Process();
        process.StartInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        // Prevent git from hanging on credential prompts or pager
        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GIT_PAGER"] = "";

        process.Start();

        // Read both stdout and stderr concurrently to avoid pipe buffer deadlocks
        var outputTask = process.StandardOutput.ReadToEndAsync(ct);
        var errorTask = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(30));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Timeout — kill the process
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"git {arguments} timed out after 30 seconds");
        }

        // Await both streams to prevent fire-and-forget task leaks
        var output = await outputTask;
        await errorTask;

        return output;
    }
}
