using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Server-side SignalR hub methods invoked by agents on the orchestrator.
/// Hosted at <c>/hubs/agent</c>.
/// </summary>
public interface IAgentHub
{
    // Registration
    Task RegisterAgent(AgentRegistrationMessage message);
    Task DeregisterAgent(string agentId);

    // Job lifecycle
    Task JobAccepted(JobId jobId);
    Task JobRejected(JobId jobId, string reason);
    Task ReportJobCompleted(JobId jobId, JobCompletionPayload payload);
    Task AgentReady(string agentId);

    // Real-time status
    Task ReportStepTransition(JobId jobId, PipelineStep step, DateTimeOffset timestamp, Dictionary<string, string>? metadata = null);
    Task ReportOutputLines(JobId jobId, IReadOnlyList<string> lines);
    Task ReportChatEntry(JobId jobId, ChatRole role, string content);
    Task ReportQualityGateResult(JobId jobId, QualityGateReport report);
    Task ReportBrainSyncResult(JobId jobId, bool contextLoaded, int knowledgeFileCount);

    // Heartbeat
    Task Heartbeat(HeartbeatMessage message);

    // Issue operations (proxied through orchestrator)
    Task RequestPostComment(JobId jobId, CommentType commentType, CommentPayload payload);
    Task RequestLabelChange(JobId jobId, string newLabel, int targetKind = 0);

    // Decomposition issue operations (proxied through orchestrator)
    Task<CreatedIssueResult> RequestCreateIssue(JobId jobId, string title, string body, IReadOnlyList<string> labels);
    Task<CreatedIssueResult> RequestCreateIssueForProvider(JobId jobId, string issueProviderConfigId, string title, string body, IReadOnlyList<string> labels);
    Task<PagedResult<IssueSummary>> RequestListOpenIssues(JobId jobId, int page, int pageSize, IReadOnlyList<string>? labels);
    Task<PagedResult<IssueSummary>> RequestListClosedIssues(JobId jobId, int page, int pageSize, IReadOnlyList<string>? labels, DateTime? since);
    Task<IssueDetail> RequestGetIssue(JobId jobId, string identifier);
    Task<IReadOnlyList<IssueComment>> RequestListComments(JobId jobId, string identifier);
    Task RequestUpdateComment(JobId jobId, string issueId, string commentId, string body);

    // Token refresh
    Task<TokenRefreshResponse> RequestTokenRefresh(JobId jobId, ProviderKind providerKind);

    // Interactive chat
    Task ReportChatResponse(ChatResponseMessage message);
    Task ReportChatCompleted(ChatCompletedMessage message);

    // Model fetch
    Task ReportFetchModelsResult(FetchModelsResponse response);

    // Consolidation
    Task<string> ReportConsolidationComplete(ConsolidationJobResult result);
}

/// <summary>
/// Client-side SignalR methods invoked by the orchestrator on agents.
/// </summary>
public interface IAgentHubClient
{
    Task AssignJob(JobAssignmentMessage message);
    Task CancelJob(JobId jobId);
    Task AssignChatPrompt(ChatPromptMessage message);
    Task CancelChat(string sessionId);
    Task ForceDisconnect();
    Task RequestFetchModels(FetchModelsRequest request);
    Task AssignConsolidationJob(string agentId, ConsolidationJobMessage job);
}
