using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Optional interface for mocking the lifecycle service in unit tests.
/// Not required for DI registration (concrete type injection is fine for singletons).
/// </summary>
public interface IPipelineRunLifecycleService
{
    PipelineRun? ActiveRun { get; }
    bool IsRunning { get; }
    bool HasAnyActiveRuns { get; }
    IReadOnlyList<PipelineRun> GetAllActiveRuns();
    bool IsIssueBeingProcessed(string issueIdentifier);
    void TransitionTo(PipelineRun run, PipelineStep step);
    Task FailRunAsync(PipelineRun run, string reason, CancellationToken ct = default);
    void AddRunToHistory(PipelineRun run);
    void NotifyChange();
    void EmitOutputLine(string message);
    void NotifyChatResponse(string sessionId, IReadOnlyList<string> lines);
    void NotifyChatCompleted(string sessionId, int exitCode, string? error);
    CancellationToken CreateLinkedCancellationToken(CancellationToken externalToken);
    Task CancelPipelineAsync();
    Task MarkAgentRunsCancelled();
    bool RegisterDispatchedRun(PipelineRun run);

    event Action? OnChange;
    event Action<string>? OnOutputLine;
    event Action<string, IReadOnlyList<string>>? OnChatResponse;
    event Action<string, int, string?>? OnChatCompleted;
}
