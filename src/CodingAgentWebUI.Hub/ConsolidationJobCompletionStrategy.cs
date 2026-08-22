using System.Diagnostics;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Hub;

/// <summary>
/// Handles completion of consolidation pipeline runs.
/// Extracted from <see cref="AgentJobLifecycleService.HandleConsolidationRunCompletedAsync"/>.
/// </summary>
/// <remarks>
/// <para>
/// Consolidation runs skip pipeline history persistence — they have their own completion path
/// (ReportConsolidationComplete) and their own history on the Consolidation page. They enter
/// the PipelineRun tracking only as ghost entries during orchestrator restart rehydration.
/// </para>
/// <para>
/// Does not perform agent-idle transitions — that is the caller's responsibility.
/// Does not call PostCompletionBookkeepingAsync — consolidation runs have no associated
/// issue labels or feedback comments.
/// </para>
/// </remarks>
internal sealed class ConsolidationJobCompletionStrategy : IJobCompletionStrategy
{
    private readonly IAgentHubFacade _facade;
    private readonly IChangeNotifier _changeNotifier;
    private readonly ILogger _logger;

    public ConsolidationJobCompletionStrategy(
        IAgentHubFacade facade,
        IChangeNotifier changeNotifier,
        ILogger logger)
    {
        _facade = facade;
        _changeNotifier = changeNotifier;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task ExecuteAsync(JobId jobId, PipelineRun run, JobCompletionPayload payload,
                                   Activity? activity, CancellationToken ct)
    {
        // Skip pipeline history persistence for consolidation runs.
        // Consolidation runs have their own completion path (ReportConsolidationComplete)
        // and their own history on the Consolidation page. They enter the PipelineRun
        // tracking only as ghost entries during orchestrator restart rehydration.
        _logger.Information(
            "ReportJobCompleted: skipping pipeline persistence for consolidation run {JobId} (IssueIdentifier={IssueIdentifier})",
            jobId.Value, run.IssueIdentifier);

        var (workItemStatus, consolidationError, consolidationFailureEnum) =
            CompletionOutcomeResolver.Resolve(payload.FinalStep, payload.FailureReason, payload.FailureCategory,
                "Consolidation run failed");

        _facade.RemoveRun(jobId.Value);

        try
        {
            await _facade.TransitionWorkItemAsync(jobId.Value, workItemStatus, ct,
                consolidationError, consolidationFailureEnum);
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "ReportJobCompleted: failed to transition consolidation WorkItem {JobId} (non-fatal)", jobId.Value);
        }

        // TODO: NotifyChange fires here while the agent is still in Busy state — the agent-idle
        // transition happens in AgentJobLifecycleService.HandleJobCompletedAsync after this method
        // returns. In the original HandleConsolidationRunCompletedAsync, NotifyChange was called
        // after agent state was cleared. A UI client reading agent status immediately on receiving
        // the notification will observe a stale Busy state. Consider moving NotifyChange to the
        // caller after the agent-idle transition, or accepting the minor ordering difference.
        _changeNotifier.NotifyChange();
    }
}
