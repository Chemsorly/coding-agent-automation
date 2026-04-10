namespace KiroCliLib.Models;

/// <summary>
/// Represents the execution context for a Kiro CLI invocation.
/// </summary>
public class ExecutionContext
{
    public required IReadOnlyList<string> Prompts { get; init; }
    public string WorkspaceDirectory { get; init; } = Directory.GetCurrentDirectory();
    public string AgentName { get; init; } = "feature-developer";
}
