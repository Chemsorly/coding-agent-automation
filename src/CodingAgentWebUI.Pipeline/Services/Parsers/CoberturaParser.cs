using System.Xml.Linq;

namespace CodingAgentWebUI.Pipeline.Services.Parsers;

/// <summary>
/// Parses Cobertura XML coverage reports to extract line coverage percentage.
/// </summary>
public static class CoberturaParser
{
    /// <summary>
    /// Parses Cobertura XML files and returns the merged line coverage percentage.
    /// When multiple reports cover the same source file, line-level hits are merged
    /// (max hit count per line) to avoid double-counting in multi-project solutions.
    /// </summary>
    public static double ParseCoverage(string[] coberturaFiles)
    {
        // Track per-line coverage: sourceFile -> (lineNumber -> hits)
        var lineCoverage = new Dictionary<string, Dictionary<int, int>>();

        foreach (var file in coberturaFiles)
        {
            try
            {
                var doc = XDocument.Load(file);
                if (doc.Root == null) continue;

                foreach (var cls in doc.Descendants("class"))
                {
                    var filename = cls.Attribute("filename")?.Value;
                    if (string.IsNullOrEmpty(filename)) continue;

                    if (!lineCoverage.TryGetValue(filename, out var fileLines))
                    {
                        fileLines = new Dictionary<int, int>();
                        lineCoverage[filename] = fileLines;
                    }

                    foreach (var line in cls.Descendants("line"))
                    {
                        var number = (int?)line.Attribute("number");
                        var hits = (int?)line.Attribute("hits");
                        if (number == null) continue;

                        var lineNum = number.Value;
                        var lineHits = hits ?? 0;

                        // Take the max hits across reports for the same line
                        if (fileLines.TryGetValue(lineNum, out var existing))
                            fileLines[lineNum] = Math.Max(existing, lineHits);
                        else
                            fileLines[lineNum] = lineHits;
                    }
                }
            }
            catch
            {
                // Skip malformed files
            }
        }

        var totalLines = 0L;
        var coveredLines = 0L;
        foreach (var fileLines in lineCoverage.Values)
        {
            foreach (var hits in fileLines.Values)
            {
                totalLines++;
                if (hits > 0) coveredLines++;
            }
        }

        return totalLines > 0 ? (double)coveredLines / totalLines * 100.0 : 0.0;
    }
}
