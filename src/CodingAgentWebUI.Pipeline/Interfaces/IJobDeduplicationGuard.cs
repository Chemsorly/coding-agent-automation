namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Releases the dedup guard for an issue identifier, making it re-dispatchable.
/// Separated from dispatch logic to avoid circular DI dependencies
/// (PipelineOrchestrationService cannot depend on AgentJobDispatcher).
/// </summary>
public interface IJobDeduplicationGuard
{
    /// <summary>
    /// Marks an issue as no longer being processed, releasing the dedup guard.
    /// After this call, the issue can be re-enqueued for dispatch.
    /// </summary>
    void MarkIssueComplete(string issueIdentifier, string issueProviderConfigId);
}
