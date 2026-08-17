using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Static helpers shared by all three drawer services for the orchestration and legacy dispatch flows.
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

    /// <summary>
    /// Shared legacy dispatch flow: distribute a pre-built request and return success/failure messages.
    /// </summary>
    public static async Task<(bool Success, string? Error, string? SuccessMessage)> DispatchLegacyAsync(
        IWorkDistributor workDistributor,
        JobDistributionRequest request,
        string successMessage,
        string failureError)
    {
        var result = await workDistributor.DistributeAsync(request, CancellationToken.None);
        return result.Success
            ? (true, null, successMessage)
            : (false, failureError, null);
    }

    /// <summary>Returns "manual" — the initiator string used for all manual dispatches.</summary>
    public static string ManualInitiator => InitiatedByManual;
}
