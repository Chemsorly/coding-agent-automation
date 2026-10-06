using System.Text.RegularExpressions;
using CodingAgent.Pipeline.CodeReview.Models;

namespace CodingAgent.Pipeline.CodeReview;

/// <summary>
/// Decides which lines of review output are findings. <see cref="SeverityParser"/> counts these lines and
/// <see cref="FindingsParser"/> parses them, so the severity counts always match the parsed findings.
/// </summary>
/// <remarks>
/// A finding line starts with a severity marker — [CRITICAL], [WARNING] or [SUGGESTION], in any case — after
/// optional indentation, heading hashes, a list bullet or number, and bold or a code span. A marker anywhere
/// else is prose that names a severity ("No [CRITICAL] issues found", "// TODO [WARNING]: …", a quoted prior
/// finding) and is not a finding. A finding line that carries the upper-case status word RESOLVED reports a
/// prior finding as fixed and is skipped; the lower-case word is ordinary prose ("are resolved from the root
/// provider") and does not skip the line.
/// </remarks>
internal static partial class FindingLineMatcher
{
    /// <summary>
    /// Returns true when <paramref name="line"/> is a finding, with its severity and the index just past its marker.
    /// </summary>
    public static bool TryMatch(string line, out FindingSeverity severity, out int markerEnd)
    {
        var match = LeadingMarkerRegex().Match(line);
        if (!match.Success || ResolvedStatusRegex().IsMatch(line))
        {
            severity = default;
            markerEnd = 0;
            return false;
        }

        severity = match.Groups["severity"].Value.ToUpperInvariant() switch
        {
            "CRITICAL" => FindingSeverity.Critical,
            "WARNING" => FindingSeverity.Warning,
            _ => FindingSeverity.Suggestion
        };
        markerEnd = match.Index + match.Length;
        return true;
    }

    /// <summary>
    /// Matches a severity marker that starts the line: optional indentation, heading hashes, list bullet or
    /// number, and an opening <c>**</c>, <c>__</c>, <c>*</c>, <c>_</c> or backtick before it.
    /// </summary>
    [GeneratedRegex(@"^\s*(?:#{1,6}\s+)?(?:(?:[-*+]|\d{1,3}[.)])\s+)?(?:\*\*|__|\*|_|`)?\[(?<severity>critical|warning|suggestion)\]", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingMarkerRegex();

    /// <summary>
    /// Matches the upper-case status word RESOLVED as a whole word (not "resolved", "UNRESOLVED" or
    /// "DispatchResolvedWorkItemAsync").
    /// </summary>
    [GeneratedRegex(@"\bRESOLVED\b")]
    private static partial Regex ResolvedStatusRegex();
}
