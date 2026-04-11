using KiroCliLib.Models;

namespace KiroWebUI.Models;

/// <summary>
/// Response body for the POST /api/prompt endpoint.
/// </summary>
public sealed class PromptResponse
{
    public required int ExitCode { get; init; }
    public required IReadOnlyList<string> OutputLines { get; init; }
    public IReadOnlyList<FileChange>? FileChanges { get; init; }
    public TestResult? TestResults { get; init; }
}
