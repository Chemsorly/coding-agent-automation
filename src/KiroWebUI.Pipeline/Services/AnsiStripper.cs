namespace KiroWebUI.Pipeline.Services;

/// <summary>
/// Delegates to KiroCliLib.Core.AnsiStripper for ANSI escape code stripping.
/// Kept as a thin wrapper to avoid updating all existing references in KiroWebUI.
/// </summary>
public static class AnsiStripper
{
    public static string Strip(string input) => KiroCliLib.Core.AnsiStripper.Strip(input);
}
