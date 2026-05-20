using System.Text.RegularExpressions;

namespace CodingAgentWebUI.Pipeline.Services.Parsers;

internal static class StdoutTestResultParser
{
    internal static (int Passed, int Failed, int Skipped) ParseTestCountsFromStdout(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
            return (0, 0, 0);

        // Try .NET 10 summary line first: "Test summary: total: 47; failed: 0; succeeded: 47; skipped: 0"
        var summaryMatch = Regex.Match(output,
            @"Test summary:.*?failed:\s*(\d+).*?succeeded:\s*(\d+).*?skipped:\s*(\d+)",
            RegexOptions.IgnoreCase);
        if (summaryMatch.Success)
        {
            int.TryParse(summaryMatch.Groups[2].Value, out var succeeded);
            int.TryParse(summaryMatch.Groups[1].Value, out var summaryFailed);
            int.TryParse(summaryMatch.Groups[3].Value, out var summarySkipped);
            return (succeeded, summaryFailed, summarySkipped);
        }

        // Try pytest format: "5 passed, 2 failed, 1 skipped in 3.45s" or "5 passed in 1.23s"
        var pytestMatch = Regex.Match(output,
            @"=+\s*(.*?)\s*in\s+[\d.]+s\s*=+",
            RegexOptions.IgnoreCase);
        if (pytestMatch.Success)
        {
            var pytestSummary = pytestMatch.Groups[1].Value;
            var pytestPassed = 0;
            var pytestFailed = 0;
            var pytestSkipped = 0;

            var passedMatch = Regex.Match(pytestSummary, @"(\d+)\s+passed", RegexOptions.IgnoreCase);
            if (passedMatch.Success) int.TryParse(passedMatch.Groups[1].Value, out pytestPassed);

            var failedMatch = Regex.Match(pytestSummary, @"(\d+)\s+failed", RegexOptions.IgnoreCase);
            if (failedMatch.Success) int.TryParse(failedMatch.Groups[1].Value, out pytestFailed);

            var skippedMatch = Regex.Match(pytestSummary, @"(\d+)\s+skipped", RegexOptions.IgnoreCase);
            if (skippedMatch.Success) int.TryParse(skippedMatch.Groups[1].Value, out pytestSkipped);

            var errorMatch = Regex.Match(pytestSummary, @"(\d+)\s+error", RegexOptions.IgnoreCase);
            if (errorMatch.Success && int.TryParse(errorMatch.Groups[1].Value, out var pytestErrors))
                pytestFailed += pytestErrors;

            if (pytestPassed > 0 || pytestFailed > 0 || pytestSkipped > 0)
                return (pytestPassed, pytestFailed, pytestSkipped);
        }

        // Try Maven/JUnit format: "Tests run: 10, Failures: 2, Errors: 1, Skipped: 3"
        var mavenPassed = 0;
        var mavenFailed = 0;
        var mavenSkipped = 0;
        var mavenMatched = false;

        foreach (var match in Regex.Matches(output,
            @"Tests run:\s*(\d+),\s*Failures:\s*(\d+),\s*Errors:\s*(\d+),\s*Skipped:\s*(\d+)",
            RegexOptions.IgnoreCase)
            .Cast<Match>())
        {
            mavenMatched = true;
            int.TryParse(match.Groups[1].Value, out var run);
            int.TryParse(match.Groups[2].Value, out var failures);
            int.TryParse(match.Groups[3].Value, out var errors);
            int.TryParse(match.Groups[4].Value, out var skip);

            mavenPassed += run - failures - errors - skip;
            mavenFailed += failures + errors;
            mavenSkipped += skip;
        }

        if (mavenMatched)
            return (mavenPassed, mavenFailed, mavenSkipped);

        // Fall back to summing per-assembly lines: "Passed:  10, Failed:   2, Skipped:   1"
        var passed = 0;
        var failed = 0;
        var skipped = 0;

        foreach (var match in Regex.Matches(output,
            @"Passed:\s*(\d+),\s*Failed:\s*(\d+),\s*Skipped:\s*(\d+)",
            RegexOptions.IgnoreCase)
            .Cast<Match>())
        {
            if (int.TryParse(match.Groups[1].Value, out var p)) passed += p;
            if (int.TryParse(match.Groups[2].Value, out var f)) failed += f;
            if (int.TryParse(match.Groups[3].Value, out var s)) skipped += s;
        }

        return (passed, failed, skipped);
    }
}
