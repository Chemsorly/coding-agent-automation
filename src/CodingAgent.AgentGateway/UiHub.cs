using CodingAgent.Infrastructure.Common;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using ILogger = Serilog.ILogger;

namespace CodingAgent.AgentGateway;

/// <summary>
/// Decides which runs and chats a signed-in user may watch on the <see cref="UiHub"/>. The host that maps the hub
/// implements it, since it holds the work items and chat sessions.
/// </summary>
public interface IUiHubAuthorizer
{
    /// <summary>True when the run exists and the user can read its project.</summary>
    Task<bool> CanWatchRunAsync(System.Security.Claims.ClaimsPrincipal user, Guid runId, CancellationToken ct);

    /// <summary>True when the chat pod's session belongs to the user (or the user is a global admin).</summary>
    Task<bool> CanWatchChatAsync(System.Security.Claims.ClaimsPrincipal user, string agentId, CancellationToken ct);
}

/// <summary>The UI hub's group names. <see cref="AgentHub"/> pushes to them through <c>IHubContext&lt;UiHub&gt;</c>.</summary>
public static class UiHubGroups
{
    public static string Run(string runId) => $"run-{runId}";

    public static string Chat(string agentId) => $"chat-{agentId}";
}

/// <summary>
/// The SignalR hub of the signed-in UI at <c>/hubs/ui</c> (spec 051 D7). The host maps it for the user token only,
/// with JSON only, and closes a connection when its token (which lasts the session) expires. A connection joins
/// the runs and chats it shows; <see cref="AgentHub"/> pushes their events (<see cref="IUiHubClient"/>).
/// A refusal reads the same whether the run or chat does not exist or belongs to someone else.
/// </summary>
public sealed class UiHub(IUiHubAuthorizer authorizer, IAgentHubFacade facade, ILogger logger) : Hub, IUiHub
{
    /// <summary>
    /// Joins the run's group, then sends the caller the output so far and, while the run is active, a
    /// <see cref="RunStateSnapshot"/>, so a page opened mid-run starts complete.
    /// </summary>
    public async Task SubscribeToRun(string runId)
    {
        if (!Guid.TryParse(runId, out var id) || !await authorizer.CanWatchRunAsync(Context.User!, id, Context.ConnectionAborted))
        {
            logger.Debug("UiHub: connection {ConnectionId} may not watch run {RunId}", Context.ConnectionId, LogSanitizer.SanitizeForLog(runId));
            throw new HubException("Run not found.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, UiHubGroups.Run(runId));

        // Read from Redis in distributed mode (cross-replica), from the in-memory ring buffer otherwise.
        var jobId = new JobId(runId);
        var lines = await facade.GetOutputBacklogAsync(jobId);
        if (lines.Count > 0)
            await Clients.Caller.SendAsync(HubMethodNames.OnOutputLines, runId, lines);

        // GetRun returns null once the run has ended. In Redis mode it returns a copy without the chat and
        // quality-gate histories, so those come from their own lists.
        var activeRun = facade.GetRun(jobId);
        if (activeRun is not null)
        {
            var chatHistory = await facade.GetChatHistoryAsync(jobId);
            var qgHistory = await facade.GetQualityGateHistoryAsync(jobId);
            await Clients.Caller.SendAsync(HubMethodNames.OnRunStateSnapshot, runId, BuildRunStateSnapshot(activeRun, chatHistory, qgHistory));
        }
    }

    public Task UnsubscribeFromRun(string runId)
    {
        if (!Guid.TryParse(runId, out _))
            throw new HubException("Run not found.");
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, UiHubGroups.Run(runId));
    }

    public async Task SubscribeToChat(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId) || agentId.Length > 200
            || !await authorizer.CanWatchChatAsync(Context.User!, agentId, Context.ConnectionAborted))
        {
            logger.Debug("UiHub: connection {ConnectionId} may not watch chat {AgentId}", Context.ConnectionId, LogSanitizer.SanitizeForLog(agentId));
            throw new HubException("Chat not found.");
        }

        await Groups.AddToGroupAsync(Context.ConnectionId, UiHubGroups.Chat(agentId));
    }

    public Task UnsubscribeFromChat(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId) || agentId.Length > 200)
            throw new HubException("Chat not found.");
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, UiHubGroups.Chat(agentId));
    }

    /// <summary>Builds a <see cref="RunStateSnapshot"/> from the current state of an active <see cref="PipelineRun"/>.</summary>
    internal static RunStateSnapshot BuildRunStateSnapshot(PipelineRun run, IReadOnlyList<ChatEntry> chatHistory, IReadOnlyList<QualityGateReport> qgHistory) => new()
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
        QualityGateHistory = qgHistory,
        PullRequestUrl = run.PullRequestUrl,
        PullRequestNumber = run.PullRequestNumber,
        IsDraftPr = run.IsDraftPr,
        BlacklistedFilesDetected = run.BlacklistedFilesDetected,
        OpenIssuesDownloaded = run.OpenIssuesDownloaded,
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
        ChatHistory = chatHistory,
        BrainInboxFileUrl = run.BrainInboxFileUrl,
        BrainSkillsResult = run.BrainSkillsResult,
    };
}
