using KiroCliLib.Models;

namespace KiroWebUI.Models;

/// <summary>
/// Represents a single message in the chat conversation.
/// Content, IsStreaming, FinalState, FileChanges, TestResults, and ExitCode are mutable
/// because they are updated incrementally during streaming. All mutations are performed
/// under KiroExecutionService._messageLock for thread safety.
/// </summary>
public sealed class ChatMessage
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required ChatMessageRole Role { get; init; }
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    public bool IsStreaming { get; set; }
    public KiroState? FinalState { get; set; }
    public IReadOnlyList<FileChange>? FileChanges { get; set; }
    public TestResult? TestResults { get; set; }
    public int? ExitCode { get; set; }
}
