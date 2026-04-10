namespace KiroCliLib.Models;

/// <summary>
/// Represents the results of test execution.
/// </summary>
public class TestResult
{
    public required int TotalTests { get; init; }
    public required int PassedTests { get; init; }
    public required int FailedTests { get; init; }
    public double? Coverage { get; init; }
    public IReadOnlyList<string> FailureMessages { get; init; } = Array.Empty<string>();
    public bool AllPassed => FailedTests == 0 && TotalTests > 0;
}
