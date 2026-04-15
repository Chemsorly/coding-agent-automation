using System.Text.RegularExpressions;

namespace KiroWebUI.Pipeline.Services;

/// <summary>
/// Shared utility for stripping ANSI escape codes from terminal output.
/// Handles both standard ESC[...m sequences and bare bracket sequences
/// emitted by some CLI tools without the ESC byte prefix.
/// </summary>
public static partial class AnsiStripper
{
    public static string Strip(string input) => AnsiPattern().Replace(input, string.Empty);

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]|\x1B\].*?\x07|\[(?:\d+;)*\d*[A-Za-z]|\[K")]
    private static partial Regex AnsiPattern();
}
