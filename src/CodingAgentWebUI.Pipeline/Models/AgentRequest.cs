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
}
