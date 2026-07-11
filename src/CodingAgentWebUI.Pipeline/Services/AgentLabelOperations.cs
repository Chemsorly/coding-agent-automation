using CodingAgentWebUI.Pipeline.Models;
using Serilog;

namespace CodingAgentWebUI.Pipeline.Services;

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
    public static async Task SwapAsync(
        Func<string, CancellationToken, Task> removeLabel,
        Func<string, CancellationToken, Task> addLabel,
        string newLabel,
        CancellationToken ct,
        string? expectedCurrentLabel = null,
        string? identifier = null)
    {
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
            Logger.Debug("AgentLabelOperations: adding label {Label}", newLabel);
            await addLabel(newLabel, ct);
        }

        foreach (var label in AgentLabels.All)
        {
            if (string.Equals(label, newLabel, StringComparison.Ordinal))
                continue;
            Logger.Debug("AgentLabelOperations: removing label {Label}", label);
            await removeLabel(label, ct);
        }
    }

    /// <summary>Removes all agent labels.</summary>
    public static async Task RemoveAllAsync(
        Func<string, CancellationToken, Task> removeLabel,
        CancellationToken ct)
    {
        foreach (var label in AgentLabels.All)
            await removeLabel(label, ct);
    }
}
