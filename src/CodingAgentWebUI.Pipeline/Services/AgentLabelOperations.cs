using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Shared helper that encapsulates the label-swap loop: iterate <see cref="AgentLabels.All"/>,
/// skip the target label, remove each, then add the target.
/// </summary>
public static class AgentLabelOperations
{
    /// <summary>Adds <paramref name="newLabel"/> first, then removes all other agent labels.
    /// Add-first ordering ensures the target label is present even if the process is
    /// interrupted mid-swap (e.g., Docker SIGKILL during shutdown).</summary>
    public static async Task SwapAsync(
        Func<string, CancellationToken, Task> removeLabel,
        Func<string, CancellationToken, Task> addLabel,
        string newLabel,
        CancellationToken ct)
    {
        // Add the target label first so the issue is never left without a status label
        // if the operation is interrupted partway through.
        if (!string.IsNullOrEmpty(newLabel))
            await addLabel(newLabel, ct);

        foreach (var label in AgentLabels.All)
        {
            if (string.Equals(label, newLabel, StringComparison.Ordinal))
                continue;
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
