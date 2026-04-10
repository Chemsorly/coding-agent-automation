using System.Text.RegularExpressions;
using KiroCliPoc.Models;

namespace KiroCliPoc.Core;

/// <summary>
/// Parses Kiro CLI output to detect states, extract information, and trigger events.
/// </summary>
public class OutputParser
{
    private KiroState _currentState = KiroState.Started;
    private readonly List<string> _detectedFiles = new();
    private TestResult? _testResults;

    /// <summary>
    /// Occurs when the Kiro CLI state changes.
    /// </summary>
    public event EventHandler<KiroState>? StateChanged;

    /// <summary>
    /// Occurs when progress information is detected in the output.
    /// </summary>
    public event EventHandler<string>? ProgressUpdate;

    /// <summary>
    /// Occurs when a file operation is detected in the output.
    /// </summary>
    public event EventHandler<FileChange>? FileDetected;

    /// <summary>
    /// Occurs when test results are detected in the output.
    /// </summary>
    public event EventHandler<TestResult>? TestResultDetected;

    /// <summary>
    /// Gets the current state of Kiro CLI execution.
    /// </summary>
    public KiroState CurrentState => _currentState;

    /// <summary>
    /// Gets the list of detected file operations.
    /// </summary>
    public IReadOnlyList<string> DetectedFiles => _detectedFiles.AsReadOnly();

    /// <summary>
    /// Gets the parsed test results, if any.
    /// </summary>
    public TestResult? TestResults => _testResults;

    /// <summary>
    /// Processes a single line of output from Kiro CLI.
    /// </summary>
    /// <param name="line">The output line to process.</param>
    public void ProcessLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);

        if (string.IsNullOrWhiteSpace(line))
            return;

        // Check for state changes
        var newState = DetectState(line);
        if (newState.HasValue && newState.Value != _currentState)
        {
            _currentState = newState.Value;
            StateChanged?.Invoke(this, _currentState);
        }

        // Check for file operations
        var fileChange = DetectFileOperation(line);
        if (fileChange != null)
        {
            _detectedFiles.Add(fileChange.Path);
            FileDetected?.Invoke(this, fileChange);
        }

        // Check for test results
        var testResult = DetectTestResults(line);
        if (testResult != null)
        {
            _testResults = testResult;
            TestResultDetected?.Invoke(this, testResult);
        }

        // Emit progress update for informational lines
        if (IsProgressLine(line))
        {
            ProgressUpdate?.Invoke(this, line);
        }
    }

    private KiroState? DetectState(string line)
    {
        // Check for completion — require markers at line start or standalone status lines
        if (Regex.IsMatch(line, @"^[✓✔]|^\s*Done\b|^\s*Completed\b|^\s*Success\b", RegexOptions.IgnoreCase))
            return KiroState.Completed;

        // Check for errors — require markers at line start or structured error output
        if (Regex.IsMatch(line, @"^[✗✘]|^\s*Error:\s|^\s*Failed:\s|^\s*Exception:\s", RegexOptions.IgnoreCase))
            return KiroState.Error;

        // Check for needs input — require structured prompts, not conversational question marks
        if (Regex.IsMatch(line, @"^\s*\?\s+\S|^\s*Please provide\b|^\s*Clarification needed\b|^\s*Waiting for input\b", RegexOptions.IgnoreCase))
            return KiroState.NeedsInput;

        // Check for phase changes — require "phase" keyword or structured phase indicators
        if (Regex.IsMatch(line, @"\bresearch(?:ing)?\s+phase\b|\bstarting\s+research\b", RegexOptions.IgnoreCase))
            return KiroState.ResearchPhase;
        if (Regex.IsMatch(line, @"\bplan(?:ning)?\s+phase\b|\bcreating\s+plan\b", RegexOptions.IgnoreCase))
            return KiroState.PlanPhase;
        if (Regex.IsMatch(line, @"\bimplement(?:ation|ing)?\s+phase\b|\bimplementing\s+\w+\b", RegexOptions.IgnoreCase))
            return KiroState.ImplementPhase;
        if (Regex.IsMatch(line, @"\btest(?:ing)?\s+phase\b|\brunning\s+tests\b", RegexOptions.IgnoreCase))
            return KiroState.TestPhase;

        return null;
    }

    private FileChange? DetectFileOperation(string line)
    {
        // Match patterns like "Created: path/to/file", "Modified: path/to/file", "Writing to path/to/file"
        var createdMatch = Regex.Match(line, @"Created:\s+(.+)", RegexOptions.IgnoreCase);
        if (createdMatch.Success)
        {
            return new FileChange
            {
                Path = createdMatch.Groups[1].Value.Trim(),
                Type = FileChangeType.Created,
                Timestamp = DateTime.UtcNow
            };
        }

        var modifiedMatch = Regex.Match(line, @"Modified:\s+(.+)", RegexOptions.IgnoreCase);
        if (modifiedMatch.Success)
        {
            return new FileChange
            {
                Path = modifiedMatch.Groups[1].Value.Trim(),
                Type = FileChangeType.Modified,
                Timestamp = DateTime.UtcNow
            };
        }

        var writingMatch = Regex.Match(line, @"Writing to\s+(.+)", RegexOptions.IgnoreCase);
        if (writingMatch.Success)
        {
            return new FileChange
            {
                Path = writingMatch.Groups[1].Value.Trim(),
                Type = FileChangeType.Modified,
                Timestamp = DateTime.UtcNow
            };
        }

        return null;
    }

    private TestResult? DetectTestResults(string line)
    {
        // Match patterns like "Tests: 10 passed, 2 failed" or "✓ 10 tests passed"
        var testMatch = Regex.Match(line, @"Tests?:\s*(\d+)\s+passed(?:,\s*(\d+)\s+failed)?", RegexOptions.IgnoreCase);
        if (testMatch.Success)
        {
            var passed = int.Parse(testMatch.Groups[1].Value);
            var failed = testMatch.Groups[2].Success ? int.Parse(testMatch.Groups[2].Value) : 0;

            return new TestResult
            {
                TotalTests = passed + failed,
                PassedTests = passed,
                FailedTests = failed
            };
        }

        // Match patterns like "✓ 10 tests"
        var simpleTestMatch = Regex.Match(line, @"[✓✔]\s*(\d+)\s+tests?", RegexOptions.IgnoreCase);
        if (simpleTestMatch.Success)
        {
            var total = int.Parse(simpleTestMatch.Groups[1].Value);
            return new TestResult
            {
                TotalTests = total,
                PassedTests = total,
                FailedTests = 0
            };
        }

        // Match coverage patterns like "Coverage: 85%"
        var coverageMatch = Regex.Match(line, @"Coverage:\s*(\d+(?:\.\d+)?)%", RegexOptions.IgnoreCase);
        if (coverageMatch.Success && _testResults != null)
        {
            var coverage = double.Parse(coverageMatch.Groups[1].Value);
            return new TestResult
            {
                TotalTests = _testResults.TotalTests,
                PassedTests = _testResults.PassedTests,
                FailedTests = _testResults.FailedTests,
                Coverage = coverage
            };
        }

        return null;
    }

    private bool IsProgressLine(string line)
    {
        // Consider lines with certain keywords as progress updates
        var lowerLine = line.ToLowerInvariant();
        return lowerLine.Contains("processing") ||
               lowerLine.Contains("analyzing") ||
               lowerLine.Contains("generating") ||
               lowerLine.Contains("building") ||
               lowerLine.Contains("running");
    }
}
