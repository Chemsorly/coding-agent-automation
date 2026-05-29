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
    /// A line is valid if it falls within any @@ hunk range for that file.
    /// </summary>
    public static IReadOnlyDictionary<string, HashSet<int>> ParseValidLines(string? diffText)
    {
        if (string.IsNullOrEmpty(diffText))
            return new Dictionary<string, HashSet<int>>();

        var result = new Dictionary<string, HashSet<int>>(StringComparer.OrdinalIgnoreCase);
        var lines = diffText.Split('\n');

        string? currentFile = null;

        foreach (var line in lines)
        {
            // Detect file boundary: +++ b/path/to/file
            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                var path = ExtractNewFilePath(line);
                if (path is null)
                {
                    // Deleted file (+++ /dev/null) — no valid RIGHT-side lines
                    currentFile = null;
                    continue;
                }

                currentFile = NormalizePath(path);
                // Ensure the file entry exists (may already exist from a previous hunk section)
                if (!result.ContainsKey(currentFile))
                    result[currentFile] = new HashSet<int>();

                continue;
            }

            // Detect hunk header: @@ -oldStart,oldSize +newStart,newSize @@
            if (currentFile is not null && line.StartsWith("@@", StringComparison.Ordinal))
            {
                var match = HunkHeaderRegex().Match(line);
                if (!match.Success)
                    continue;

                var newStart = int.Parse(match.Groups[3].Value);
                var newSize = match.Groups[4].Success ? int.Parse(match.Groups[4].Value) : 1;

                var validLines = result[currentFile];
                for (var i = newStart; i < newStart + newSize; i++)
                {
                    validLines.Add(i);
                }
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
