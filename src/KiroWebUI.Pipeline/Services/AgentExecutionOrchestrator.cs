using System.Text.Json;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Pipeline.Services;

/// <summary>
/// Handles agent execution phases: analysis, code generation (with stall monitoring),
/// and code review iterations. Extracted from PipelineOrchestrationService.
/// </summary>
internal class AgentExecutionOrchestrator
{
    private readonly Serilog.ILogger _logger;

    public AgentExecutionOrchestrator(Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Executes the analysis phase: checks for existing analysis, runs agent analysis if needed,
    /// reads the analysis file, evaluates the confidence gate, and posts the analysis comment.
    /// Returns true if the pipeline should continue to code generation, false if it should stop.
    /// </summary>
    // TODO: [ARC-10] Add ArgumentNullException.ThrowIfNull for public method parameters
    public async Task<bool> ExecuteAnalysisPhaseAsync(
        PipelineRun run, PipelineConfiguration config,
        IAgentProvider agentProvider, IIssueProvider issueProvider,
        IssueDetail issue, ParsedIssue parsed,
        IReadOnlyList<IssueComment> issueComments,
        BrainSyncOrchestrator brainSync,
        Action<PipelineStep> transitionTo,
        Func<string, string, CancellationToken, Task> swapAgentLabel,
        Action<PipelineRun> addRunToHistory,
        Action? onOutputLine, Action? onChange,
        CancellationToken ct)
    {
        string? existingAnalysis = null;
        var analysisComment = issueComments.FirstOrDefault(c => c.Body.Contains("## 🤖 Agent Analysis"));
        var gateRejection = issueComments.FirstOrDefault(c => c.Body.Contains("<!-- agent:gate-rejection -->"));
        var gateWontDo = issueComments.FirstOrDefault(c => c.Body.Contains("<!-- agent:gate-wont-do -->"));

        var latestGateComment = new[] { gateRejection, gateWontDo }
            .Where(c => c != null)
            .OrderByDescending(c => c!.CreatedAt)
            .FirstOrDefault();

        bool forceRefresh = latestGateComment != null
            && (analysisComment == null || latestGateComment.CreatedAt > analysisComment.CreatedAt);

        if (analysisComment != null && !forceRefresh)
        {
            existingAnalysis = analysisComment.Body;
            _logger.Information("Pipeline {RunId} found existing analysis comment on issue {IssueIdentifier}, skipping agent analysis",
                run.RunId, run.IssueIdentifier);
        }

        // Write issue context file before analysis
        // TODO: [AGT-12] Wrap in try/catch for graceful fallback if file write fails (IOException could abort pipeline)
        var kiroDir = Path.Combine(run.WorkspacePath!, ".kiro");
        Directory.CreateDirectory(kiroDir);

        var issueContextContent = PromptBuilder.BuildIssueContextFileContent(issue, parsed, issueComments);
        await File.WriteAllTextAsync(Path.Combine(run.WorkspacePath!, PromptBuilder.IssueContextFilePath), issueContextContent, ct);
        _logger.Debug("Pipeline {RunId} wrote issue context to {FilePath}", run.RunId, PromptBuilder.IssueContextFilePath);

        if (existingAnalysis != null)
        {
            run.AnalysisContent = existingAnalysis;
            run.AnalysisSkipped = true;
            transitionTo(PipelineStep.AnalyzingCode);
            await agentProvider.EnsureSessionAsync(run.WorkspacePath!, ct);
            transitionTo(PipelineStep.PostingAnalysis);
        }
        else
        {
            transitionTo(PipelineStep.AnalyzingCode);
            await agentProvider.EnsureSessionAsync(run.WorkspacePath!, ct);

            try
            {
                var brainContextForAnalysis = PromptBuilder.BuildBrainContextSection(
                    run.BrainContextLoaded,
                    run.RepositoryName?.Split('/').LastOrDefault(),
                    null,
                    brainSync.GetPreviousBrainWarnings(run.BrainProviderConfigId));

                var brainContextWrittenForAnalysis = !string.IsNullOrEmpty(brainContextForAnalysis);
                if (brainContextWrittenForAnalysis)
                {
                    await File.WriteAllTextAsync(Path.Combine(run.WorkspacePath!, PromptBuilder.BrainContextFilePath), brainContextForAnalysis, ct);
                    _logger.Debug("Pipeline {RunId} wrote brain context to {FilePath}", run.RunId, PromptBuilder.BrainContextFilePath);
                }

                var analysisPrompt = PromptBuilder.BuildAnalysisPrompt(config.AnalysisPrompt, issue, parsed, brainContextWrittenForAnalysis);
                _logger.Debug("Pipeline {RunId} analysis prompt:\n{Prompt}", run.RunId, analysisPrompt);

                await agentProvider.ExecuteAsync(
                    new AgentRequest
                    {
                        Prompt = analysisPrompt,
                        WorkspacePath = run.WorkspacePath!,
                        Timeout = config.AgentTimeout,
                        UseResume = true
                    },
                    ct,
                    line =>
                    {
                        run.OutputLines.Enqueue(line);
                        onOutputLine?.Invoke();
                    });

                var analysisFilePath = Path.Combine(run.WorkspacePath!, PromptBuilder.AnalysisFilePath);
                if (File.Exists(analysisFilePath))
                {
                    run.AnalysisContent = await File.ReadAllTextAsync(analysisFilePath, ct);
                    _logger.Information("Pipeline {RunId} read analysis from {AnalysisFilePath}", run.RunId, analysisFilePath);
                }
                else
                {
                    _logger.Warning("Pipeline {RunId} agent did not write analysis file at {AnalysisFilePath}", run.RunId, analysisFilePath);
                    run.AnalysisContent = null;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(ex, "Pipeline {RunId} code analysis failed, falling back to issue-based analysis", run.RunId);
                run.AnalysisContent = null;
            }

            // Read and evaluate the confidence gate assessment
            var assessment = await ReadAssessmentAsync(run, ct);
            run.AnalysisRecommendation = assessment?.Recommendation;
            run.AnalysisConcerns = assessment?.Concerns ?? Array.Empty<string>();
            run.AnalysisBlockingIssues = assessment?.BlockingIssues ?? Array.Empty<string>();

            var isWontDo = assessment != null
                && string.Equals(assessment.Recommendation, "wont_do", StringComparison.OrdinalIgnoreCase);

            // isNotReady is checked first: non-empty blockingIssues forces not_ready regardless of recommendation
            var isNotReady = assessment != null && (
                string.Equals(assessment.Recommendation, "not_ready", StringComparison.OrdinalIgnoreCase)
                || (assessment.BlockingIssues.Count > 0));

            if (isNotReady)
            {
                // Post analysis comment first
                transitionTo(PipelineStep.PostingAnalysis);
                await PostAnalysisCommentAsync(run, issue, parsed, issueProvider, assessment, ct);

                var abortComment = BuildNotReadyComment(assessment!);
                try { await issueProvider.PostCommentAsync(run.IssueIdentifier, abortComment, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { _logger.Warning(ex, "Pipeline {RunId} failed to post not-ready comment", run.RunId); }

                run.FailureReason = $"Analysis gate: needs refinement — {assessment?.Reason ?? "issue not ready"}";
                run.CompletedAt = DateTime.UtcNow;
                await swapAgentLabel(run.IssueIdentifier, AgentLabels.NeedsRefinement, ct);
                transitionTo(PipelineStep.Failed);
                addRunToHistory(run);
                return false;
            }

            if (isWontDo)
            {
                // Post analysis comment first
                transitionTo(PipelineStep.PostingAnalysis);
                await PostAnalysisCommentAsync(run, issue, parsed, issueProvider, assessment, ct);

                var wontDoComment = BuildWontDoComment(assessment!);
                try { await issueProvider.PostCommentAsync(run.IssueIdentifier, wontDoComment, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { _logger.Warning(ex, "Pipeline {RunId} failed to post won't-do comment", run.RunId); }

                run.FailureReason = $"Analysis gate: won't do — {assessment?.Reason ?? "no code changes needed"}";
                run.CompletedAt = DateTime.UtcNow;
                await swapAgentLabel(run.IssueIdentifier, AgentLabels.WontDo, ct);
                transitionTo(PipelineStep.Completed);
                addRunToHistory(run);
                return false;
            }

            // Ready path — post analysis and continue
            transitionTo(PipelineStep.PostingAnalysis);
            await PostAnalysisCommentAsync(run, issue, parsed, issueProvider, assessment, ct);
        }

        return true;
    }

    private async Task<AnalysisAssessment?> ReadAssessmentAsync(PipelineRun run, CancellationToken ct)
    {
        var assessmentPath = Path.Combine(run.WorkspacePath!, PromptBuilder.AnalysisAssessmentFilePath);
        if (!File.Exists(assessmentPath))
        {
            _logger.Warning("Pipeline {RunId} no analysis assessment file found at {Path}, defaulting to proceed",
                run.RunId, assessmentPath);
            return null;
        }

        try
        {
            var json = await File.ReadAllTextAsync(assessmentPath, ct);
            // TODO: [ARC-08a] Extract JsonSerializerOptions to a private static readonly field — per-call allocation bypasses System.Text.Json type metadata cache
            var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
            return JsonSerializer.Deserialize<AnalysisAssessment>(json, options);
        }
        catch (JsonException ex)
        {
            _logger.Warning(ex, "Pipeline {RunId} failed to parse analysis assessment, defaulting to proceed", run.RunId);
            return null;
        }
        // TODO: [ARC-08a] Catch IOException to maintain fail-open contract when file is locked/inaccessible
    }

    private async Task PostAnalysisCommentAsync(
        PipelineRun run, IssueDetail issue, ParsedIssue parsed,
        IIssueProvider issueProvider, AnalysisAssessment? assessment, CancellationToken ct)
    {
        try
        {
            var analysis = !string.IsNullOrWhiteSpace(run.AnalysisContent)
                ? IssueAnalysisComment.FromAgentAnalysis(issue, run.AnalysisContent, assessment)
                : IssueAnalysisComment.FromIssue(issue, parsed);

            await issueProvider.PostCommentAsync(run.IssueIdentifier, analysis.ToMarkdown(), ct);
            _logger.Information("Pipeline {RunId} posted analysis comment on issue {IssueIdentifier}", run.RunId, run.IssueIdentifier);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Warning(ex, "Pipeline {RunId} failed to post analysis comment on issue {IssueIdentifier}", run.RunId, run.IssueIdentifier);
        }
    }

    internal static string BuildNotReadyComment(AnalysisAssessment assessment)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## ⚠️ Analysis Gate: Needs Refinement");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(assessment.Reason))
            sb.AppendLine(assessment.Reason);

        if (assessment.BlockingIssues.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Blocking Issues");
            foreach (var issue in assessment.BlockingIssues)
                sb.AppendLine($"- {issue}");
        }

        if (assessment.Concerns.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Concerns");
            foreach (var concern in assessment.Concerns)
                sb.AppendLine($"- {concern}");
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("*The issue has been labeled `agent:needs-refinement`. Refine the issue description addressing the blocking issues above, then re-apply `agent:next` to retry.*");
        sb.AppendLine();
        sb.AppendLine("<!-- agent:gate-rejection -->");
        return sb.ToString().TrimEnd();
    }

    internal static string BuildWontDoComment(AnalysisAssessment assessment)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## 🚫 Analysis Gate: Won't Do");
        sb.AppendLine();
        if (!string.IsNullOrWhiteSpace(assessment.Reason))
            sb.AppendLine(assessment.Reason);

        if (assessment.Concerns.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("### Concerns");
            foreach (var concern in assessment.Concerns)
                sb.AppendLine($"- {concern}");
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine("*The agent analyzed the codebase and determined no code changes are needed. The issue has been labeled `agent:wont-do`. If you disagree with this assessment, remove the label and re-apply `agent:next` to retry with a fresh analysis.*");
        sb.AppendLine();
        sb.AppendLine("<!-- agent:gate-wont-do -->");
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Executes the code generation phase with stall monitoring.
    /// Returns true if the pipeline should continue to quality gates, false if it should stop (already failed).
    /// </summary>
    // TODO: [ARC-10] Add ArgumentNullException.ThrowIfNull for public method parameters
    public async Task<bool> ExecuteCodeGenerationAsync(
        PipelineRun run, PipelineConfiguration config,
        IAgentProvider agentProvider,
        IssueDetail issue, ParsedIssue parsed,
        BrainSyncOrchestrator brainSync,
        CancellationTokenSource? orchestratorCts,
        Action<PipelineStep> transitionTo,
        Action<string> onOutputLine, Action onChange,
        Func<PipelineRun, Task> updateFileChangeStats,
        Func<string, string, CancellationToken, Task> swapAgentLabel,
        Action<PipelineRun> addRunToHistory,
        CancellationToken ct)
    {
        transitionTo(PipelineStep.GeneratingCode);
        try
        {
            var brainContextSection = PromptBuilder.BuildBrainContextSection(
                run.BrainContextLoaded,
                run.RepositoryName?.Split('/').LastOrDefault(),
                null,
                brainSync.GetPreviousBrainWarnings(run.BrainProviderConfigId));

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

            var prompt = PromptBuilder.BuildPrompt(config.ImplementationPrompt, issue, parsed, brainWriteInstructions, brainContextWritten);
            _logger.Debug("Pipeline {RunId} implementation prompt:\n{Prompt}", run.RunId, prompt);

            using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var lastWarnTime = DateTime.UtcNow;
            var stallMonitorTask = Task.Run(async () =>
            {
                try
                {
                    while (!stallCts.Token.IsCancellationRequested)
                    {
                        await Task.Delay(config.StallPollInterval, stallCts.Token);

                        AgentHealthStatus health;
                        try { health = agentProvider.GetHealthStatus(); }
                        catch (Exception ex)
                        {
                            _logger.Warning(ex, "Pipeline {RunId} GetHealthStatus() call failed, continuing to poll", run.RunId);
                            continue;
                        }

                        if (health.IsProcessAlive == false)
                        {
                            var errorMsg = $"Agent process is no longer alive (PID {health.ProcessId}). " +
                                           $"Total elapsed: {(DateTime.UtcNow - run.StartedAt):hh\\:mm\\:ss}.";
                            _logger.Error("Pipeline {RunId} {StallMessage}", run.RunId, errorMsg);
                            run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.System, Content = errorMsg });
                            onChange();
                            break;
                        }

                        if (health.LastOutputTime.HasValue)
                        {
                            var silence = DateTime.UtcNow - health.LastOutputTime.Value;
                            var stallWarningInterval = config.StallWarningInterval;
                            var timeSinceLastWarn = DateTime.UtcNow - lastWarnTime;

                            if (silence >= stallWarningInterval && timeSinceLastWarn >= stallWarningInterval)
                            {
                                var elapsed = DateTime.UtcNow - run.StartedAt;
                                var msg = $"No agent output for {silence.TotalMinutes:F0}m. " +
                                          $"Agent call still in progress. " +
                                          $"Total elapsed: {elapsed:hh\\:mm\\:ss}. Timeout: {config.AgentTimeout:hh\\:mm\\:ss}.";
                                _logger.Warning("Pipeline {RunId} {StallMessage}", run.RunId, msg);
                                run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.System, Content = msg });
                                onChange();
                                lastWarnTime = DateTime.UtcNow;
                            }
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (ObjectDisposedException) { }
            }, CancellationToken.None);

            AgentResult agentResult;
            try
            {
                agentResult = await agentProvider.ExecuteAsync(
                    new AgentRequest
                    {
                        Prompt = prompt,
                        WorkspacePath = run.WorkspacePath!,
                        Timeout = config.AgentTimeout,
                        UseResume = true
                    },
                    ct,
                    line =>
                    {
                        run.OutputLines.Enqueue(line);
                        onOutputLine(line);
                    });
            }
            finally
            {
                await stallCts.CancelAsync();
                try { await stallMonitorTask; } catch (OperationCanceledException) { }
            }

            var outputSummary = agentResult.OutputLines.Count > 0
                ? string.Join(Environment.NewLine, agentResult.OutputLines.TakeLast(10))
                : "(no output)";

            run.ChatHistory.Enqueue(new ChatEntry { Role = ChatRole.Agent, Content = outputSummary });

            _logger.Information("Pipeline {RunId} initial code generation completed with exit code {ExitCode} after {Elapsed}",
                run.RunId, agentResult.ExitCode, DateTime.UtcNow - run.StartedAt);

            await updateFileChangeStats(run);

            if (agentResult.ExitCode != 0)
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

                if (agentResult.ExitCode == 124)
                {
                    run.FailureReason = $"Agent timed out after {config.AgentTimeout}. Implementation is incomplete.";
                    run.CompletedAt = DateTime.UtcNow;
                    await swapAgentLabel(run.IssueIdentifier, AgentLabels.Error, ct);
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
            await swapAgentLabel(run.IssueIdentifier, AgentLabels.Error, ct);
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

    /// <summary>
    /// Executes the code review loop with multi-agent support and fix prompts.
    /// </summary>
    // TODO: [ARC-10] Add ArgumentNullException.ThrowIfNull for public method parameters
    public async Task ExecuteCodeReviewAsync(
        PipelineRun run, PipelineConfiguration config,
        IAgentProvider agentProvider,
        IssueDetail issue, ParsedIssue parsed,
        CancellationTokenSource? orchestratorCts,
        Action<PipelineStep> transitionTo,
        Action<string> onOutputLine, Action onChange,
        CancellationToken ct)
    {
        if (!config.CodeReview.Enabled || config.CodeReview.MaxIterations <= 0)
            return;

        run.CodeReviewIterationsTotal = config.CodeReview.MaxIterations;
        for (var i = 0; i < config.CodeReview.MaxIterations; i++)
        {
            run.CodeReviewIterationInProgress = i + 1;
            transitionTo(PipelineStep.ReviewingCode);
            _logger.Information("Pipeline {RunId} starting code review iteration {Iteration}/{MaxIterations}",
                run.RunId, i + 1, config.CodeReview.MaxIterations);

            run.ChatHistory.Enqueue(new ChatEntry
            {
                Role = ChatRole.System,
                Content = $"Code review iteration {i + 1}/{config.CodeReview.MaxIterations} starting..."
            });
            onChange();

            var agents = config.CodeReview.Agents is { Count: > 0 } configuredAgents
                ? configuredAgents
                : (IReadOnlyList<ReviewAgentConfig>)new[] { new ReviewAgentConfig { Name = "Review", Prompt = config.CodeReview.Prompt } };

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
                    onChange();

                    var findingsFilePath = Path.Combine(run.WorkspacePath!, PromptBuilder.ReviewFindingsFilePath);
                    if (File.Exists(findingsFilePath))
                        File.Delete(findingsFilePath);

                    var reviewPrompt = PromptBuilder.BuildReviewPrompt(agent.Prompt, issue, parsed);
                    _logger.Debug("Pipeline {RunId} review prompt (iteration {Iteration}, agent '{AgentName}'):\n{Prompt}", run.RunId, i + 1, agent.Name, reviewPrompt);

                    var reviewResult = await agentProvider.ExecuteAsync(
                        new AgentRequest
                        {
                            Prompt = reviewPrompt,
                            WorkspacePath = run.WorkspacePath!,
                            Timeout = config.AgentTimeout,
                            UseResume = true
                        },
                        ct,
                        line =>
                        {
                            run.OutputLines.Enqueue(line);
                            onOutputLine(line);
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
                    onChange();
                }

                run.CodeReviewAgentsRun = agentsRun;
                var iterationFindingsText = iterationFindings.ToString();
                run.CodeReviewIterationsCompleted++;

                if (!string.IsNullOrEmpty(config.CodeReview.FixPrompt) && iterationCriticalCount > 0)
                {
                    _logger.Information("Pipeline {RunId} code review iteration {Iteration}: {Critical} CRITICAL findings detected across {AgentCount} agent(s), sending fix prompt",
                        run.RunId, i + 1, iterationCriticalCount, agents.Count);

                    // Write concatenated findings from all agents to the file so the fix agent can read it
                    var findingsFileForFix = Path.Combine(run.WorkspacePath!, PromptBuilder.ReviewFindingsFilePath);
                    await File.WriteAllTextAsync(findingsFileForFix, iterationFindingsText, ct);

                    var fixPrompt = PromptBuilder.BuildFixPrompt(config.CodeReview.FixPrompt);
                    _logger.Debug("Pipeline {RunId} fix prompt (iteration {Iteration}):\n{Prompt}", run.RunId, i + 1, fixPrompt);

                    await agentProvider.ExecuteAsync(
                        new AgentRequest
                        {
                            Prompt = fixPrompt,
                            WorkspacePath = run.WorkspacePath!,
                            Timeout = config.AgentTimeout,
                            UseResume = true
                        },
                        ct,
                        line =>
                        {
                            run.OutputLines.Enqueue(line);
                            onOutputLine(line);
                        });

                    run.ChatHistory.Enqueue(new ChatEntry
                    {
                        Role = ChatRole.Agent,
                        Content = $"[Code review fix {i + 1}/{config.CodeReview.MaxIterations}] Applied CRITICAL fixes"
                    });
                    onChange();
                }
                else if (!string.IsNullOrEmpty(config.CodeReview.FixPrompt))
                {
                    _logger.Information("Pipeline {RunId} code review iteration {Iteration}: no CRITICAL findings, skipping fix prompt",
                        run.RunId, i + 1);
                }
            }
            catch (OperationCanceledException) when (orchestratorCts?.IsCancellationRequested == true)
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
                onChange();
                break;
            }
        }

        run.CodeReviewIterationInProgress = 0;
    }
}
