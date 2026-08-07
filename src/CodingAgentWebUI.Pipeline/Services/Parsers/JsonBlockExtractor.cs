using System.Text.RegularExpressions;

namespace CodingAgentWebUI.Pipeline.Services.Parsers;

/// <summary>
/// Extracts JSON object blocks from agent response text.
/// Tries fenced code blocks first, then falls back to brace-depth tracking.
/// </summary>
public static partial class JsonBlockExtractor
{
    [GeneratedRegex(@"```(?:json)?\s*\n([\s\S]*?)\n\s*```", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FencedJsonBlockPattern();

    /// <summary>
    /// Extracts the first JSON object block from the response text.
    /// Prefers fenced code blocks over bare JSON objects.
    /// The <paramref name="candidateValidator"/> is applied only to the bare-JSON fallback path;
    /// fenced blocks are returned if they start with '{' without further validation.
    /// </summary>
    public static string? Extract(string responseText, Func<string, bool>? candidateValidator = null)
    {
        if (string.IsNullOrEmpty(responseText))
            return null;

        // Try fenced JSON block first
        var fencedMatch = FencedJsonBlockPattern().Match(responseText);
        if (fencedMatch.Success)
        {
            var content = fencedMatch.Groups[1].Value.Trim();
            if (content.StartsWith('{'))
                return content;
        }

        // Fall back to bare JSON object using brace-depth tracking
        var searchStart = 0;
        while (searchStart < responseText.Length)
        {
            var braceStart = responseText.IndexOf('{', searchStart);
            if (braceStart < 0)
                break;

            var endIndex = FindMatchingBrace(responseText, braceStart);
            if (endIndex < 0)
                break;

            var candidate = responseText[braceStart..(endIndex + 1)];
            if (candidateValidator is null || candidateValidator(candidate))
                return candidate;

            searchStart = endIndex + 1;
        }

        return null;
    }

    /// <summary>
    /// Finds the index of the closing brace that matches the opening brace at <paramref name="start"/>.
    /// Returns -1 if the string ends before the brace is closed.
    /// </summary>
    private static int FindMatchingBrace(string text, int start)
    {
        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];

            if (escaped) { escaped = false; continue; }
            if (c == '\\' && inString) { escaped = true; continue; }
            if (c == '"') { inString = !inString; continue; }
            if (inString) continue;

            if (c == '{') depth++;
            else if (c == '}') depth--;

            if (depth == 0)
                return i;
        }

        return -1; // unclosed brace
    }
}
