using System.Text.RegularExpressions;
using KiroCliLib.Models;

namespace KiroCliLib.Core;

/// <summary>
/// Parses Kiro CLI output to detect states and extract test results.
/// </summary>
public class OutputParser : IOutputParser
{
    private KiroState _currentState = KiroState.Started;
    private TestResult? _testResults;

    public event EventHandler<KiroState>? StateChanged;

    public TestResult? TestResults => _testResults;

    public void ProcessLine(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (string.IsNullOrWhiteSpace(line)) return;

        try
        {
            var newState = DetectState(line);
            if (newState.HasValue && newState.Value != _currentState)
            {
                _currentState = newState.Value;
                StateChanged?.Invoke(this, _currentState);
            }

            var testResult = DetectTestResults(line);
            if (testResult != null)
            {
                _testResults = testResult;
            }
        }
        catch (RegexMatchTimeoutException)
        {
            // Line contains adversarial content that caused a timeout — skip state/test detection for this line.
        }
    }

    private static KiroState? DetectState(string line)
    {
        if (Regex.IsMatch(line, @"^[✓✔]|^\s*Done\b|^\s*Completed\b|^\s*Success\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            return KiroState.Completed;

        if (Regex.IsMatch(line, @"^[✗✘]|^\s*Error:\s|^\s*Failed:\s|^\s*Exception:\s", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            return KiroState.Error;

        if (Regex.IsMatch(line, @"^\s*\?\s+\S|^\s*Please provide\b|^\s*Clarification needed\b|^\s*Waiting for input\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            return KiroState.NeedsInput;

        if (Regex.IsMatch(line, @"\bresearch(?:ing)?\s+phase\b|\bstarting\s+research\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            return KiroState.ResearchPhase;
        if (Regex.IsMatch(line, @"\bplan(?:ning)?\s+phase\b|\bcreating\s+plan\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            return KiroState.PlanPhase;
        if (Regex.IsMatch(line, @"\bimplement(?:ation|ing)?\s+phase\b|\bimplementing\s+\w+\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            return KiroState.ImplementPhase;
        if (Regex.IsMatch(line, @"\btest(?:ing)?\s+phase\b|\brunning\s+tests\b", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)))
            return KiroState.TestPhase;

        return null;
    }

    private TestResult? DetectTestResults(string line)
    {
        // TryParse: these handlers run on the process's output thread, where an exception (e.g. an
        // overflow from a 10-digit count in echoed text) would end the whole agent process.
        var testMatch = Regex.Match(line, @"Tests?:\s*(\d+)\s+passed(?:,\s*(\d+)\s+failed)?", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        if (testMatch.Success && int.TryParse(testMatch.Groups[1].Value, out var passed))
        {
            var failed = 0;
            if (testMatch.Groups[2].Success && !int.TryParse(testMatch.Groups[2].Value, out failed))
                return null;
            return new TestResult { TotalTests = passed + failed, PassedTests = passed, FailedTests = failed };
        }

        var simpleTestMatch = Regex.Match(line, @"[✓✔]\s*(\d+)\s+tests?", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        if (simpleTestMatch.Success && int.TryParse(simpleTestMatch.Groups[1].Value, out var total))
            return new TestResult { TotalTests = total, PassedTests = total, FailedTests = 0 };

        return null;
    }
}
