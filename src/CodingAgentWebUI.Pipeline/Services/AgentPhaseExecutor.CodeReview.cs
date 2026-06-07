using CodingAgentWebUI.Pipeline.CodeReview;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Prompts;

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
                line => context.Callbacks.EmitOutputLine(line));

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
        // - Isolated mode (no session sharing)
        // - Multiple agents (no point parallelizing 1 agent)
        // - Provider supports concurrent execution (SupportsParallelExecution)
        var isolated = config.CodeReview.ReviewIsolation == ReviewIsolation.Isolated;
        var useParallel = isolated
                          && agents.Count > 1
                          && context.AgentProvider.SupportsParallelExecution;

        if (useParallel)
        {
            _logger.Information(
                "Pipeline {RunId} parallel review enabled for {AgentCount} agents (provider={ProviderType})",
                run.RunId, agents.Count, context.AgentProvider.ProviderType);
        }

        for (var i = 0; i < maxIterations; i++)
        {
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
                        context, agents, i, isolated,
                        iterationFindings, agentsRun, ct);
                }
                else
                {
                    iterationCriticalCount = await ExecuteReviewAgentsSequentialAsync(
                        context, agents, i, isolated,
                        iterationFindings, agentsRun, ct);
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
                    var findingsFileForFix = Path.Combine(run.WorkspacePath!, AgentWorkspacePaths.ReviewFindingsFilePath);
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
    /// Executes review agents sequentially (original behavior).
    /// Used for Kiro CLI or when parallel review is disabled.
    /// Returns the number of critical findings in this iteration.
    /// </summary>
    private async Task<int> ExecuteReviewAgentsSequentialAsync(
        AgentPhaseContext context,
        IReadOnlyList<ReviewAgentConfig> agents,
        int iterationIndex,
        bool isolated,
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

            var result = await ExecuteSingleReviewAgentAsync(context, agent, iterationIndex, isolated, ct);
            agentsRun.Add(agent.Name);

            run.AccumulateTokenUsage(result.AgentResult);
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
        bool isolated,
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

        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Launch all agents concurrently
        var tasks = agents.Select(agent => ExecuteSingleReviewAgentSafeAsync(context, agent, iterationIndex, isolated, ct)).ToList();
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

            run.AccumulateTokenUsage(result.AgentResult);
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
    /// Wraps <see cref="ExecuteSingleReviewAgentAsync"/> with exception handling for parallel execution.
    /// Returns a failed result instead of throwing, so one agent failure doesn't cancel others.
    /// </summary>
    private async Task<ReviewAgentResult> ExecuteSingleReviewAgentSafeAsync(
        AgentPhaseContext context, ReviewAgentConfig agent, int iterationIndex, bool isolated, CancellationToken ct)
    {
        try
        {
            return await ExecuteSingleReviewAgentAsync(context, agent, iterationIndex, isolated, ct);
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
        AgentPhaseContext context, ReviewAgentConfig agent, int iterationIndex, bool isolated, CancellationToken ct)
    {
        var run = context.Run;
        var config = context.Config;

        _logger.Information("Pipeline {RunId} iteration {Iteration}: starting review agent '{AgentName}' (isolated={Isolated})",
            run.RunId, iterationIndex + 1, agent.Name, isolated);

        var agentFindingsRelativePath = AgentWorkspacePaths.GetReviewFindingsFilePath(agent.Name);
        var findingsFilePath = Path.Combine(run.WorkspacePath!, agentFindingsRelativePath);
        if (File.Exists(findingsFilePath))
            File.Delete(findingsFilePath);

        var reviewPrompt = PromptBuilder.BuildReviewPrompt(agent.Prompt, context.Issue, context.ParsedIssue, agentFindingsRelativePath, isolated: isolated, inlineCommentsEnabled: config.CodeReview.InlineComments.Enabled);
        _logger.Debug("Pipeline {RunId} review prompt (iteration {Iteration}, agent '{AgentName}'):\n{Prompt}", run.RunId, iterationIndex + 1, agent.Name, reviewPrompt);

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
            line => context.Callbacks.EmitOutputLine($"[{agent.Name}] {line}"));

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
