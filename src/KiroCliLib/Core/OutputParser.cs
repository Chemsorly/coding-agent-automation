using System.Text.RegularExpressions;
using KiroCliLib.Models;

namespace KiroCliLib.Core;

/// <summary>
/// Parses Kiro CLI output to detect states, extract information, and trigger events.
/// </summary>
public class OutputParser : IOutputParser
{
    private KiroState _currentState = KiroState.Started;
    private TestResult? _testResults;

    public event EventHandler<KiroState>? StateChanged;
    public event EventHandler<FileChange>? FileDetected;
    public event EventHandler<TestResult>? TestResultDetected;

    public TestResult? TestResults => _testResults;

    public void ProcessLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (string.IsNullOrWhiteSpace(line)) return;

        var newState = DetectState(line);
        if (newState.HasValue && newState.Value != _currentState)
        {
            _currentState = newState.Value;
            StateChanged?.Invoke(this, _currentState);
        }

        var fileChange = DetectFileOperation(line);
        if (fileChange != null)
        {
            FileDetected?.Invoke(this, fileChange);
        }

        var testResult = DetectTestResults(line);
        if (testResult != null)
        {
            _testResults = testResult;
            TestResultDetected?.Invoke(this, testResult);
        }
    }

    private KiroState? DetectState(string line)
    {
        if (Regex.IsMatch(line, @"^[✓✔]|^\s*Done\b|^\s*Completed\b|^\s*Success\b", RegexOptions.IgnoreCase))
            return KiroState.Completed;

        if (Regex.IsMatch(line, @"^[✗✘]|^\s*Error:\s|^\s*Failed:\s|^\s*Exception:\s", RegexOptions.IgnoreCase))
            return KiroState.Error;

        if (Regex.IsMatch(line, @"^\s*\?\s+\S|^\s*Please provide\b|^\s*Clarification needed\b|^\s*Waiting for input\b", RegexOptions.IgnoreCase))
            return KiroState.NeedsInput;

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
        var createdMatch = Regex.Match(line, @"Created:\s+(.+)", RegexOptions.IgnoreCase);
        if (createdMatch.Success)
            return new FileChange { Path = createdMatch.Groups[1].Value.Trim(), Type = FileChangeType.Created };

        var modifiedMatch = Regex.Match(line, @"Modified:\s+(.+)", RegexOptions.IgnoreCase);
        if (modifiedMatch.Success)
            return new FileChange { Path = modifiedMatch.Groups[1].Value.Trim(), Type = FileChangeType.Modified };

        var writingMatch = Regex.Match(line, @"Writing to\s+(.+)", RegexOptions.IgnoreCase);
        if (writingMatch.Success)
            return new FileChange { Path = writingMatch.Groups[1].Value.Trim(), Type = FileChangeType.Modified };

        return null;
    }

    private TestResult? DetectTestResults(string line)
    {
        var testMatch = Regex.Match(line, @"Tests?:\s*(\d+)\s+passed(?:,\s*(\d+)\s+failed)?", RegexOptions.IgnoreCase);
        if (testMatch.Success)
        {
            var passed = int.Parse(testMatch.Groups[1].Value);
            var failed = testMatch.Groups[2].Success ? int.Parse(testMatch.Groups[2].Value) : 0;
            return new TestResult { TotalTests = passed + failed, PassedTests = passed, FailedTests = failed };
        }

        var simpleTestMatch = Regex.Match(line, @"[✓✔]\s*(\d+)\s+tests?", RegexOptions.IgnoreCase);
        if (simpleTestMatch.Success)
        {
            var total = int.Parse(simpleTestMatch.Groups[1].Value);
            return new TestResult { TotalTests = total, PassedTests = total, FailedTests = 0 };
        }

        return null;
    }
}
