using System.Text.RegularExpressions;
using CodingAgentWebUI.Pipeline.CodeReview.Models;

namespace CodingAgentWebUI.Pipeline.CodeReview;

/// <summary>
/// Extracts structured findings with file:line metadata from review agent output.
/// Supplementary to SeverityParser — does not replace it for count tracking.
/// Produces one StructuredFinding per input line containing a severity marker.
/// </summary>
public static partial class FindingsParser
{
    /// <summary>
    /// Parses agent output text into structured findings.
    /// Returns empty list for null/empty input (never throws).
    /// </summary>
    public static IReadOnlyList<StructuredFinding> Parse(string? agentOutput, string agentName)
    {
        ArgumentNullException.ThrowIfNull(agentName);

        if (string.IsNullOrEmpty(agentOutput))
            return [];

        var preprocessed = StripCodeBlockFences(agentOutput);
        var lines = preprocessed.Split('\n');
        var findings = new List<StructuredFinding>();

        foreach (var rawLine in lines)
        {
            var line = rawLine.TrimEnd('\r');
            var severityMatch = SeverityMarkerRegex().Match(line);
            if (!severityMatch.Success)
                continue;

            var severity = ParseSeverity(severityMatch.Value);
            var afterMarker = line[(severityMatch.Index + severityMatch.Length)..];

            // Search for file:line reference in the content after the severity marker
            var (filePath, lineNumber, fileLineEnd) = ExtractFileLineReference(afterMarker);

            string message;

            if (filePath is not null)
            {
                // Extract message: content after the file:line reference, with separators stripped
                message = afterMarker[fileLineEnd..];
                message = StripLeadingSeparators(message).Trim();
            }
            else
            {
                // No file:line reference — entire content after marker is the message
                message = StripLeadingSeparators(afterMarker).Trim();
            }

            // Truncate message to max length (silent — callers can detect via Message.Length == 65536).
            // FindingsParser is a pure static class with no logger; truncation is a rare edge case
            // that only occurs with extremely verbose agent output on a single finding line.
            if (message.Length > 65536)
                message = message[..65536];

            findings.Add(new StructuredFinding
            {
                Severity = severity,
                FilePath = filePath,
                LineNumber = lineNumber,
                Message = message,
                AgentName = agentName
            });
        }

        return findings;
    }

    /// <summary>
    /// Extracts the first file:line reference from text following a severity marker.
    /// Returns (filePath, lineNumber, endIndex) or (null, 0, 0) if no match found.
    /// </summary>
    private static (string? FilePath, int LineNumber, int EndIndex) ExtractFileLineReference(string text)
    {
        // Try each file:line pattern in order, return first match
        // Pattern 1: path:N (colon-separated)
        var match = FileLineColonRegex().Match(text);
        while (match.Success)
        {
            if (IsValidFilePath(match.Groups["path"].Value) && !IsPartOfUrl(text, match.Index)
                && int.TryParse(match.Groups["num"].Value, out var lineNum) && lineNum > 0)
                return (NormalizePath(match.Groups["path"].Value), lineNum, match.Index + match.Length);
            match = match.NextMatch();
        }

        // Pattern 2: path#LN (hash-L-number)
        match = FileLineHashRegex().Match(text);
        while (match.Success)
        {
            if (IsValidFilePath(match.Groups["path"].Value) && !IsPartOfUrl(text, match.Index)
                && int.TryParse(match.Groups["num"].Value, out var lineNum) && lineNum > 0)
                return (NormalizePath(match.Groups["path"].Value), lineNum, match.Index + match.Length);
            match = match.NextMatch();
        }

        // Pattern 3: path (line N) (parenthesized)
        match = FileLineParenRegex().Match(text);
        while (match.Success)
        {
            if (IsValidFilePath(match.Groups["path"].Value) && !IsPartOfUrl(text, match.Index)
                && int.TryParse(match.Groups["num"].Value, out var lineNum) && lineNum > 0)
                return (NormalizePath(match.Groups["path"].Value), lineNum, match.Index + match.Length);
            match = match.NextMatch();
        }

        // Pattern 4: path, line N (comma-separated)
        match = FileLineCommaRegex().Match(text);
        while (match.Success)
        {
            if (IsValidFilePath(match.Groups["path"].Value) && !IsPartOfUrl(text, match.Index)
                && int.TryParse(match.Groups["num"].Value, out var lineNum) && lineNum > 0)
                return (NormalizePath(match.Groups["path"].Value), lineNum, match.Index + match.Length);
            match = match.NextMatch();
        }

        return (null, 0, 0);
    }

    /// <summary>
    /// Validates that a matched path is a legitimate file path (not a URL or email).
    /// A valid file path must contain at least one '/' or '.' and must not start with
    /// http:// or https:// and must not contain '@'.
    /// </summary>
    private static bool IsValidFilePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        // Exclude URLs (full and protocol-relative)
        if (path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("//"))
            return false;

        // Exclude email addresses
        if (path.Contains('@'))
            return false;

