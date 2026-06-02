using System.Text.RegularExpressions;

namespace CodingAgentWebUI.Pipeline.CodeReview;

/// <summary>
/// Parses unified diff output to extract valid line ranges per file.
/// Used to validate inline review comments before submission — GitHub's API
/// only accepts comments on lines that appear within diff hunk ranges.
/// </summary>
internal static partial class DiffHunkParser
{
    /// <summary>
    /// Parses a unified diff string and returns a dictionary mapping file paths
    /// to their valid line number sets (RIGHT side — new file state).
    /// Only lines that were actually added or modified ('+' prefix in the diff) are
    /// considered valid. Context lines (' ' prefix) are NOT valid targets for inline
    /// comments because GitLab's API rejects position-based discussions on context lines.
    /// </summary>
    public static IReadOnlyDictionary<string, HashSet<int>> ParseValidLines(string? diffText)
    {
        if (string.IsNullOrEmpty(diffText))
            return new Dictionary<string, HashSet<int>>();

        var result = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var lines = diffText.Split('\n');

        string? currentFile = null;
        var inHunk = false;
        var currentNewLine = 0;

        foreach (var rawLine in lines)
        {
            // Normalize Windows line endings — prevents \r from breaking prefix detection
            var line = rawLine.TrimEnd('\r');
            // Detect file boundary: +++ b/path/to/file
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var path = ExtractNewFilePath(line);
                if (path is null)
                {
                    // Deleted file (+++ /dev/null) — no valid RIGHT-side lines
                    currentFile = null;
                    inHunk = false;
                    continue;
                }

                currentFile = NormalizePath(path);
                // Ensure the file entry exists (may already exist from a previous hunk section)
                if (!result.ContainsKey(currentFile))
                    result[currentFile] = new HashSet<int>();

                inHunk = false;
                continue;
            }

            // Detect new diff entry — resets hunk state
            if (line.StartsWith("diff ", StringComparison.Ordinal))
            {
                inHunk = false;
                continue;
            }

            // Detect hunk header: @@ -oldStart,oldSize +newStart,newSize @@
            if (currentFile is not null && line.StartsWith("@@", StringComparison.Ordinal))
            {
                var match = HunkHeaderRegex().Match(line);
                if (!match.Success)
                    continue;

                currentNewLine = int.Parse(match.Groups[3].Value);
                inHunk = true;
                continue;
            }

            // Walk hunk content lines to identify actually-changed lines
            if (inHunk && currentFile is not null)
            {
                if (line.StartsWith('+'))
                {
                    // Added/modified line — valid target for inline comments
                    result[currentFile].Add(currentNewLine);
                    currentNewLine++;
                }
                else if (line.StartsWith('-'))
                {
                    // Deleted line — does NOT advance new-line counter
                    // Not a valid target on the RIGHT side
                }
                else if (line.StartsWith(' ') || (line.Length == 0 && currentNewLine > 0))
                {
                    // Context line — advances counter but NOT a valid comment target
                    currentNewLine++;
                }
                // else: unknown line (e.g., "\ No newline at end of file") — skip without advancing
            }
        }

        return result;
    }

    /// <summary>
    /// Extracts the new file path from a "+++ b/path" line.
    /// Returns null for deleted files (+++ /dev/null).
    /// </summary>
    private static string? ExtractNewFilePath(string line)
    {
        // "+++ /dev/null" means the file was deleted
        if (line.StartsWith("+++ /dev/null", StringComparison.Ordinal))
            return null;

        // "+++ b/path/to/file" — strip the "b/" prefix
        if (line.StartsWith("+++ b/", StringComparison.Ordinal))
            return line[6..];

        // Fallback: strip "+++ " prefix (handles edge cases)
        return line[4..].TrimEnd('\r');
    }

    /// <summary>
    /// Normalizes a file path to use forward slashes consistently.
    /// </summary>
    private static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimEnd('\r');

    [GeneratedRegex(@"@@ -(\d+)(?:,(\d+))? \+(\d+)(?:,(\d+))? @@")]
    private static partial Regex HunkHeaderRegex();
}
