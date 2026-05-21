using System.Xml.Linq;

namespace CodingAgentWebUI.Pipeline.Services.Parsers;

/// <summary>
/// Parses .trx (Visual Studio Test Results) files to extract test counts.
/// </summary>
public static class TrxTestResultParser
{
    /// <summary>
    /// Parses all .trx files in the results directory and sums up test counts across all assemblies.
    /// TRX files contain a ResultSummary/Counters element with total/passed/failed/etc attributes.
    /// </summary>
    public static (int Passed, int Failed, int Skipped) ParseTestCounts(string resultsDir)
    {
        if (!Directory.Exists(resultsDir))
            return (0, 0, 0);

        var trxFiles = Directory.GetFiles(resultsDir, "*.trx", SearchOption.AllDirectories);
        if (trxFiles.Length == 0)
            return (0, 0, 0);

        var totalPassed = 0;
        var totalFailed = 0;
        var totalSkipped = 0;

        foreach (var trxFile in trxFiles)
        {
            try
            {
                var doc = XDocument.Load(trxFile);
                var ns = doc.Root?.Name.Namespace ?? XNamespace.None;
                var counters = doc.Descendants(ns + "Counters").FirstOrDefault();
                if (counters == null) continue;

                totalPassed += ParseIntAttribute(counters, "passed");
                totalFailed += ParseIntAttribute(counters, "failed") + ParseIntAttribute(counters, "error");
                totalSkipped += ParseIntAttribute(counters, "notExecuted");
            }
            catch
            {
                // Skip malformed TRX files
            }
        }

        return (totalPassed, totalFailed, totalSkipped);
    }

    private static int ParseIntAttribute(XElement element, string name)
    {
        var attr = element.Attribute(name);
        return attr != null && int.TryParse(attr.Value, out var val) ? val : 0;
    }
}
