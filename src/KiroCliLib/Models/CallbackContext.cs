namespace KiroCliLib.Models;

/// <summary>
/// Contains context information passed to callback functions.
/// </summary>
public class CallbackContext
{
    public required KiroState State { get; init; }
    public string? Message { get; init; }
    public IReadOnlyList<string>? Files { get; init; }
    public IReadOnlyList<FileChange>? FileChanges { get; init; }
    public TestResult? TestResults { get; init; }
    public int? ExitCode { get; init; }
}
