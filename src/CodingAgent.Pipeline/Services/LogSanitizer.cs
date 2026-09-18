namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Escapes control characters from user-supplied strings before they are written to
/// log entries or HTTP response bodies, preventing log injection (log forging) attacks.
/// </summary>
// TODO [WARNING]: The summary XML doc says "Escapes control characters" (plural/general) but the
// implementation only handles \r and \n. Other control characters that can affect some log
// parsers or terminals (e.g. \0 null byte, \t tab used as field delimiter in CEF/syslog,
// ANSI escape sequences starting with \x1b) are passed through unmodified. The mismatch
// between the stated contract and the implementation could mislead future callers. If this
// utility is extended, consider also stripping/escaping other ASCII control characters
// (U+0000–U+001F, U+007F) and ANSI CSI sequences for defence-in-depth.
public static class LogSanitizer
{
    /// <summary>
    /// Escapes newline characters in <paramref name="value"/> so they cannot inject
    /// fake log lines into the audit trail. Returns an empty string for <see langword="null"/>.
    /// </summary>
    /// <param name="value">A user-controlled string (e.g. an agent selector from the database).</param>
    /// <returns>
    /// The input with <c>\r</c> replaced by <c>\\r</c> and <c>\n</c> replaced by <c>\\n</c>,
    /// or an empty string if <paramref name="value"/> is <see langword="null"/>.
    /// </returns>
    public static string SanitizeForLog(string? value)
        => value?.Replace("\r", "\\r", StringComparison.Ordinal)
                 .Replace("\n", "\\n", StringComparison.Ordinal) ?? "";
}
