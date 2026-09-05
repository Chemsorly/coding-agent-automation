namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Narrow interface for chat session notification. Consumers that need to broadcast
/// chat response lines or chat completion depend on this interface rather than the full
/// orchestration or lifecycle service.
/// Implemented by <see cref="Services.PipelineRunLifecycleService"/>.
/// </summary>
public interface IChatNotifier
{
    /// <summary>Notifies subscribers that chat response lines were received for a session.</summary>
    void NotifyChatResponse(string sessionId, IReadOnlyList<string> lines);

    /// <summary>Notifies subscribers that a chat session has completed.</summary>
    void NotifyChatCompleted(string sessionId, int exitCode, string? error);
}
