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

            var depth = 0;
            var inString = false;
            var escaped = false;

            for (var i = braceStart; i < responseText.Length; i++)
            {
                var c = responseText[i];

                if (escaped)
                {
                    escaped = false;
                    continue;
                }

                if (c == '\\' && inString)
                {
                    escaped = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = !inString;
                    continue;
                }

                if (inString)
                    continue;

                if (c == '{') depth++;
                else if (c == '}') depth--;

                if (depth == 0)
                {
                    var candidate = responseText[braceStart..(i + 1)];
                    if (candidateValidator is null || candidateValidator(candidate))
                        return candidate;
                    searchStart = i + 1;
                    break;
                }
            }

            if (depth != 0)
                break;
        }

        return null;
    }
}
