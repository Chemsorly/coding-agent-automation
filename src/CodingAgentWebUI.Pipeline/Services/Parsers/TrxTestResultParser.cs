using System.Xml.Linq;

namespace CodingAgentWebUI.Pipeline.Services.Parsers;

/// <summary>
/// Parses .trx (Visual Studio Test Results) files to extract test counts and individual test names.
/// </summary>
public static class TrxTestResultParser
{
    /// <summary>
    /// Parses all .trx files in the results directory and sums up test counts across all assemblies.
    /// TRX files contain a ResultSummary/Counters element with total/passed/failed/etc attributes.
    /// </summary>
    public static (int Passed, int Failed, int Skipped) ParseTestCounts(string resultsDir)
    {
        var result = ParseTestResults(resultsDir);
        return (result.Passed, result.Failed, result.Skipped);
    }

    /// <summary>
    /// Parses all .trx files in the results directory and returns aggregate counts
    /// plus individual failed test names.
    /// </summary>
    public static TrxTestResult ParseTestResults(string resultsDir)
    {
        if (!Directory.Exists(resultsDir))
            return new TrxTestResult { Passed = 0, Failed = 0, Skipped = 0, FailedTestNames = [] };

        var trxFiles = Directory.GetFiles(resultsDir, "*.trx", SearchOption.AllDirectories);
        if (trxFiles.Length == 0)
            return new TrxTestResult { Passed = 0, Failed = 0, Skipped = 0, FailedTestNames = [] };

        var totalPassed = 0;
        var totalFailed = 0;
        var totalSkipped = 0;
        var failedTestNames = new List<string>();

        foreach (var trxFile in trxFiles)
        {
            try
            {
                var doc = XDocument.Load(trxFile);
                var ns = doc.Root?.Name.Namespace ?? XNamespace.None;

                var counters = doc.Descendants(ns + "Counters").FirstOrDefault();
                if (counters != null)
                {
                    totalPassed += ParseIntAttribute(counters, "passed");
                    totalFailed += ParseIntAttribute(counters, "failed") + ParseIntAttribute(counters, "error");
                    totalSkipped += ParseIntAttribute(counters, "notExecuted");
                }

                // Extract individual failed test names from UnitTestResult elements
                // Include both "Failed" and "Error" outcomes to match the Counters-based failed count
                foreach (var unitTestResult in doc.Descendants(ns + "UnitTestResult"))
                {
                    var outcome = unitTestResult.Attribute("outcome")?.Value;
                    if (string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(outcome, "Error", StringComparison.OrdinalIgnoreCase))
                    {
                        var testName = unitTestResult.Attribute("testName")?.Value;
                        if (!string.IsNullOrEmpty(testName))
                            failedTestNames.Add(testName);
                    }
                }
            }
            catch
            {
                // Skip malformed TRX files
            }
        }

        return new TrxTestResult
        {
            Passed = totalPassed,
            Failed = totalFailed,
            Skipped = totalSkipped,
            FailedTestNames = failedTestNames
        };
    }

    private static int ParseIntAttribute(XElement element, string name)
    {
        var attr = element.Attribute(name);
        return attr != null && int.TryParse(attr.Value, out var val) ? val : 0;
    }
}

/// <summary>
/// Result of parsing TRX files, including aggregate counts and individual failed test names.
/// </summary>
public sealed record TrxTestResult
{
    public required int Passed { get; init; }
    public required int Failed { get; init; }
    public required int Skipped { get; init; }
    public required IReadOnlyList<string> FailedTestNames { get; init; }
}
