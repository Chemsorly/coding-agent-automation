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
    Task JobAccepted(string jobId);
    Task JobRejected(string jobId, string reason);
    Task ReportJobCompleted(string jobId, JobCompletionPayload payload);
    Task AgentReady(string agentId);

    // Real-time status
    Task ReportStepTransition(string jobId, PipelineStep step, DateTimeOffset timestamp, Dictionary<string, string>? metadata = null);
    Task ReportOutputLines(string jobId, IReadOnlyList<string> lines);
    Task ReportChatEntry(string jobId, ChatRole role, string content);
    Task ReportQualityGateResult(string jobId, QualityGateReport report);
    Task ReportBrainSyncResult(string jobId, bool contextLoaded, int knowledgeFileCount);

    // Heartbeat
    Task Heartbeat(HeartbeatMessage message);

    // Issue operations (proxied through orchestrator)
    Task RequestPostComment(string jobId, CommentType commentType, CommentPayload payload);
    Task RequestLabelChange(string jobId, string newLabel, int targetKind = 0);

    // Decomposition issue operations (proxied through orchestrator)
    Task<CreatedIssueResult> RequestCreateIssue(string jobId, string title, string body, IReadOnlyList<string> labels);
    Task<CreatedIssueResult> RequestCreateIssueForProvider(string jobId, string issueProviderConfigId, string title, string body, IReadOnlyList<string> labels);
    Task<PagedResult<IssueSummary>> RequestListOpenIssues(string jobId, int page, int pageSize, IReadOnlyList<string>? labels);
    Task<PagedResult<IssueSummary>> RequestListClosedIssues(string jobId, int page, int pageSize, IReadOnlyList<string>? labels, DateTime? since);
    Task<IssueDetail> RequestGetIssue(string jobId, string identifier);
    Task<IReadOnlyList<IssueComment>> RequestListComments(string jobId, string identifier);
    Task RequestUpdateComment(string jobId, string issueId, string commentId, string body);

    // Token refresh
    Task<TokenRefreshResponse> RequestTokenRefresh(string jobId, ProviderKind providerKind);

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
    Task CancelJob(string jobId);
    Task AssignChatPrompt(ChatPromptMessage message);
    Task CancelChat(string sessionId);
    Task ForceDisconnect();
    Task RequestFetchModels(FetchModelsRequest request);
    Task AssignConsolidationJob(string agentId, ConsolidationJobMessage job);
}
