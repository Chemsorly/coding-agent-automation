namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Result of validating brain repository updates after the agent finishes writing.
/// Checks for session log creation, operation log updates, and proper entry format.
/// </summary>
public sealed class BrainValidationResult
{
    public bool SessionLogCreated { get; init; }
    public bool OperationLogUpdated { get; init; }
    public bool EntryFormatValid { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public bool HasWarnings => Warnings.Count > 0;
}
