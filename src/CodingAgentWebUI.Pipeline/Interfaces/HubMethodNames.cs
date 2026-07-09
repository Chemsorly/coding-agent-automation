namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Compile-time constants for SignalR hub method names.
/// Uses <c>nameof()</c> on <see cref="IAgentHub"/> to ensure rename refactoring propagates to call sites.
/// </summary>
public static class HubMethodNames
{
    // Registration
    public const string RegisterAgent = nameof(IAgentHub.RegisterAgent);
    public const string DeregisterAgent = nameof(IAgentHub.DeregisterAgent);

    // Job lifecycle
    public const string JobAccepted = nameof(IAgentHub.JobAccepted);
    public const string JobRejected = nameof(IAgentHub.JobRejected);
    public const string ReportJobCompleted = nameof(IAgentHub.ReportJobCompleted);
    public const string AgentReady = nameof(IAgentHub.AgentReady);

    // Real-time status
    public const string ReportStepTransition = nameof(IAgentHub.ReportStepTransition);
    public const string ReportOutputLines = nameof(IAgentHub.ReportOutputLines);
    public const string ReportChatEntry = nameof(IAgentHub.ReportChatEntry);
    public const string ReportQualityGateResult = nameof(IAgentHub.ReportQualityGateResult);
    public const string ReportBrainSyncResult = nameof(IAgentHub.ReportBrainSyncResult);

    // Heartbeat
    public const string Heartbeat = nameof(IAgentHub.Heartbeat);

    // Issue operations
    public const string RequestPostComment = nameof(IAgentHub.RequestPostComment);
    public const string RequestLabelChange = nameof(IAgentHub.RequestLabelChange);

    // Decomposition issue operations
    public const string RequestCreateIssue = nameof(IAgentHub.RequestCreateIssue);
    public const string RequestCreateIssueForProvider = nameof(IAgentHub.RequestCreateIssueForProvider);
    public const string RequestListOpenIssues = nameof(IAgentHub.RequestListOpenIssues);
    public const string RequestListClosedIssues = nameof(IAgentHub.RequestListClosedIssues);
    public const string RequestGetIssue = nameof(IAgentHub.RequestGetIssue);
    public const string RequestListComments = nameof(IAgentHub.RequestListComments);
    public const string RequestUpdateComment = nameof(IAgentHub.RequestUpdateComment);

    // Token refresh
    public const string RequestTokenRefresh = nameof(IAgentHub.RequestTokenRefresh);

    // Interactive chat
    public const string ReportChatResponse = nameof(IAgentHub.ReportChatResponse);
    public const string ReportChatCompleted = nameof(IAgentHub.ReportChatCompleted);

    // Model fetch
    public const string ReportFetchModelsResult = nameof(IAgentHub.ReportFetchModelsResult);

    // Consolidation
    public const string ReportConsolidationComplete = nameof(IAgentHub.ReportConsolidationComplete);
}
