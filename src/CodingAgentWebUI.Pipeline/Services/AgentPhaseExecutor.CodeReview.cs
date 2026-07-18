using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Prompts;
using CodingAgentWebUI.Pipeline.Telemetry;

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
                    context.Issue, context.ParsedIssue, Array.Empty<IssueComment>(), context.DownloadedImages);
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

        if (useParallel)
        {
            _logger.Information(
                "Pipeline {RunId} parallel review enabled for {AgentCount} agents (provider={ProviderType})",
                run.RunId, agents.Count, context.AgentProvider.ProviderType);
        }

        var orchestrator = new CodeReviewOrchestrator(_logger);
        await orchestrator.RunReviewLoopAsync(context, agents, maxIterations, skipFixPrompt, useParallel, ct);

        run.CodeReviewIterationInProgress = 0;

        // Generate review summary (non-fatal — failures are logged and skipped)
        await GenerateReviewSummarySafeAsync(context, ct);
    }

    /// <summary>
    /// Determines the fix-prompt action based on skip flag, prompt configuration, and finding counts.
    /// Pure logic — no I/O, no async.
    /// </summary>
    internal static FixPromptDecision DetermineFixPromptAction(
        bool skipFixPrompt,
        string? fixPrompt,
        int iterationCriticalCount,
        string iterationFindingsText)
    {
        if (skipFixPrompt || string.IsNullOrEmpty(fixPrompt))
            return FixPromptDecision.Skip;

        if (iterationCriticalCount > 0)
            return FixPromptDecision.SendFixAndContinue;

        if (!string.IsNullOrEmpty(iterationFindingsText))
            return FixPromptDecision.SendFixAndBreak;

        return FixPromptDecision.NoFindingsBreak;
    }

    /// <summary>Outcome of the fix-prompt decision tree in the code review loop.</summary>
    internal enum FixPromptDecision
    {
        /// <summary>CRITICAL findings exist — send fix prompt and continue to next iteration.</summary>
        SendFixAndContinue,

        /// <summary>Warnings/suggestions only — send fix prompt then exit the review loop.</summary>
        SendFixAndBreak,

        /// <summary>No findings at all — exit the review loop immediately.</summary>
        NoFindingsBreak,

        /// <summary>Fix prompt is skipped (review run) or not configured — continue to next iteration.</summary>
        Skip
    }

    /// <summary>
    /// Generates an AI summary of code review findings (change summary + verdict).
    /// Non-fatal — logs a warning and returns on any failure. Runs regardless of finding counts.
    /// </summary>
    private async Task GenerateReviewSummarySafeAsync(AgentPhaseContext context, CancellationToken ct)
    {
        var run = context.Run;
        var config = context.Config;

        try
        {
            _logger.Information("Pipeline {RunId} generating code review summary", run.RunId);
            context.Callbacks.EmitOutputLine("📝 Generating review summary...");

            // Read diff stat if available
            var diffStat = string.Empty;
            if (run.WorkspacePath is not null)
            {
                var diffStatPath = Path.Combine(run.WorkspacePath, AgentWorkspacePaths.DiffStatFilePath);
                if (File.Exists(diffStatPath))
                {
                    diffStat = await File.ReadAllTextAsync(diffStatPath, ct);
                }
            }

            // Concatenate per-agent findings
            var findings = string.Join(
                Environment.NewLine,
                run.CodeReviewAgentFindings
                    .Where(kv => !string.IsNullOrEmpty(kv.Value))
                    .Select(kv => $"--- Agent: {kv.Key} ---{Environment.NewLine}{kv.Value}"));

            var prompt = PromptBuilder.BuildReviewSummaryPrompt(diffStat, run.IssueTitle, findings);

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

            // Parse the output for ## Change Summary and ## Review Verdict sections
            var output = agentResult.OutputLines.Count > 0
                ? string.Join(Environment.NewLine, agentResult.OutputLines)
                : string.Empty;

            var (changeSummary, verdictSummary) = ReviewSummaryParser.Parse(output);

            if (changeSummary is null && verdictSummary is null)
            {
                _logger.Warning(
                    "Pipeline {RunId} review summary agent produced malformed output (missing headings), skipping summary",
                    run.RunId);
                return;
            }

            run.CodeReviewChangeSummary = changeSummary;
            run.CodeReviewVerdictSummary = verdictSummary;

            _logger.Information(
                "Pipeline {RunId} review summary generated (change={ChangeLen} chars, verdict={VerdictLen} chars)",
                run.RunId, changeSummary?.Length ?? 0, verdictSummary?.Length ?? 0);
            context.Callbacks.EmitOutputLine("✅ Review summary generated");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // Propagate cancellation
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Pipeline {RunId} review summary generation failed (non-fatal), skipping", run.RunId);
        }
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
