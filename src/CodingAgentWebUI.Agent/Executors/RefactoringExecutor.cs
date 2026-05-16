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
public sealed class RefactoringExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly Serilog.ILogger _logger;

    public RefactoringExecutor(Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Executes the refactoring detection workflow:
    /// 1. Clone code repo into temp workspace
    /// 2. Optionally clone brain repo for architectural context
    /// 3. Build holistic analysis prompt
    /// 4. Execute agent — expects .agent/refactoring-proposals.json in workspace
    /// 5. Parse proposals JSON from workspace file
    /// 6. If no proposals: return success with "No refactoring opportunities identified"
    /// 7. Create GitHub issues via issueProvider.CreateIssueAsync() for each proposal (max 3)
    /// 8. Return summary with issue count and identifiers
    /// </summary>
    public async Task<ConsolidationJobResult> ExecuteAsync(
        ConsolidationJobMessage job,
        IRepositoryProvider repoProvider,
        IRepositoryProvider? brainProvider,
        IIssueProvider issueProvider,
        IAgentProvider agentProvider,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(repoProvider);
        ArgumentNullException.ThrowIfNull(issueProvider);
        ArgumentNullException.ThrowIfNull(agentProvider);

        // WARNING 5 fix: Validate job ID is a valid GUID to prevent path traversal
        if (!Guid.TryParse(job.JobId, out _))
        {
            _logger.Warning("Invalid JobId format: {JobId}", job.JobId);
            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = false,
                ErrorMessage = "Invalid JobId format"
            };
        }

        // CRITICAL 3 fix: Use workspace path from job message, fall back to temp path
        var workspacePath = job.WorkspacePath is not null
            ? Path.Combine(job.WorkspacePath, "refactoring")
            : Path.Combine(Path.GetTempPath(), "consolidation", job.JobId, "refactoring");

        try
        {
            // 1. Clone code repo
            Directory.CreateDirectory(workspacePath);
            _logger.Information("Cloning code repo for refactoring detection run {RunId} into {Workspace}",
                job.JobId, workspacePath);
            await repoProvider.CloneAsync(workspacePath, ct);

            // 2. Optionally clone brain repo for architectural context
            if (brainProvider is not null)
            {
                var brainPath = Path.Combine(workspacePath, ".brain");
                try
                {
                    await brainProvider.CloneAsync(brainPath, ct);
                    _logger.Information("Brain repo cloned for architectural context in run {RunId}", job.JobId);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.Warning(ex, "Failed to clone brain repo for context in run {RunId}, continuing without it", job.JobId);
                }
            }

            // 3. Build prompt
            var prompt = ConsolidationPromptBuilder.BuildRefactoringDetectionPrompt();

            // 4. Execute agent
            _logger.Information("Executing refactoring detection agent for run {RunId}", job.JobId);
            var agentResult = await agentProvider.ExecuteAsync(
                new AgentRequest
                {
                    Prompt = prompt,
                    WorkspacePath = workspacePath,
                    Timeout = job.PipelineConfiguration.AgentTimeout
                },
                ct);

            if (!agentResult.Success)
            {
                _logger.Warning("Refactoring detection agent exited with code {ExitCode} for run {RunId}",
                    agentResult.ExitCode, job.JobId);
                return new ConsolidationJobResult
                {
                    JobId = job.JobId,
                    Success = false,
                    ErrorMessage = $"Agent exited with code {agentResult.ExitCode}"
                };
            }

            // 5. Parse proposals JSON from workspace file
            var proposalsFilePath = Path.Combine(workspacePath, AgentWorkspacePaths.MetadataDirectory, "refactoring-proposals.json");
            var proposals = await ParseProposalsAsync(proposalsFilePath, ct);

            if (proposals is null)
            {
                // Malformed JSON — mark as failed
                return new ConsolidationJobResult
                {
                    JobId = job.JobId,
                    Success = false,
                    ErrorMessage = "Failed to parse refactoring proposals JSON from agent output"
                };
            }

            // 6. If no proposals: return success
            if (proposals.Count == 0)
            {
                _logger.Information("No refactoring opportunities identified in run {RunId}", job.JobId);
                return new ConsolidationJobResult
                {
                    JobId = job.JobId,
                    Success = true,
                    Summary = "No refactoring opportunities identified"
                };
            }

            // 7. Create GitHub issues (max 3)
            var createdIssues = await CreateIssuesAsync(proposals, issueProvider, ct);

            // 8. Return summary — distinguish between "no proposals" and "proposals found but issues failed"
            var summary = FormatRefactoringSummary(createdIssues, proposals.Count);
            _logger.Information("Refactoring detection run {RunId} completed: {Summary}", job.JobId, summary);

            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = true,
                Summary = summary,
                CreatedIssues = createdIssues
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = false,
                ErrorMessage = "Consolidation run was cancelled"
            };
        }
        catch (Exception ex)
        {
            _logger.Error(ex, "Refactoring detection run {RunId} failed: {Message}", job.JobId, ex.Message);
            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
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
            _logger.Information("No refactoring proposals file found at {Path}, treating as no proposals", filePath);
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
            _logger.Warning(ex, "Malformed JSON in refactoring proposals file at {Path}", filePath);
            return null;
        }
    }

    /// <summary>
    /// Creates GitHub issues for each proposal, capped at 3.
    /// Individual issue creation failures are logged but do not stop processing.
    /// </summary>
    private async Task<IReadOnlyList<CreatedIssueInfo>> CreateIssuesAsync(
        IReadOnlyList<RefactoringProposal> proposals,
        IIssueProvider issueProvider,
        CancellationToken ct)
    {
        var createdIssues = new List<CreatedIssueInfo>();
        var proposalsToProcess = proposals.Take(3);

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

                _logger.Information("Created refactoring issue {Identifier}: {Title}",
                    result.Identifier, proposal.Title);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.Warning(ex, "Failed to create issue for proposal '{Title}', continuing with remaining",
                    proposal.Title);
            }
        }

        return createdIssues;
    }

    /// <summary>
    /// Formats the issue body from a refactoring proposal using the project's issue template.
    /// </summary>
    private static string FormatIssueBody(RefactoringProposal proposal)
    {
        var sanitizedDescription = SanitizeMarkdown(proposal.Description);
        var sanitizedRationale = SanitizeMarkdown(proposal.Rationale);
        var affectedFiles = string.Join("\n", proposal.AffectedFiles.Select(f => $"- `{f}`"));
        return $"""
            ## Summary

            {sanitizedDescription}

            ## Affected Components

            {affectedFiles}

            ## Suggested Approach

            {sanitizedRationale}

            ## Acceptance Criteria

            - [ ] Refactoring applied without changing observable behavior
            - [ ] All existing tests continue to pass

            ---
            *This issue was automatically generated by the refactoring detection consolidation loop.*
            """;
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
