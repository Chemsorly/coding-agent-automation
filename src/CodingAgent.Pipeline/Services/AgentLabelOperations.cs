using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Telemetry;
using Serilog;
using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Shared helper that encapsulates the label-swap loop: iterate <see cref="AgentLabels.All"/>,
/// skip the target label, remove each, then add the target.
/// </summary>
public static class AgentLabelOperations
{
    private static readonly ILogger Logger = Log.ForContext(typeof(AgentLabelOperations));

    /// <summary>Adds <paramref name="newLabel"/> first, then removes all other agent labels.
    /// Add-first ordering ensures the target label is present even if the process is
    /// interrupted mid-swap (e.g., Docker SIGKILL during shutdown).</summary>
    /// <param name="removeLabel">Delegate to remove a label.</param>
    /// <param name="addLabel">Delegate to add a label.</param>
    /// <param name="newLabel">The target label to apply.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="expectedCurrentLabel">
    /// Optional: the label the caller expects is currently set.
    /// When provided, the <see cref="LabelStateMachine"/> validates the transition
    /// and logs a warning if invalid. Does NOT block execution (fail-open).
    /// </param>
    /// <param name="identifier">Optional: issue/PR identifier for log context.</param>
    /// <param name="logger">
    /// Optional: logger to use for this invocation. When null, falls back to the
    /// static <see cref="Logger"/> field. Intended for unit-test injection only —
    /// production callers should omit this parameter.
    /// </param>
    /// <param name="throwOnRemoveExhaustion">
    /// When true, re-throws the exception after all retry attempts for a label are exhausted,
    /// aborting the loop. When false (default), logs a warning and continues to the next label.
    /// Set to true for strict callers (e.g. <c>SwapLabelStrictAsync</c>) that expect failure
    /// propagation. Best-effort callers should leave this as false.
    /// </param>
    /// <param name="exhaustionCounter">
    /// Optional: counter to increment when remove-phase retries are exhausted on the
    /// <c>throwOnRemoveExhaustion=false</c> path. When null, falls back to
    /// <see cref="PipelineTelemetry.LabelSwapRemoveExhausted"/>. Intended for unit-test
    /// injection only — production callers should omit this parameter.
    /// </param>
    /// <param name="currentLabels">
    /// Optional: the labels currently present on the issue or PR.
    /// When provided, the remove phase only attempts to remove labels that are actually
    /// present — eliminating the DELETE 404s that occur when all <see cref="AgentLabels.All"/>
    /// entries are removed unconditionally. When null (default), falls back to the original
    /// behavior: attempt removal of every label in <see cref="AgentLabels.All"/> except
    /// <paramref name="newLabel"/>. The fallback guarantees correctness when the caller
    /// cannot supply current label state (Requirement #2).
    /// </param>
    public static async Task SwapAsync(
        Func<string, CancellationToken, Task> removeLabel,
        Func<string, CancellationToken, Task> addLabel,
        string newLabel,
        CancellationToken ct,
        string? expectedCurrentLabel = null,
        string? identifier = null,
        ILogger? logger = null,
        bool throwOnRemoveExhaustion = false,
        Counter<long>? exhaustionCounter = null,
        IReadOnlyList<string>? currentLabels = null)
    {
        var effectiveLogger = logger ?? Logger;
        var effectiveExhaustionCounter = exhaustionCounter ?? PipelineTelemetry.LabelSwapRemoveExhausted;

        // Validate the transition if the caller provides context about the current state.
        // This is observational only — invalid transitions log a warning but never block.
        if (expectedCurrentLabel is not null && !string.IsNullOrEmpty(newLabel))
        {
            LabelStateMachine.ValidateTransition(expectedCurrentLabel, newLabel, identifier);
        }

        // Add the target label first so the issue is never left without a status label
        // if the operation is interrupted partway through.
        if (!string.IsNullOrEmpty(newLabel))
        {
            effectiveLogger.Information("AgentLabelOperations: adding label {Label} (issue={IssueIdentifier})", newLabel, identifier ?? "unknown");
            await addLabel(newLabel, ct);
        }

        foreach (var label in AgentLabels.All)
        {
            // When the caller supplies the issue's current labels, skip any label that is not
            // actually present — avoids the DELETE 404s from removing labels the issue doesn't have.
            // When currentLabels is null (fallback path), attempt all labels as before.
            // TODO (WARNING): This check uses StringComparer.OrdinalIgnoreCase while the
            // skip-if-new-label guard two lines below uses StringComparison.Ordinal. The
            // OrdinalIgnoreCase choice is intentional for robustness (GitHub label names are
            // case-insensitive), but the inconsistency is a latent correctness risk: if newLabel
            // differs from an AgentLabels.All entry only in casing, the Ordinal guard below will
            // not fire and the label will be removed instead of skipped. Consider making both
            // guards use OrdinalIgnoreCase to be consistent. See issue #2971.
            if (currentLabels is not null && !currentLabels.Contains(label, StringComparer.OrdinalIgnoreCase))
                continue;

            if (string.Equals(label, newLabel, StringComparison.Ordinal))
                continue;

            var removed = false;
            for (var attempt = 0; attempt < 3 && !removed; attempt++)
            {
                try
                {
                    effectiveLogger.Debug("AgentLabelOperations: removing label {Label}", label);
                    await removeLabel(label, ct);
                    removed = true;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (attempt < 2)
                {
                    effectiveLogger.Warning(ex,
                        "AgentLabelOperations: removeLabel attempt {Attempt} failed for label {Label} on {Identifier} — retrying",
                        attempt + 1, label, identifier ?? "unknown");
                    await Task.Delay(TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt)), ct);
                }
                catch (Exception ex)
                {
                    effectiveLogger.Warning(ex,
                        "AgentLabelOperations: removeLabel exhausted retries for label {Label} on {Identifier} — partial swap; old label remains",
                        label, identifier ?? "unknown");

                    if (throwOnRemoveExhaustion)
                        throw;
                    else
                    {
                        // Increment only when the error is swallowed — the issue is now in a dual-label state.
                        // When throwOnRemoveExhaustion=true the caller knows about the failure; no counter needed.
                        effectiveExhaustionCounter.Add(1, new TagList
                        {
                            new("label", label),
                            new("identifier", identifier ?? "unknown")
                        });
                    }
                }
            }
        }
    }

    /// <summary>Removes all agent labels.</summary>
    /// <param name="removeLabel">Delegate to remove a label.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="currentLabels">
    /// Optional: the labels currently present on the issue or PR.
    /// When provided, only labels that appear in this collection are removed.
    /// When null (default), all labels in <see cref="AgentLabels.All"/> are removed
    /// (original behavior, used as fallback when current label state is unavailable).
    /// </param>
    public static async Task RemoveAllAsync(
        Func<string, CancellationToken, Task> removeLabel,
        CancellationToken ct,
        IReadOnlyList<string>? currentLabels = null)
    {
        foreach (var label in AgentLabels.All)
        {
            if (currentLabels is not null && !currentLabels.Contains(label, StringComparer.OrdinalIgnoreCase))
                continue;

            await removeLabel(label, ct);
        }
    }
}
