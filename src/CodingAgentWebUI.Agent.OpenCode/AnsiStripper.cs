using System.Text.RegularExpressions;

namespace CodingAgentWebUI.Agent.OpenCode;

/// <summary>
/// Strips ANSI escape codes from terminal output. Inlined from KiroCliLib to avoid
/// pulling in the full KiroCliLib dependency for a single regex utility.
/// </summary>
internal static partial class AnsiStripper
{
    internal static string Strip(string input) => AnsiPattern().Replace(input, string.Empty);

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]|\x1B\].*?\x07|\[(?:\d+;)*\d+[A-Za-z]|\[K")]
    private static partial Regex AnsiPattern();
}
