using System.Text.RegularExpressions;

namespace CodingAgentWebUI.Pipeline.Services.Parsers;

/// <summary>
/// Parses test counts from stdout output when structured report files are not available.
/// Supports .NET per-assembly format, .NET 10 summary line, pytest output, and Maven/JUnit output.
/// </summary>
public static class StdoutTestResultParser
{
    /// <summary>
    /// Fallback: parses test counts from stdout when TRX files are not available.
    /// Handles .NET per-assembly format, .NET 10 summary line, pytest output, and Maven/JUnit output.
    /// Returns zeros on any error (including regex timeout on adversarial input).
    /// </summary>
    public static (int Passed, int Failed, int Skipped) ParseTestCounts(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return (0, 0, 0);

        try
        {
            return ParseTestCountsCore(output);
        }
        catch (RegexMatchTimeoutException)
        {
            return (0, 0, 0);
        }
    }

    private static (int Passed, int Failed, int Skipped) ParseTestCountsCore(string output)
    {
        return TryParseDotNetSummaryLine(output)
            ?? TryParsePytestSummary(output)
            ?? TryParseMavenSummary(output)
            ?? ParseDotNetPerAssemblyLines(output);
    }

    private static (int Passed, int Failed, int Skipped)? TryParseDotNetSummaryLine(string output)
    {
        var match = Regex.Match(output,
            @"Test summary:.*?failed:\s*(\d+).*?succeeded:\s*(\d+).*?skipped:\s*(\d+)",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        if (!match.Success) return null;
        var passed  = int.Parse(match.Groups[2].Value);
        var failed  = int.Parse(match.Groups[1].Value);
        var skipped = int.Parse(match.Groups[3].Value);
        return (passed, failed, skipped);
    }

    private static (int Passed, int Failed, int Skipped)? TryParsePytestSummary(string output)
    {
        var pytestMatch = Regex.Match(output,
            @"=+\s*(.*?)\s*in\s+[\d.]+s\s*=+",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2));
        if (!pytestMatch.Success) return null;

        var summary = pytestMatch.Groups[1].Value;
        var passed = ParseRegexInt(summary, @"(\d+)\s+passed");
        var failed = ParseRegexInt(summary, @"(\d+)\s+failed");
        var skipped = ParseRegexInt(summary, @"(\d+)\s+skipped");
        var errors = ParseRegexInt(summary, @"(\d+)\s+error");

        if (passed == 0 && failed == 0 && skipped == 0) return null;
        return (passed, failed + errors, skipped);
    }

    private static (int Passed, int Failed, int Skipped)? TryParseMavenSummary(string output)
    {
        var passed = 0; var failed = 0; var skipped = 0;
        var matched = false;
        foreach (var match in Regex.Matches(output,
            @"Tests run:\s*(\d+),\s*Failures:\s*(\d+),\s*Errors:\s*(\d+),\s*Skipped:\s*(\d+)",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)).Cast<Match>())
        {
            matched = true;
            int.TryParse(match.Groups[1].Value, out var run);
            int.TryParse(match.Groups[2].Value, out var failures);
            int.TryParse(match.Groups[3].Value, out var errors);
            int.TryParse(match.Groups[4].Value, out var skip);
            passed += run - failures - errors - skip;
            failed += failures + errors;
            skipped += skip;
        }
        return matched ? (passed, failed, skipped) : null;
    }

    private static (int Passed, int Failed, int Skipped) ParseDotNetPerAssemblyLines(string output)
    {
        var passed = 0; var failed = 0; var skipped = 0;
        foreach (var match in Regex.Matches(output,
            @"Passed:\s*(\d+),\s*Failed:\s*(\d+),\s*Skipped:\s*(\d+)",
            RegexOptions.IgnoreCase, TimeSpan.FromSeconds(2)).Cast<Match>())
        {
            if (int.TryParse(match.Groups[1].Value, out var p)) passed += p;
            if (int.TryParse(match.Groups[2].Value, out var f)) failed += f;
            if (int.TryParse(match.Groups[3].Value, out var s)) skipped += s;
        }
        return (passed, failed, skipped);
    }

    private static int ParseRegexInt(string input, string pattern)
    {
        var m = Regex.Match(input, pattern, RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1));
        return m.Success && int.TryParse(m.Groups[1].Value, out var n) ? n : 0;
    }
}
