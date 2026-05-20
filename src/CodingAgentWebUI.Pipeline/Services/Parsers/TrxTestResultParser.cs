using System.Xml.Linq;

namespace CodingAgentWebUI.Pipeline.Services.Parsers;

internal static class TrxTestResultParser
{
    internal static (int Passed, int Failed, int Skipped) ParseTestCountsFromTrx(string resultsDir)
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
