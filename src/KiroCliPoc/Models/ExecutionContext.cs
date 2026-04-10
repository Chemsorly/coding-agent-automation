namespace KiroCliPoc.Models;

/// <summary>
/// Represents the execution context for a Kiro CLI invocation.
/// Contains the prompt and configuration for executing the agent.
/// </summary>
/// <remarks>
/// Rule IDs: DOTNET_CONVENTIONS
/// </remarks>
public class ExecutionContext
{
    /// <summary>
    /// Gets or initializes the list of prompts to send to Kiro CLI in sequence.
    /// </summary>
    public required IReadOnlyList<string> Prompts { get; init; }

    /// <summary>
    /// Gets or initializes the workspace directory path.
    /// </summary>
    public string WorkspaceDirectory { get; init; } = Directory.GetCurrentDirectory();

    /// <summary>
    /// Gets or initializes the Kiro CLI agent name.
    /// </summary>
    public string AgentName { get; init; } = "feature-developer";
}
