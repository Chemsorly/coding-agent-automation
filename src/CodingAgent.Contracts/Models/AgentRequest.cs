namespace CodingAgent.Pipeline.Models;

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
    /// Per-invocation environment variables to inject into the child agent process via
    /// <see cref="System.Diagnostics.ProcessStartInfo.Environment"/>. These are scoped to
    /// the single process launch and do not pollute the parent process environment.
    /// Null or empty means the child inherits the parent environment unchanged.
    /// </summary>
    public IReadOnlyDictionary<string, string>? EnvironmentVariables { get; init; }

    /// <summary>
    /// Per-request effort override. When set, takes precedence over the provider's configured effort.
    /// Null means use the provider default. Supported by KiroCli (isolated calls) and ClaudeCode.
    /// </summary>
    public AgentEffortLevel? EffortOverride { get; init; }
}
