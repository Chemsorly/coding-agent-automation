using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;

namespace CodingAgentWebUI.Hub;

public sealed partial class AgentHub
{
    // ── UI group subscriptions ──────────────────────────────────────────

    /// <summary>
    /// Adds the caller's connection to the <c>run-{jobId}</c> SignalR group so that
    /// subsequent push events (<see cref="IAgentHubUiClient"/>) are delivered to it.
    ///
    /// Ownership check (Req 5.3a): an agent-authenticated caller is only allowed to
    /// observe runs assigned to themselves. Operator-authenticated callers (those with
    /// no <c>agentId</c> query parameter) may subscribe to any run.
    ///
    /// Immediately pushes the current output backlog to the new subscriber
    /// so navigating to a mid-run page shows existing output without a separate fetch.
    ///
    /// Also pushes a <see cref="RunStateSnapshot"/> to the caller if the run is currently
    /// active (in-memory). This seeds the PipelineSidebar view model with HighWaterMark,
    /// IssueLabels, and all sidebar detail fields, including state for steps that completed
    /// before the subscriber connected.
    /// </summary>
    public async Task SubscribeToRun(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);

        // Validate jobId is a well-formed GUID (max 36 chars, parseable). This prevents
        // operator-authenticated connections from accumulating subscriptions to arbitrary
        // strings (very long, special characters, etc.) before the ownership check applies.
        if (!Guid.TryParse(jobId, out _))
            throw new HubException($"Invalid jobId format: must be a valid GUID (e.g. '3fa85f64-5717-4562-b3fc-2c963f66afa6').");

        // Agent connections carry an agentId query parameter. UI/operator connections do not.
        var callerAgentId = Context.GetHttpContext()?.Request.Query["agentId"].ToString();
        if (!string.IsNullOrEmpty(callerAgentId))
        {
            // Caller is an agent connection — enforce per-run ownership (Req 5.3a).
            var run = _facade.GetRun(new Pipeline.Models.JobId(jobId));

            // Fail closed. An unknown jobId means the run is not registered on this hub, so
            // nothing establishes that this agent owns it — admitting the subscription would
            // let an agent camp on an arbitrary run id and receive the full output stream
            // (which carries tokens and repository content) once that run starts.
            if (run is null || !string.Equals(run.AgentId, callerAgentId, StringComparison.Ordinal))
            {
                _logger.Warning(
                    "SubscribeToRun rejected — agent {AgentId} is not assigned to run {JobId}",
                    callerAgentId, jobId);
                throw new HubException("Not authorized for this run.");
            }
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, $"run-{jobId}");
        _logger.Debug("Connection {ConnectionId} subscribed to run-{JobId}", Context.ConnectionId, jobId);

        // Push buffered output lines to the new subscriber immediately (Req 3.4a).
        // Uses GetOutputBacklogAsync which reads from Redis in distributed mode (cross-replica),
        // or the in-memory ring buffer in single-replica mode.
        var lines = await _facade.GetOutputBacklogAsync(new Pipeline.Models.JobId(jobId));
        if (lines.Count > 0)
        {
            await _uiContext.Clients.Client(Context.ConnectionId)
                .SendAsync(HubMethodNames.OnOutputLines, jobId, lines);
            _logger.Debug("Pushed {LineCount} buffered output lines to new subscriber for run-{JobId}",
                lines.Count, jobId);
        }

        // Push a RunStateSnapshot to the caller so the PipelineSidebar can be seeded
        // with the current step, HighWaterMark, IssueLabels, and all detail fields.
        // Only push if the run is active (GetRun returns null for completed runs).
        var activeRun = _facade.GetRun(new Pipeline.Models.JobId(jobId));
        if (activeRun is not null)
        {
            var snapshot = BuildRunStateSnapshot(activeRun);
            await _uiContext.Clients.Client(Context.ConnectionId)
                .SendAsync(HubMethodNames.OnRunStateSnapshot, jobId, snapshot);
            _logger.Debug("Pushed RunStateSnapshot to new subscriber for run-{JobId} at step {Step}",
                jobId, activeRun.CurrentStep);
        }
    }

    /// <summary>
    /// Removes the caller's connection from the <c>run-{jobId}</c> group.
    /// Called when the UI navigates away from a run page.
    /// </summary>
    public Task UnsubscribeFromRun(string jobId)
    {
        ArgumentNullException.ThrowIfNull(jobId);
        if (!Guid.TryParse(jobId, out _))
            throw new HubException($"Invalid jobId format: must be a valid GUID.");
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, $"run-{jobId}");
    }

    /// <summary>
    /// Builds a <see cref="RunStateSnapshot"/> from the current state of an active <see cref="PipelineRun"/>.
    /// </summary>
    private static RunStateSnapshot BuildRunStateSnapshot(PipelineRun run) => new()
    {
        CurrentStep = run.CurrentStep,
        HighWaterMark = run.HighWaterMark,
        RetryCount = run.RetryCount,
        BranchName = run.BranchName,
        BaselineHealthPassed = run.BaselineHealthPassed,
        BrainRepoUsed = run.BrainProviderConfigId != null,
        BrainContextLoaded = run.BrainContextLoaded,
        BrainKnowledgeFileCount = run.BrainKnowledgeFileCount,
        IssueLabels = run.IssueLabels,
        AnalysisSkipped = run.AnalysisSkipped,
        AnalysisRecommendation = run.AnalysisRecommendation,
        FilesChangedCount = run.FilesChangedCount,
        LinesAdded = run.LinesAdded,
        LinesRemoved = run.LinesRemoved,
        CodeReviewIterationsCompleted = run.CodeReviewIterationsCompleted,
        CodeReviewIterationInProgress = run.CodeReviewIterationInProgress,
        CodeReviewIterationsTotal = run.CodeReviewIterationsTotal,
        CodeReviewAgentsRun = run.CodeReviewAgentsRun,
        CodeReviewCriticalCount = run.CodeReviewCriticalCount,
        CodeReviewWarningCount = run.CodeReviewWarningCount,
        CodeReviewSuggestionCount = run.CodeReviewSuggestionCount,
        LatestQualityReport = run.LatestQualityReport,
        QualityGateHistory = run.QualityGateHistory.ToArray(),
        PullRequestUrl = run.PullRequestUrl,
        PullRequestNumber = run.PullRequestNumber,
        IsDraftPr = run.IsDraftPr,
        BlacklistedFilesDetected = run.BlacklistedFilesDetected,
        OpenIssuesDownloaded = run.OpenIssuesDownloaded,
        BrainFilesCommitted = run.BrainFilesCommitted,
        BrainUpdatesPushed = run.BrainUpdatesPushed,
        DecompositionSubIssuesCreated = run.DecompositionSubIssuesCreated,
        DecompositionSubIssuesAttempted = run.DecompositionSubIssuesAttempted,
        FinalStep = null, // Not terminal — active run
        FailureReason = run.FailureReason,
        ModelName = run.ModelName,
        RepositoryName = run.RepositoryName,
        RunType = run.RunType,
        IssueIdentifier = run.IssueIdentifier.ToString(),
        IssueTitle = run.IssueTitle,
        StartedAtOffset = run.StartedAtOffset,
        BrainProviderConfigId = run.BrainProviderConfigId,
    };
}
