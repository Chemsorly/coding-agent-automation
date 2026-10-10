using System.Text.RegularExpressions;

namespace KiroCliLib.Core;

/// <summary>
/// Shared utility for stripping ANSI escape codes from terminal output.
/// Handles standard ESC-prefixed sequences, plus the two bare forms some CLI tools emit without the
/// ESC byte: colour codes (<c>[32m</c>) and erase-line (<c>[K</c>). The bare forms are matched
/// narrowly so ordinary text such as <c>[Kubernetes docs]</c>, <c>dict[Key]</c> or <c>[1st]</c> stays intact.
/// </summary>
public static partial class AnsiStripper
{
    public static string Strip(string input) => AnsiPattern().Replace(input, string.Empty);

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]|\x1B\].*?\x07|\[(?:\d+;)*\d+m|\[K(?![A-Za-z])")]
    private static partial Regex AnsiPattern();
}
