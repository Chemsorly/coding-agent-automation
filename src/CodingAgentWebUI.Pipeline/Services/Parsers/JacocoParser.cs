using System.Xml.Linq;

namespace CodingAgentWebUI.Pipeline.Services.Parsers;

internal static class JacocoParser
{
    /// <summary>
    /// Parses JaCoCo XML files and returns the aggregate line coverage percentage.
    /// JaCoCo uses counter elements with type="LINE" at the class level:
    /// <![CDATA[<counter type="LINE" missed="6" covered="10"/>]]>
    /// When multiple reports cover the same source file, counters are summed.
    /// </summary>
    internal static double ParseCoverageFromJacoco(string[] jacocoFiles)
    {
        var totalMissed = 0L;
        var totalCovered = 0L;

        foreach (var file in jacocoFiles)
        {
            try
            {
                var doc = XDocument.Load(file);
                if (doc.Root == null) continue;

                // Sum LINE counters from all class elements
                foreach (var counter in doc.Descendants("counter"))
                {
                    var type = counter.Attribute("type")?.Value;
                    if (!string.Equals(type, "LINE", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var missed = (long?)counter.Attribute("missed") ?? 0;
                    var covered = (long?)counter.Attribute("covered") ?? 0;

                    // Only count class-level counters (direct children of <class> elements)
                    // to avoid double-counting from package/report-level summaries
                    var parent = counter.Parent;
                    if (parent?.Name.LocalName == "class")
                    {
                        totalMissed += missed;
                        totalCovered += covered;
                    }
                }
            }
            catch
            {
                // Skip malformed files
            }
        }

        var totalLines = totalMissed + totalCovered;
        return totalLines > 0 ? (double)totalCovered / totalLines * 100.0 : 0.0;
    }
}
