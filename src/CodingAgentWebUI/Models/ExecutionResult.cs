using KiroCliLib.Models;

namespace CodingAgentWebUI.Models;

/// <summary>
/// Internal result of a Kiro CLI execution within KiroExecutionService.
/// </summary>
public sealed class ExecutionResult
{
    public required int ExitCode { get; init; }
    public required IReadOnlyList<string> OutputLines { get; init; }
    public IReadOnlyList<FileChange>? FileChanges { get; init; }
    public TestResult? TestResults { get; init; }
    public KiroState FinalState { get; init; }
}
