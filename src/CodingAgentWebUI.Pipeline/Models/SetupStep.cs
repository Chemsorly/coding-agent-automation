using MessagePack;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// A named shell command that executes in the agent workspace after repository clone,
/// with environment secrets injected as environment variables.
/// </summary>
[MessagePackObject]
public sealed record SetupStep
{
    /// <summary>The shell command to execute via /bin/bash -c.</summary>
    [Key(0)]
    public required string Command { get; init; }

    /// <summary>Human-readable description of the setup step.</summary>
    [Key(1)]
    public required string Name { get; init; }
}
