using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.Logging;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Encapsulates label swap logic with configurable retry policy.
/// Used by <see cref="DispatchService"/> with maxAttempts=1. The retry policy is configurable
/// because a second caller — the SignalR-mode drain service, deleted in Spec 041 — used 3.
/// </summary>
public interface ILabelSwapService
{
    /// <summary>
    /// Swaps the work item label to agent:in-progress with exponential backoff retry.
    /// Propagates <see cref="OperationCanceledException"/> unconditionally — callers
    /// may observe this if cancellation fires during the swap or during backoff delay.
    /// </summary>
    Task SwapLabelWithRetryAsync(
        Guid workItemId,
        ProviderConfigId providerConfigId,
        IssueIdentifier issueIdentifier,
        LabelTargetKind targetKind,
        CancellationToken ct);
}

/// <summary>
/// Default implementation of <see cref="ILabelSwapService"/>.
/// Extracted from <c>PendingWorkItemDrainService.SwapLabelWithRetryAsync</c>
/// and <c>TrySwapLabelOnceAsync</c>.
/// </summary>
internal sealed class LabelSwapService : ILabelSwapService
{
    private readonly ILabelService _labelService;
    private readonly ILogger<LabelSwapService> _logger;
    private readonly int _maxAttempts;

    public LabelSwapService(
        ILabelService labelService,
        ILogger<LabelSwapService> logger,
        int maxAttempts = 3)
    {
        ArgumentNullException.ThrowIfNull(labelService);
        ArgumentNullException.ThrowIfNull(logger);
        _labelService = labelService;
        _logger = logger;
        _maxAttempts = maxAttempts;
    }

    /// <inheritdoc/>
    public async Task SwapLabelWithRetryAsync(
        Guid workItemId,
        ProviderConfigId providerConfigId,
        IssueIdentifier issueIdentifier,
        LabelTargetKind targetKind,
        CancellationToken ct)
    {
        for (int attempt = 1; attempt <= _maxAttempts; attempt++)
        {
            if (await TrySwapLabelOnceAsync(workItemId, providerConfigId, issueIdentifier, targetKind, attempt, ct))
            {
                break;
            }
        }
    }

    /// <summary>
    /// Performs a single label swap attempt. Returns <c>true</c> if the swap succeeded.
    /// Returns <c>false</c> if the attempt failed with a non-cancellation exception
    /// (caller should proceed to the next attempt or stop if retries are exhausted).
    /// Propagates <see cref="OperationCanceledException"/> unconditionally.
    /// </summary>
    private async Task<bool> TrySwapLabelOnceAsync(
        Guid workItemId,
        ProviderConfigId providerConfigId,
        IssueIdentifier issueIdentifier,
        LabelTargetKind targetKind,
        int attempt,
        CancellationToken ct)
    {
        try
        {
            await _labelService.SwapLabelStrictAsync(
                providerConfigId, issueIdentifier, AgentLabels.InProgress, targetKind, ct);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (attempt < _maxAttempts)
            {
                var delay = TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt - 1)); // 200ms, 400ms
                _logger.LogWarning(ex,
                    "LabelSwapService: label swap attempt {Attempt}/{Max} failed for WorkItem {WorkItemId}, retrying in {Delay}ms",
                    attempt, _maxAttempts, workItemId, delay.TotalMilliseconds);
                await Task.Delay(delay, ct);
            }
            else
            {
                _logger.LogWarning(ex,
                    "LabelSwapService: label swap exhausted all {Max} attempts for WorkItem {WorkItemId}",
                    _maxAttempts, workItemId);
            }
            return false;
        }
    }
}
