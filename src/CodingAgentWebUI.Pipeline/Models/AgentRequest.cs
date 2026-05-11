namespace CodingAgentWebUI.Pipeline.Models;

public sealed class AgentRequest
{
    public required string Prompt { get; init; }
    public required string WorkspacePath { get; init; }
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(30);
    public bool UseResume { get; init; }

    /// <summary>Explicit session ID to resume via --resume-id. Takes precedence over UseResume.</summary>
    public string? ResumeSessionId { get; init; }
}
