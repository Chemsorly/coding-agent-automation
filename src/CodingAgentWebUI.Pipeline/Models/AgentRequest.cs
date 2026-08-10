namespace CodingAgentWebUI.Pipeline.Models;

public sealed class AgentRequest
{
    public required string Prompt { get; init; }
    public required string WorkspacePath { get; init; }
    public TimeSpan Timeout { get; init; } = PipelineConstants.DefaultAgentTimeout;
    public bool UseResume { get; init; }

    /// <summary>Explicit session ID to resume via --resume-id. Takes precedence over UseResume.</summary>
    public string? ResumeSessionId { get; init; }

    /// <summary>Local file paths of downloaded issue/PR images for native vision delivery.</summary>
    public IReadOnlyList<string>? ImagePaths { get; init; }

    /// <summary>
    /// Per-process environment variables to inject into the agent's child process only.
    /// These are merged into <see cref="System.Diagnostics.ProcessStartInfo.Environment"/> before launch
    /// and do NOT mutate the parent process environment.
    /// </summary>
    public IReadOnlyDictionary<string, string>? AdditionalEnv { get; init; }
}
