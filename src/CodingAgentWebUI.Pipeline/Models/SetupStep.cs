namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// A named shell command that executes in the agent workspace after repository clone,
/// with environment secrets injected as environment variables.
/// </summary>
public sealed record SetupStep
{
    /// <summary>Human-readable description of the setup step.</summary>
    public required string Name { get; init; }

    /// <summary>The shell command to execute via /bin/bash -c.</summary>
    public required string Command { get; init; }
}