        // Must contain at least one '/' or '.'
        return path.Contains('/') || path.Contains('.') || path.Contains('\\');
    }

    /// <summary>
    /// Checks if the matched path in context is actually part of a URL.
    /// Looks at the text preceding the match to detect URL schemes.
    /// </summary>
    private static bool IsPartOfUrl(string fullText, int matchStart)
    {
        // Check if the match is preceded by "://" which indicates it's part of a URL
        if (matchStart >= 3)
        {
            var preceding = fullText[(matchStart - 3)..matchStart];
            if (preceding == "://")
                return true;
        }

        // Check for protocol-relative URLs
        if (matchStart >= 2)
        {
            var preceding = fullText[(matchStart - 2)..matchStart];
            if (preceding == "//")
                return true;
        }

        return false;
    }

    /// <summary>
    /// Strips triple-backtick fenced code block wrapper lines from the input.
    /// Only the fence lines (``` with optional language tag) are removed; content inside is preserved.
    /// </summary>
    private static string StripCodeBlockFences(string input)
    {
        var lines = input.Split('\n');
        var result = new List<string>(lines.Length);

        foreach (var line in lines)
        {
            var trimmed = line.TrimEnd('\r').TrimStart();
            if (IsFenceLine(trimmed))
                continue;
            result.Add(line);
        }

        return string.Join('\n', result);
    }

    /// <summary>
    /// Determines if a trimmed line is a code fence line (``` with optional language identifier).
    /// </summary>
    private static bool IsFenceLine(string trimmedLine)
    {
        if (!trimmedLine.StartsWith("```"))
            return false;

        // A fence line is ``` optionally followed by a language identifier (no other content)
        var afterBackticks = trimmedLine[3..].Trim();
        // Language identifiers are alphanumeric (e.g., ```csharp, ```json, ```text)
        // Empty after backticks is also a fence (closing fence)
        return afterBackticks.Length == 0 || FenceLanguageRegex().IsMatch(afterBackticks);
    }

    private static FindingSeverity ParseSeverity(string marker)
    {
        // marker includes brackets, e.g. "[CRITICAL]"
        var inner = marker[1..^1]; // strip [ and ]
        return inner.ToUpperInvariant() switch
        {
            "CRITICAL" => FindingSeverity.Critical,
            "WARNING" => FindingSeverity.Warning,
            "SUGGESTION" => FindingSeverity.Suggestion,
            _ => FindingSeverity.Suggestion
        };
    }

    /// <summary>
    /// Normalizes file paths to forward slashes and strips leading ./ or / prefixes.
    /// </summary>
    private static string NormalizePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.StartsWith("./"))
            normalized = normalized[2..];
        else if (normalized.StartsWith("/"))
            normalized = normalized[1..];
        return normalized;
    }

    /// <summary>
    /// Strips leading separator patterns from the message text.
    /// Separators: " — ", " - ", ": "
    /// </summary>
    private static string StripLeadingSeparators(string text)
    {
        var trimmed = text;

        // Try each separator pattern at the start (most specific first)
        if (trimmed.StartsWith(" — "))
            trimmed = trimmed[3..];
        else if (trimmed.StartsWith(" - "))
            trimmed = trimmed[3..];
        else if (trimmed.StartsWith(": "))
            trimmed = trimmed[2..];
        else if (trimmed.StartsWith(" —"))
            trimmed = trimmed[2..];
        else if (trimmed.StartsWith(" -"))
            trimmed = trimmed[2..];
        else if (trimmed.StartsWith(":"))
            trimmed = trimmed[1..];

        return trimmed;
    }

    /// <summary>
    /// Matches the first severity marker on a line (case-insensitive).
    /// Matches [CRITICAL], [WARNING], or [SUGGESTION] in any case.
    /// </summary>
    [GeneratedRegex(@"\[(critical|warning|suggestion)\]", RegexOptions.IgnoreCase)]
    private static partial Regex SeverityMarkerRegex();

    /// <summary>
    /// Matches file:line reference in format path:N (e.g., src/Service.cs:42).
    /// Path is a sequence of non-whitespace characters.
    /// </summary>
    [GeneratedRegex(@"(?<path>[^\s:,#(]+):(?<num>\d+)")]
    private static partial Regex FileLineColonRegex();

    /// <summary>
    /// Matches file:line reference in format path#LN (e.g., src/Service.cs#L42).
    /// </summary>
    [GeneratedRegex(@"(?<path>[^\s:,#(]+)#L(?<num>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex FileLineHashRegex();

    /// <summary>
    /// Matches file:line reference in format path (line N) (e.g., src/Service.cs (line 42)).
    /// </summary>
    [GeneratedRegex(@"(?<path>[^\s:,#(]+)\s*\(line\s+(?<num>\d+)\)", RegexOptions.IgnoreCase)]
    private static partial Regex FileLineParenRegex();

    /// <summary>
    /// Matches file:line reference in format path, line N (e.g., src/Service.cs, line 42).
    /// </summary>
    [GeneratedRegex(@"(?<path>[^\s:,#(]+),\s*line\s+(?<num>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex FileLineCommaRegex();

    /// <summary>
    /// Matches valid fence language identifiers (alphanumeric, hyphens, dots, plus signs).
    /// </summary>
    [GeneratedRegex(@"^[\w\-\.\+]+$")]
    private static partial Regex FenceLanguageRegex();
}
