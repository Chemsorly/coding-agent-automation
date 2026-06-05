using System.Text.Json;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Prompts;

namespace CodingAgentWebUI.Pipeline.Services;

internal partial class AgentPhaseExecutor
{
    /// <summary>
    /// Executes the analysis phase: checks for existing analysis, runs agent analysis if needed,
    /// reads the analysis file, evaluates the confidence gate, and posts the analysis comment.
    /// Returns true if the pipeline should continue to code generation, false if it should stop.
    /// </summary>
    public async Task<bool> ExecuteAnalysisPhaseAsync(
        AgentPhaseContext context,
        IReadOnlyList<IssueComment> issueComments,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(issueComments);
        var run = context.Run;
        var config = context.Config;
        string? existingAnalysis = null;
        var analysisComment = issueComments.FirstOrDefault(c => c.Body.Contains(CommentMarkers.AnalysisHeader));
        var gateRejection = issueComments.FirstOrDefault(c => c.Body.Contains(CommentMarkers.GateRejection));
        var gateWontDo = issueComments.FirstOrDefault(c => c.Body.Contains(CommentMarkers.GateWontDo));

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
        try
        {
            var agentDir = Path.Combine(run.WorkspacePath!, AgentWorkspacePaths.MetadataDirectory);
            Directory.CreateDirectory(agentDir);

            var issueContextContent = PromptBuilder.BuildIssueContextFileContent(context.Issue, context.ParsedIssue, issueComments);
            await File.WriteAllTextAsync(Path.Combine(run.WorkspacePath!, AgentWorkspacePaths.IssueContextFilePath), issueContextContent, ct);
            _logger.Debug("Pipeline {RunId} wrote issue context to {FilePath}", run.RunId, AgentWorkspacePaths.IssueContextFilePath);
        }
        catch (IOException ex)
        {
            _logger.Warning(ex, "Pipeline {RunId} failed to write issue context file, continuing without it", run.RunId);
        }

        if (existingAnalysis != null)
        {
            run.AnalysisContent = existingAnalysis;
            run.AnalysisSkipped = true;
            context.Callbacks.TransitionTo(PipelineStep.AnalyzingCode);
            await AgentStallMonitor.MonitorAsync(context.AgentProvider,
                () => context.AgentProvider.EnsureSessionAsync(run.WorkspacePath!, ct),
                run, config, "Session warm-up", context.Callbacks.NotifyChange, _logger, ct);
            context.Callbacks.TransitionTo(PipelineStep.PostingAnalysis);
        }
        else
        {
            context.Callbacks.TransitionTo(PipelineStep.AnalyzingCode);

            var analysisFilePath = Path.Combine(run.WorkspacePath!, AgentWorkspacePaths.AnalysisFilePath);
            var assessmentFilePath = Path.Combine(run.WorkspacePath!, AgentWorkspacePaths.AnalysisAssessmentFilePath);
            AnalysisAssessment? assessment = null;
            var maxRetries = Math.Max(0, config.MaxAnalysisRetries);

            for (int attempt = 0; attempt <= maxRetries; attempt++)
            {
                // Delete stale artifacts before each attempt
                DeleteIfExists(analysisFilePath);
                DeleteIfExists(assessmentFilePath);

                try
                {
                    try
                    {
                        await AgentStallMonitor.MonitorAsync(context.AgentProvider,
                            () => context.AgentProvider.EnsureSessionAsync(run.WorkspacePath!, ct),
                            run, config, "Session warm-up", context.Callbacks.NotifyChange, _logger, ct);

                        var brainContextWrittenForAnalysis = await WriteBrainContextIfNeededAsync(run, ct);

                        var analysisPrompt = PromptBuilder.BuildAnalysisPrompt(config.AnalysisPrompt, context.Issue, context.ParsedIssue, brainContextWrittenForAnalysis);
                        _logger.Debug("Pipeline {RunId} analysis prompt:\n{Prompt}", run.RunId, analysisPrompt);

                        var analysisResult = await AgentStallMonitor.ExecuteWithMonitoringAsync(
                            context.AgentProvider,
                            new AgentRequest
                            {
                                Prompt = analysisPrompt,
                                WorkspacePath = run.WorkspacePath!,
                                Timeout = config.AgentTimeout,
                                UseResume = true
                            },
                            run, config, "Analysis agent", context.Callbacks.NotifyChange, _logger, ct,
                            line => context.Callbacks.EmitOutputLine(line));

                        run.AccumulateTokenUsage(analysisResult);

                        _logger.Information("Pipeline {RunId} analysis agent completed with exit code {ExitCode}, output lines: {LineCount}",
                            run.RunId, analysisResult.ExitCode, analysisResult.OutputLines.Count);

                        // Hard gate: analysis.md must exist and be non-trivial
                        if (!File.Exists(analysisFilePath))
                        {
                            var tailOutput = analysisResult.OutputLines.Count > 0
                                ? string.Join(Environment.NewLine, analysisResult.OutputLines.TakeLast(PipelineConstants.OutputTailLineCount))
                                : PipelineConstants.NoOutputFallback;
                            _logger.Warning("Pipeline {RunId} analysis.md not found. Exit code: {ExitCode}, last output:\n{Output}",
                                run.RunId, analysisResult.ExitCode, tailOutput);
                            throw new AnalysisIncompleteException("analysis.md not found after agent execution");
                        }

                        var analysisLength = new FileInfo(analysisFilePath).Length;
                        if (analysisLength < MinAnalysisLength)
                            throw new AnalysisIncompleteException($"analysis.md too short ({analysisLength} bytes, minimum {MinAnalysisLength})");

                        run.AnalysisContent = await File.ReadAllTextAsync(analysisFilePath, ct);
                        _logger.Information("Pipeline {RunId} read analysis from {AnalysisFilePath}", run.RunId, analysisFilePath);

                        // Hard gate: assessment.json must exist and be valid
                        if (!File.Exists(assessmentFilePath))
                        {
                            var tailOutput = analysisResult.OutputLines.Count > 0
                                ? string.Join(Environment.NewLine, analysisResult.OutputLines.TakeLast(PipelineConstants.OutputTailLineCount))
                                : PipelineConstants.NoOutputFallback;
                            _logger.Warning("Pipeline {RunId} analysis-assessment.json not found. Exit code: {ExitCode}, last output:\n{Output}",
                                run.RunId, analysisResult.ExitCode, tailOutput);
                        }
                        assessment = await ReadAssessmentAsync(run, ct);

                        // Success — exit retry loop
                        break;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException and not AnalysisIncompleteException)
                    {
                        throw new AnalysisIncompleteException($"Agent execution failed: {ex.Message}", ex);
                    }
                }
                catch (AnalysisIncompleteException ex)
                {
                    if (attempt < maxRetries)
                    {
                        var logException = ex.InnerException ?? ex;
                        _logger.Warning(logException, "Pipeline {RunId} analysis attempt {Attempt}/{MaxAttempts} failed, retrying",
                            run.RunId, attempt + 1, maxRetries + 1);
                        run.ChatHistory.Enqueue(new ChatEntry
                        {
                            Role = ChatRole.System,
                            Content = $"Analysis attempt {attempt + 1} failed: {ex.Message}. Retrying..."
                        });
                        context.Callbacks.NotifyChange();
                        run.AnalysisContent = null;
                        continue;
                    }

                    var terminalLogException = ex.InnerException ?? ex;
                    _logger.Error(terminalLogException, "Pipeline {RunId} analysis failed after {Attempts} attempt(s)",
                        run.RunId, attempt + 1);
                    return await FailPhaseAsync(run,
                        $"Analysis failed after {attempt + 1} attempt(s): {ex.Message}",
                        AgentLabels.Error, PipelineStep.Failed, context.IssueOps, context.Callbacks, CancellationToken.None);
                }
            }

            // ── Analysis Review (GAN-style adversarial feedback via shared helper) ──
            if (run.AnalysisContent != null)
            {
                context.Callbacks.TransitionTo(PipelineStep.ReviewingAnalysis);
                _logger.Information("Pipeline {RunId} starting analysis review (adversarial feedback loop)", run.RunId);

                var reviewConfig = new AdversarialReviewConfig
                {
                    Enabled = config.AnalysisReviewEnabled,
                    AgentTimeout = config.AgentTimeout
                };

                var reviewPrompt = PromptBuilder.BuildAnalysisReviewPrompt(
                    config.AnalysisReviewPrompt, context.Issue, context.ParsedIssue);
                var refinementPrompt = PromptBuilder.BuildAnalysisRefinementPrompt(config.AnalysisRefinementPrompt);

                var reviewResult = await AdversarialReviewHelper.ExecuteReviewAsync(
                    context.AgentProvider,
                    run.WorkspacePath!,
                    reviewPrompt,
                    refinementPrompt,
                    AgentWorkspacePaths.AnalysisReviewFilePath,
                    reviewConfig,
                    line => context.Callbacks.EmitOutputLine(line),
                    _logger,
                    ct);

                // Re-read analysis outputs if refinement was triggered
                if (reviewResult.RefinementTriggered)
                {
                    if (File.Exists(analysisFilePath))
                    {
                        var refinedLength = new FileInfo(analysisFilePath).Length;
                        if (refinedLength >= MinAnalysisLength)
                        {
                            run.AnalysisContent = await File.ReadAllTextAsync(analysisFilePath, ct);
                            _logger.Information("Pipeline {RunId} re-read refined analysis ({Length} bytes)", run.RunId, refinedLength);
                        }
                        else
                        {
                            _logger.Warning("Pipeline {RunId} refined analysis too short ({Length} bytes), keeping original", run.RunId, refinedLength);
                        }
                    }

                    try
                    {
                        assessment = await ReadAssessmentAsync(run, ct);
                    }
                    catch (AnalysisIncompleteException ex)
                    {
                        _logger.Warning(ex, "Pipeline {RunId} failed to re-read assessment after refinement, keeping original", run.RunId);
                    }
                }
            }

            // Read and evaluate the confidence gate assessment
            run.AnalysisRecommendation = ParseRecommendation(assessment?.Recommendation);
            run.AnalysisConcerns = assessment?.Concerns ?? Array.Empty<string>();
            run.AnalysisBlockingIssues = assessment?.BlockingIssues ?? Array.Empty<string>();

            // isNotReady is checked first: non-empty blockingIssues forces not_ready regardless of recommendation
            var isNotReady = assessment != null && (
                run.AnalysisRecommendation == AnalysisGateResult.NotReady
                || (assessment.BlockingIssues.Count > 0));

            var isWontDo = run.AnalysisRecommendation == AnalysisGateResult.WontDo;

            if (isNotReady)
            {
                context.Callbacks.TransitionTo(PipelineStep.PostingAnalysis);
                await PostAnalysisCommentAsync(run, context.Issue, context.IssueOps, assessment, ct);

                var abortComment = BuildNotReadyComment(assessment!);
                try { await context.IssueOps.PostCommentAsync(run.IssueIdentifier, abortComment, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { _logger.Warning(ex, "Pipeline {RunId} failed to post not-ready comment", run.RunId); }

                return await FailPhaseAsync(run,
                    $"Analysis gate: needs refinement — {assessment?.Reason ?? "issue not ready"}",
                    AgentLabels.NeedsRefinement, PipelineStep.Failed, context.IssueOps, context.Callbacks, ct);
            }

            if (isWontDo)
            {
                context.Callbacks.TransitionTo(PipelineStep.PostingAnalysis);
                await PostAnalysisCommentAsync(run, context.Issue, context.IssueOps, assessment, ct);

                var wontDoComment = BuildWontDoComment(assessment!);
                try { await context.IssueOps.PostCommentAsync(run.IssueIdentifier, wontDoComment, ct); }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { _logger.Warning(ex, "Pipeline {RunId} failed to post won't-do comment", run.RunId); }

                return await FailPhaseAsync(run,
                    $"Analysis gate: won't do — {assessment?.Reason ?? "no code changes needed"}",
                    AgentLabels.WontDo, PipelineStep.Completed, context.IssueOps, context.Callbacks, ct);
            }

            // Ready path — post analysis and continue
            context.Callbacks.TransitionTo(PipelineStep.PostingAnalysis);
            await PostAnalysisCommentAsync(run, context.Issue, context.IssueOps, assessment, ct);
        }

        return true;
    }

    private async Task<AnalysisAssessment> ReadAssessmentAsync(PipelineRun run, CancellationToken ct)
    {
        var assessmentPath = Path.Combine(run.WorkspacePath!, AgentWorkspacePaths.AnalysisAssessmentFilePath);
        if (!File.Exists(assessmentPath))
            throw new AnalysisIncompleteException("analysis-assessment.json not found after agent execution");

        try
        {
            var json = await File.ReadAllTextAsync(assessmentPath, ct);
            var result = JsonSerializer.Deserialize<AnalysisAssessment>(json, PipelineJsonOptions.Lenient);
            return result ?? throw new AnalysisIncompleteException("analysis-assessment.json deserialized to null");
        }
        catch (JsonException ex)
        {
            throw new AnalysisIncompleteException("analysis-assessment.json contains malformed JSON", ex);
        }
        catch (IOException ex)
        {
            throw new AnalysisIncompleteException($"Failed to read analysis-assessment.json: {ex.Message}", ex);
        }
    }

    private async Task PostAnalysisCommentAsync(
        PipelineRun run, IssueDetail issue,
        IAgentIssueOperations issueOps, AnalysisAssessment? assessment, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(run.AnalysisContent))
        {
            _logger.Warning("Pipeline {RunId} skipping analysis comment — no content", run.RunId);
            return;
        }

        try
        {
            var analysis = IssueAnalysisComment.FromAgentAnalysis(issue, run.AnalysisContent, assessment);
            await issueOps.PostCommentAsync(run.IssueIdentifier, analysis.ToMarkdown(), ct);
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
        sb.AppendLine(CommentMarkers.GateRejection);
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
        sb.AppendLine(CommentMarkers.GateWontDo);
        return sb.ToString().TrimEnd();
    }

    private static AnalysisGateResult? ParseRecommendation(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (string.Equals(value, "ready", StringComparison.OrdinalIgnoreCase))
            return AnalysisGateResult.Ready;
        if (string.Equals(value, "not_ready", StringComparison.OrdinalIgnoreCase))
            return AnalysisGateResult.NotReady;
        if (string.Equals(value, "wont_do", StringComparison.OrdinalIgnoreCase))
            return AnalysisGateResult.WontDo;

        return null;
    }
}
