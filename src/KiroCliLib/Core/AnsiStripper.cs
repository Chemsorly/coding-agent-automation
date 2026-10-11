using System.Text.RegularExpressions;

namespace KiroCliLib.Core;

/// <summary>
/// Shared utility for stripping ANSI escape codes from terminal output.
/// Handles standard ESC-prefixed sequences, plus the bare forms some CLI tools emit without the ESC
/// byte: colour codes (<c>[32m</c>) anywhere, and cursor/erase codes (<c>[K</c>, <c>[2K</c>, <c>[1G</c>)
/// only at the end of a line or next to another code, where they cannot be text. Ordinary text such
/// as <c>[Kubernetes docs]</c>, <c>Dict[K, V]</c>, <c>[3D printing]</c> or <c>[1st]</c> stays intact.
/// </summary>
public static partial class AnsiStripper
{
    public static string Strip(string input) => AnsiPattern().Replace(input, string.Empty);

    [GeneratedRegex(
        @"\x1B\[[0-9;]*[A-Za-z]|\x1B\].*?\x07|\[(?:\d+;)*\d+m" +
        @"|\[\d*[A-DGHJK](?=$|\[|\x1B|\r)" +
        @"|(?<=\x1B\[[0-9;]*[A-Za-z]|\[\d*[A-DGHJKm])\[\d*[A-DGHJK]")]
    private static partial Regex AnsiPattern();
}
