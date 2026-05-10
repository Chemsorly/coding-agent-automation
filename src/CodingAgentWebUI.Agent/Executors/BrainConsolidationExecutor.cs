using System.Text.RegularExpressions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Agent.Executors;

/// <summary>
/// Executes brain consolidation: clones the brain repo, runs the 4-phase consolidation
/// agent prompt, commits all changes, and pushes to the base branch.
/// </summary>
public sealed class BrainConsolidationExecutor
{
    private readonly Serilog.ILogger _logger;

    public BrainConsolidationExecutor(Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Executes the brain consolidation workflow:
    /// 1. Clone brain repo into temp workspace
    /// 2. Build 4-phase consolidation prompt
    /// 3. Execute agent with prompt in the cloned workspace
    /// 4. Commit all changes via brainProvider.CommitAllAsync()
    /// 5. Push via brainProvider.PushBranchAsync() to base branch
    /// 6. Parse agent output for metrics, format summary
    /// 7. Return ConsolidationJobResult with success and summary
    /// </summary>
    public async Task<ConsolidationJobResult> ExecuteAsync(
        ConsolidationJobMessage job,
        IRepositoryProvider brainProvider,
        IAgentProvider agentProvider,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(job);
        ArgumentNullException.ThrowIfNull(brainProvider);
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
            ? Path.Combine(job.WorkspacePath, "brain")
            : Path.Combine(Path.GetTempPath(), "consolidation", job.JobId, "brain");

        try
        {
            // 1. Clone brain repo
            Directory.CreateDirectory(workspacePath);
            _logger.Information("Cloning brain repo for consolidation run {RunId} into {Workspace}",
                job.JobId, workspacePath);
            await brainProvider.CloneAsync(workspacePath, ct);

            // 2. Build prompt
            var prompt = ConsolidationPromptBuilder.BuildBrainConsolidationPrompt(job.LastSuccessfulRunUtc);

            // 3. Execute agent
            _logger.Information("Executing brain consolidation agent for run {RunId}", job.JobId);
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
                _logger.Warning("Brain consolidation agent exited with code {ExitCode} for run {RunId}",
                    agentResult.ExitCode, job.JobId);
                return new ConsolidationJobResult
                {
                    JobId = job.JobId,
                    Success = false,
                    ErrorMessage = $"Agent exited with code {agentResult.ExitCode}"
                };
            }

            // 4. Commit all changes
            _logger.Information("Committing brain consolidation changes for run {RunId}", job.JobId);
            await brainProvider.CommitAllAsync(workspacePath, $"Brain consolidation run {job.JobId}", ct);

            // 5. Push to base branch
            _logger.Information("Pushing brain consolidation changes for run {RunId}", job.JobId);
            await brainProvider.PushBranchAsync(workspacePath, brainProvider.BaseBranch, ct);

            // 6. Parse metrics from agent output
            var responseText = string.Join("\n", agentResult.OutputLines);
            var (filesModified, entriesMerged, contradictionsResolved, entriesPruned) = ParseMetrics(responseText);

            // 7. Format summary and return
            var summary = ConsolidationPromptBuilder.FormatBrainConsolidationSummary(
                filesModified, entriesMerged, contradictionsResolved, entriesPruned);

            _logger.Information("Brain consolidation run {RunId} completed: {Summary}", job.JobId, summary);

            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = true,
                Summary = summary
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
            _logger.Error(ex, "Brain consolidation run {RunId} failed: {Message}", job.JobId, ex.Message);
            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = false,
                ErrorMessage = ex.Message
            };
        }
        finally
        {
            // Workspace cleanup for successful runs is handled by the caller
            // Failed runs retain workspace for debugging
        }
    }

    /// <summary>
    /// Parses consolidation metrics from the agent's output text.
    /// Looks for patterns like "Files modified: N", "Entries merged: N", etc.
    /// Returns zeros for any metrics not found.
    /// </summary>
    internal static (int FilesModified, int EntriesMerged, int ContradictionsResolved, int EntriesPruned) ParseMetrics(string responseText)
    {
        var filesModified = ExtractMetric(responseText, @"[Ff]iles?\s+modified\D*(\d+)");
        var entriesMerged = ExtractMetric(responseText, @"[Ee]ntries?\s+merged\D*(\d+)");
        var contradictionsResolved = ExtractMetric(responseText, @"[Cc]ontradictions?\s+resolved\D*(\d+)");
        var entriesPruned = ExtractMetric(responseText, @"[Ee]ntries?\s+pruned\D*(\d+)");

        return (filesModified, entriesMerged, contradictionsResolved, entriesPruned);
    }

    private static int ExtractMetric(string text, string pattern)
    {
        var match = Regex.Match(text, pattern);
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : 0;
    }
}
