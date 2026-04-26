namespace KiroCliLib.Models;

/// <summary>
/// Represents the results of test execution.
/// </summary>
public class TestResult
{
    public required int TotalTests { get; init; }
    public required int PassedTests { get; init; }
    public required int FailedTests { get; init; }
}
