using System.Diagnostics;
using System.Text;
using System.Text.Json;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Agent.Executors;

/// <summary>
/// Executes refactoring detection: clones the code repo, runs the holistic analysis
/// agent prompt, parses proposals from the workspace, and creates GitHub issues.
/// </summary>
public sealed class RefactoringExecutor : ConsolidationExecutorBase
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    protected override string WorkspaceSuffix => "refactoring";
    protected override string ExecutorName => "Refactoring detection";

    public RefactoringExecutor(Serilog.ILogger logger) : base(logger)
    {
    }

    /// <summary>
    /// Executes the refactoring detection workflow:
    /// 1. Clone code repo into temp workspace
    /// 2. Optionally clone brain repo for architectural context
    /// 3. Build holistic analysis prompt
    /// 4. Execute agent — expects .agent/refactoring-proposals.json in workspace
    /// 5. Parse proposals JSON from workspace file
    /// 6. If no proposals: return success with "No refactoring opportunities identified"
    /// 7. Adversarial review (if enabled and proposals non-empty)
    /// 8. Create GitHub issues via issueProvider.CreateIssueAsync() for each proposal (max 3)
    /// 9. Return summary with issue count and identifiers
    /// </summary>
    public async Task<ConsolidationJobResult> ExecuteAsync(
        ConsolidationJobMessage job,
        IRepositoryProvider repoProvider,
        IRepositoryProvider? brainProvider,
        IIssueProvider issueProvider,
        IAgentProvider agentProvider,
        CancellationToken ct,
        Action<string>? onOutputLine = null)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(repoProvider);
        ArgumentNullException.ThrowIfNull(issueProvider);
        ArgumentNullException.ThrowIfNull(agentProvider);

        var invalid = ValidateJobId(job);
        if (invalid is not null) return invalid;

        var workspacePath = ResolveWorkspacePath(job);

        return await WrapWithCancellationHandlingAsync(job.JobId, async () =>
        {
            // 1. Clone code repo
            Directory.CreateDirectory(workspacePath);
            Logger.Information("Cloning code repo for refactoring detection run {RunId} into {Workspace}",
                job.JobId, workspacePath);
            await repoProvider.CloneAsync(workspacePath, ct);

            // 2. Optionally clone brain repo for architectural context
            if (brainProvider is not null)
            {
                var brainPath = Path.Combine(workspacePath, ".brain");
                try
                {
                    await brainProvider.CloneAsync(brainPath, ct);
                    Logger.Information("Brain repo cloned for architectural context in run {RunId}", job.JobId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Logger.Warning(ex, "Failed to clone brain repo for context in run {RunId}, continuing without it", job.JobId);
                }
            }

            // 3. Hotspot analysis — run git log to identify frequently-changed files
            await WriteHotspotAnalysisAsync(workspacePath, job.PipelineConfiguration.HotspotAnalysisLookback, ct);

            // 4. Build prompt
            var prompt = ConsolidationPromptBuilder.BuildRefactoringDetectionPrompt(job.PipelineConfiguration.MaxRefactoringProposals);

            // 4. Execute agent
            Logger.Information("Executing refactoring detection agent for run {RunId}", job.JobId);
            var (agentResult, failure) = await ExecuteAgentAndCheckAsync(
                agentProvider,
                new AgentRequest
                {
                    Prompt = prompt,
                    WorkspacePath = workspacePath,
                    Timeout = job.PipelineConfiguration.AgentTimeout
                },
                job.JobId,
                ct);

            if (failure is not null) return failure;

            // 5. Parse proposals JSON from workspace file
            var proposalsFilePath = Path.Combine(workspacePath, AgentWorkspacePaths.RefactoringProposalsFilePath);
            var proposals = await ParseProposalsAsync(proposalsFilePath, ct);

            if (proposals is null)
            {
                // Malformed JSON — mark as failed
                return CreateFailureResult(job.JobId, "Failed to parse refactoring proposals JSON from agent output");
            }

            // 6. If no proposals: return success (skip review)
            if (proposals.Count == 0)
            {
                Logger.Information("No refactoring opportunities identified in run {RunId}", job.JobId);
                return new ConsolidationJobResult
                {
                    JobId = job.JobId,
                    Success = true,
                    Summary = "No refactoring opportunities identified"
                };
            }

            // 7. Adversarial review (if enabled and proposals non-empty)
            var reviewResult = await AdversarialReviewHelper.ExecuteReviewAsync(
                agentProvider,
                workspacePath,
                ConsolidationPromptBuilder.BuildRefactoringReviewPrompt(),
                ConsolidationPromptBuilder.BuildRefactoringRefinementPrompt(),
                AgentWorkspacePaths.RefactoringReviewFilePath,
                new AdversarialReviewConfig
                {
                    Enabled = job.PipelineConfiguration.RefactoringReviewEnabled,
                    AgentTimeout = job.PipelineConfiguration.AgentTimeout
                },
                onOutputLine,
                Logger,
                ct);

            // If refinement was triggered, re-read and re-parse proposals
            if (reviewResult.RefinementTriggered)
            {
                var refinedProposals = await ParseProposalsAsync(proposalsFilePath, ct);
                if (refinedProposals is null)
                {
                    Logger.Warning("Refined proposals file is malformed in run {RunId}, keeping original proposals", job.JobId);
                    // Keep original proposals — don't overwrite
                }
                else
                {
                    proposals = refinedProposals;
                    Logger.Information("Using refined proposals ({Count} proposals) in run {RunId}",
                        proposals.Count, job.JobId);
                }
            }

            // 8. Create GitHub issues (capped at MaxRefactoringProposals)
            var createdIssues = await CreateIssuesAsync(proposals, issueProvider, job.PipelineConfiguration.MaxRefactoringProposals, ct);

            // 9. Return summary — distinguish between "no proposals" and "proposals found but issues failed"
            var summary = FormatRefactoringSummary(createdIssues, proposals.Count);
            Logger.Information("{ExecutorName} run {RunId} completed: {Summary}", ExecutorName, job.JobId, summary);

            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = true,
                Summary = summary,
                CreatedIssues = createdIssues,
                ReviewTokenUsage = reviewResult.ReviewTokenUsage,
                RefinementTokenUsage = reviewResult.RefinementTokenUsage
            };
        }, ct);
    }

    /// <summary>
    /// Runs git log to identify frequently-changed files and writes a hotspot summary.
    /// Gracefully degrades on any failure — logs a warning and continues without the file.
    /// </summary>
    private async Task WriteHotspotAnalysisAsync(string workspacePath, TimeSpan lookback, CancellationToken ct)
    {
        try
        {
            var sinceDate = DateTime.UtcNow.Subtract(lookback).ToString("yyyy-MM-dd");
            var output = await RunGitCommandAsync(workspacePath, $"log --name-only --since=\"{sinceDate}\" --format=\"\"", ct);

            var hotspots = ParseHotspotOutput(output, lookback);
            if (hotspots is null)
            {
                Logger.Information("No git history found within lookback window for hotspot analysis");
                return;
            }

            var outputPath = Path.Combine(workspacePath, AgentWorkspacePaths.HotspotAnalysisFilePath);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            await File.WriteAllTextAsync(outputPath, hotspots, ct);

            Logger.Information("Wrote hotspot analysis to {Path}", outputPath);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Warning(ex, "Hotspot analysis failed, continuing without it");
        }
    }

    /// <summary>
    /// Parses raw git log --name-only output into a formatted hotspot summary.
    /// Returns null if no files found. Exposed as internal static for testability.
    /// </summary>
    internal static string? ParseHotspotOutput(string gitLogOutput, TimeSpan lookback)
    {
        var hotspots = gitLogOutput
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .GroupBy(file => file)
            .Select(g => (File: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .Take(30)
            .ToList();

        if (hotspots.Count == 0)
            return null;

        var sb = new StringBuilder();
        sb.AppendLine($"# Git Hotspot Analysis (last {lookback.Days} days)");
        sb.AppendLine("# Files ranked by change frequency — higher = more actively developed");
        sb.AppendLine();
        foreach (var (file, count) in hotspots)
            sb.AppendLine($"{count} changes — {file}");

        return sb.ToString();
    }

    private static async Task<string> RunGitCommandAsync(string workingDirectory, string arguments, CancellationToken ct)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.StartInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
        process.StartInfo.Environment["GIT_PAGER"] = "";

        process.Start();

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
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException($"git {arguments} timed out after 30 seconds");
        }

        var output = await outputTask;
        await errorTask;

        return output;
    }

    /// <summary>
    /// Parses the refactoring proposals JSON file from the workspace.
    /// Returns null if the file doesn't exist or contains malformed JSON.
    /// Returns an empty list if the file contains an empty array.
    /// </summary>
    private async Task<IReadOnlyList<RefactoringProposal>?> ParseProposalsAsync(
        string filePath, CancellationToken ct)
    {
        if (!File.Exists(filePath))
        {
            Logger.Information("No refactoring proposals file found at {Path}, treating as no proposals", filePath);
            return Array.Empty<RefactoringProposal>();
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            var proposals = JsonSerializer.Deserialize<List<RefactoringProposal>>(json, JsonOptions);
            return (IReadOnlyList<RefactoringProposal>?)proposals ?? Array.Empty<RefactoringProposal>();
        }
        catch (JsonException ex)
        {
            Logger.Warning(ex, "Malformed JSON in refactoring proposals file at {Path}", filePath);
            return null;
        }
    }

    /// <summary>
    /// Creates GitHub issues for each proposal, capped at <paramref name="maxProposals"/>.
    /// Individual issue creation failures are logged but do not stop processing.
    /// </summary>
    private async Task<IReadOnlyList<CreatedIssueInfo>> CreateIssuesAsync(
        IReadOnlyList<RefactoringProposal> proposals,
        IIssueProvider issueProvider,
        int maxProposals,
        CancellationToken ct)
    {
        var createdIssues = new List<CreatedIssueInfo>();
        var proposalsToProcess = proposals.Take(maxProposals);

        foreach (var proposal in proposalsToProcess)
        {
            try
            {
                var body = FormatIssueBody(proposal);
                var sanitizedTitle = SanitizeTitle(proposal.Title);
                var result = await issueProvider.CreateIssueAsync(
                    sanitizedTitle,
                    body,
                    new[] { AgentLabels.Refactoring, AgentLabels.AgentGenerated },
                    ct);

                createdIssues.Add(new CreatedIssueInfo
                {
                    Identifier = result.Identifier,
                    Title = proposal.Title,
                    Url = result.Url
                });

                Logger.Information("Created refactoring issue {Identifier}: {Title}",
                    result.Identifier, proposal.Title);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Warning(ex, "Failed to create issue for proposal '{Title}', continuing with remaining",
                    proposal.Title);
            }
        }

        return createdIssues;
    }

    /// <summary>
    /// Formats the issue body from a refactoring proposal using the project's issue template.
    /// </summary>
    internal static string FormatIssueBody(RefactoringProposal proposal)
    {
        var sanitizedDescription = SanitizeMarkdown(proposal.Description);
        var sanitizedRationale = SanitizeMarkdown(proposal.Rationale);
        var affectedFiles = string.Join("\n", proposal.AffectedFiles.Select(f => $"- `{f}`"));

        var sb = new StringBuilder();
        sb.AppendLine("## Summary");
        sb.AppendLine();
        sb.AppendLine(sanitizedDescription);
        sb.AppendLine();

        // TODO: EstimatedEffort, RiskLevel, and Technique should be passed through SanitizeMarkdown() for consistency with other agent-sourced fields
        if (proposal.EstimatedEffort is not null || proposal.RiskLevel is not null || proposal.Technique is not null)
        {
            if (proposal.EstimatedEffort is not null)
                sb.Append($"**Effort:** {proposal.EstimatedEffort}");
            if (proposal.RiskLevel is not null)
                sb.Append($"{(proposal.EstimatedEffort is not null ? " | " : "")}**Risk:** {proposal.RiskLevel}");
            if (proposal.Technique is not null)
                sb.Append($"{(proposal.EstimatedEffort is not null || proposal.RiskLevel is not null ? " | " : "")}**Technique:** {proposal.Technique}");
            sb.AppendLine();
            sb.AppendLine();
        }

        if (proposal.Prerequisites is { Count: > 0 })
        {
            sb.AppendLine("## Prerequisites");
            sb.AppendLine();
            // TODO: Guard against null entries in Prerequisites list (System.Text.Json may deserialize JSON nulls into non-nullable list elements)
            foreach (var prereq in proposal.Prerequisites)
                sb.AppendLine($"- {SanitizeMarkdown(prereq)}");
            sb.AppendLine();
        }

        sb.AppendLine("## Affected Components");
        sb.AppendLine();
        sb.AppendLine(affectedFiles);
        sb.AppendLine();
        sb.AppendLine("## Suggested Approach");
        sb.AppendLine();
        sb.AppendLine(sanitizedRationale);
        sb.AppendLine();
        sb.AppendLine("## Acceptance Criteria");
        sb.AppendLine();
        sb.AppendLine("- [ ] Refactoring applied without changing observable behavior");
        sb.AppendLine("- [ ] All existing tests continue to pass");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.Append("*This issue was automatically generated by the refactoring detection consolidation loop.*");

        return sb.ToString();
    }

    /// <summary>
    /// Sanitizes the proposal title for use in GitHub issue titles.
    /// Truncates to 200 chars and strips newlines.
    /// </summary>
    internal static string SanitizeTitle(string title)
    {
        var sanitized = title
            .Replace("\r", "")
            .Replace("\n", " ")
            .Trim();
        return sanitized.Length > 200 ? sanitized[..200] : sanitized;
    }

    /// <summary>
    /// Escapes markdown-sensitive characters to prevent injection in GitHub issues.
    /// Mirrors the logic in <see cref="FeedbackCommentFormatter"/>.
    /// </summary>
    private static string SanitizeMarkdown(string value)
    {
        return value
            .Replace("@", "@\u200B")  // Zero-width space breaks @mention parsing
            .Replace("<", "&lt;")     // Prevent HTML injection
            .Replace(">", "&gt;");
    }

    /// <summary>
    /// Formats the refactoring run summary with issue count and identifiers.
    /// Distinguishes between "no proposals found" and "proposals found but issue creation failed".
    /// </summary>
    internal static string FormatRefactoringSummary(IReadOnlyList<CreatedIssueInfo> createdIssues, int proposalCount = 0)
    {
        if (createdIssues.Count == 0 && proposalCount == 0)
            return "No refactoring opportunities identified";

        if (createdIssues.Count == 0 && proposalCount > 0)
            return $"Found {proposalCount} proposal(s) but failed to create issues (check GitHub App permissions)";

        var identifiers = string.Join(", ", createdIssues.Select(i => $"#{i.Identifier}"));
        if (createdIssues.Count < proposalCount)
            return $"Created {createdIssues.Count}/{proposalCount} refactoring issue(s): {identifiers} ({proposalCount - createdIssues.Count} failed)";

        return $"Created {createdIssues.Count} refactoring issue(s): {identifiers}";
    }
}
