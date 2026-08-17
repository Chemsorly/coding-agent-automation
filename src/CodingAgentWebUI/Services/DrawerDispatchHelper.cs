using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Static helpers shared by all three drawer services for the orchestration dispatch flow.
/// Extracted to avoid duplication across IssueDrawerService, PrReviewDrawerService, and EpicDrawerService.
/// </summary>
internal static class DrawerDispatchHelper
{
    private const string InitiatedByManual = "manual";

    /// <summary>
    /// Shared orchestration dispatch flow: resolve project → prepare request → distribute →
    /// return result tuple.
    /// </summary>
    public static async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchWithOrchestrationAsync(
        IDispatchOrchestrationService dispatchOrchestration,
        Func<PipelineProject, Task<JobDistributionRequest?>> prepareAsync,
        PipelineProject project,
        string distributionFailedError,
        string queuedMessage,
        string dispatchedMessage)
    {
        var request = await prepareAsync(project);

        if (request is null)
            return (false, "Could not dispatch — orchestration preparation failed (check logs for details).", null);

        var outcome = await dispatchOrchestration.DistributeAndFinalizeAsync(request, CancellationToken.None);
        if (!outcome.Success)
            return (false, distributionFailedError, null);

        return (true, null, outcome.Queued ? queuedMessage : dispatchedMessage);
    }

    /// <summary>Returns "manual" — the initiator string used for all manual dispatches.</summary>
    public static string ManualInitiator => InitiatedByManual;
}
