using System.Text.RegularExpressions;

namespace CodingAgent.Pipeline.Brain;

/// <summary>
/// Extracts structured evidence from CI job logs and step lists (spec 054, req 4.1, DD7).
/// Pure static — no I/O.
/// </summary>
public static partial class CiLogEvidenceExtractor
{
    // ISO timestamp prefix: 2024-01-01T00:00:00.000Z followed by whitespace
    [GeneratedRegex(@"^\d{4}-\d\d-\d\dT[\d:.]+Z\s", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex TimestampPrefix();

    // Shard prefix: [0] or [12] etc.
    [GeneratedRegex(@"^\[\d+\]\s", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex ShardPrefix();

    // Failed test line: "  Failed TestName [duration]" or "  Failed TestName"
    [GeneratedRegex(@"^\s*Failed\s(?<name>.+?)(?:\s\[[^\]]+\])?$", RegexOptions.None, matchTimeoutMilliseconds: 100)]
    private static partial Regex FailedTestLine();

    // Lines to keep: QUALITY GATE STATUS or ##[error]
    private const string QualityGateMarker = "QUALITY GATE STATUS:";
    private const string ErrorMarker = "##[error]";

    /// <summary>
    /// Extracts CI evidence from a job log and its failed steps.
    /// </summary>
    public static BrainEvidenceCiJob Extract(
        string jobName,
        IReadOnlyList<string> failedSteps,
        string? logContent)
    {
        var lines = new List<string>();
        var testNames = ExtractFailedTestNames(logContent);
        int errorCount = 0;

        if (logContent is not null)
        {
            using var reader = new System.IO.StringReader(logContent);
            string? raw;
            while ((raw = reader.ReadLine()) is not null)
            {
                var line = StripPrefixes(raw);
                if (line.Contains(QualityGateMarker, StringComparison.Ordinal))
                {
                    lines.Add(Truncate(line));
                }
                else if (line.StartsWith(ErrorMarker, StringComparison.Ordinal) && errorCount < 3)
                {
                    lines.Add(Truncate(line));
                    errorCount++;
                }
                if (lines.Count >= BrainLayout.MaxCiLinesPerJob) break;
            }
        }

        return new BrainEvidenceCiJob(jobName, failedSteps, lines);
    }

    /// <summary>
    /// Extracts distinct failed test names from a CI log (at most 20).
    /// </summary>
    public static IReadOnlyList<string> ExtractFailedTestNames(string? logContent)
    {
        if (logContent is null) return [];
        var names = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        using var reader = new System.IO.StringReader(logContent);
        string? raw;
        while ((raw = reader.ReadLine()) is not null)
        {
            if (!raw.Contains("Failed ", StringComparison.Ordinal)) continue;
            var line = StripPrefixes(raw);
            Match m;
            try { m = FailedTestLine().Match(line); }
            catch (RegexMatchTimeoutException) { continue; }
            if (!m.Success) continue;
            var name = m.Groups["name"].Value.Trim();
            if (string.IsNullOrEmpty(name)) continue;
            if (!seen.Add(name)) continue;
            names.Add(name);
            if (names.Count >= BrainLayout.MaxFailedTestNames) break;
        }
        return names;
    }

    private static string StripPrefixes(string line)
    {
        try
        {
            var m = TimestampPrefix().Match(line);
            if (m.Success) line = line[m.Length..];
        }
        catch (RegexMatchTimeoutException) { }

        try
        {
            var m = ShardPrefix().Match(line);
            if (m.Success) line = line[m.Length..];
        }
        catch (RegexMatchTimeoutException) { }

        return line;
    }

    private static string Truncate(string line) =>
        line.Length > BrainLayout.MaxExcerptChars ? line[..BrainLayout.MaxExcerptChars] : line;
}
