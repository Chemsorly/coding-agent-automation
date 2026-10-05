using CodingAgent.Pipeline.CodeReview.Models;

namespace CodingAgent.Pipeline.CodeReview;

/// <summary>
/// Counts the findings in code review output by their severity markers ([CRITICAL], [WARNING], [SUGGESTION]).
/// </summary>
public static class SeverityParser
{
    /// <summary>
    /// Counts the finding lines in agent output by severity: one finding per line whose first token is a
    /// severity marker, matched case-insensitively. A marker anywhere else on a line is prose that names a
    /// severity and is not counted, and neither is a finding marked RESOLVED (a prior finding that has been
    /// addressed). <see cref="FindingsParser"/> parses the same lines, so the counts match its findings.
    /// </summary>
    public static SeverityCounts Parse(IReadOnlyList<string> outputLines)
    {
        ArgumentNullException.ThrowIfNull(outputLines);

        int critical = 0, warning = 0, suggestion = 0;

        foreach (var line in outputLines)
        {
            if (!FindingLineMatcher.TryMatch(line, out var severity, out _))
                continue;

            switch (severity)
            {
                case FindingSeverity.Critical:
                    critical++;
                    break;
                case FindingSeverity.Warning:
                    warning++;
                    break;
                default:
                    suggestion++;
                    break;
            }
        }

        return new SeverityCounts(critical, warning, suggestion);
    }
}

/// <summary>
/// Severity counts parsed from code review output.
/// </summary>
public sealed record SeverityCounts(int Critical, int Warning, int Suggestion);
