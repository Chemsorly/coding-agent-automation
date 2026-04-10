namespace KiroCliPoc.Models;

/// <summary>
/// Contains context information passed to callback functions.
/// </summary>
public class CallbackContext
{
    /// <summary>
    /// Gets or initializes the current Kiro CLI state.
    /// </summary>
    public required KiroState State { get; init; }

    /// <summary>
    /// Gets or initializes an optional message associated with the state change.
    /// </summary>
    public string? Message { get; init; }

    /// <summary>
    /// Gets or initializes the list of files affected (for OnFilesChanged callback).
    /// </summary>
    public IReadOnlyList<string>? Files { get; init; }

    /// <summary>
    /// Gets or initializes the test results (for OnCompleted callback).
    /// </summary>
    public TestResult? TestResults { get; init; }

    /// <summary>
    /// Gets or initializes the exit code (for OnCompleted callback).
    /// </summary>
    public int? ExitCode { get; init; }
}
