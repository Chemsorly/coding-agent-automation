namespace KiroCliPoc.Models;

/// <summary>
/// Represents the results of test execution.
/// </summary>
public class TestResult
{
    /// <summary>
    /// Gets or initializes the total number of tests run.
    /// </summary>
    public required int TotalTests { get; init; }

    /// <summary>
    /// Gets or initializes the number of tests that passed.
    /// </summary>
    public required int PassedTests { get; init; }

    /// <summary>
    /// Gets or initializes the number of tests that failed.
    /// </summary>
    public required int FailedTests { get; init; }

    /// <summary>
    /// Gets or initializes the code coverage percentage (0-100).
    /// </summary>
    public double? Coverage { get; init; }

    /// <summary>
    /// Gets or initializes the list of failure messages from failed tests.
    /// </summary>
    public IReadOnlyList<string> FailureMessages { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Gets a value indicating whether all tests passed.
    /// </summary>
    public bool AllPassed => FailedTests == 0 && TotalTests > 0;
}
