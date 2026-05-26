using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Shared helper that encapsulates the label-swap loop: iterate <see cref="AgentLabels.All"/>,
/// skip the target label, remove each, then add the target.
/// </summary>
public static class AgentLabelOperations
{
    /// <summary>Removes all agent labels except <paramref name="newLabel"/>, then adds it.</summary>
    public static async Task SwapAsync(
        Func<string, CancellationToken, Task> removeLabel,
        Func<string, CancellationToken, Task> addLabel,
        string newLabel,
        CancellationToken ct)
    {
        foreach (var label in AgentLabels.All)
        {
            if (string.Equals(label, newLabel, StringComparison.Ordinal))
                continue;
            await removeLabel(label, ct);
        }

        if (!string.IsNullOrEmpty(newLabel))
            await addLabel(newLabel, ct);
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
