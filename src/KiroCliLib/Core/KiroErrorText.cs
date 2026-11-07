namespace KiroCliLib.Core;

/// <summary>What a kiro-cli error line says about the provider.</summary>
public enum KiroErrorKind
{
    /// <summary>Nothing known: not an error line, or an error this classifier does not know.</summary>
    None,

    /// <summary>The Kiro subscription's monthly usage or overage limit is reached.</summary>
    QuotaExhausted,

    /// <summary>Kiro throttled the request (transient).</summary>
    RateLimited,
}

/// <summary>
/// Spec 056 req 5.2: classifies kiro-cli's own error lines. Only a line that starts with <c>error:</c>
/// (case-insensitive, after leading white space) counts: the model's text and tool output can contain the same
/// phrases. The caller applies the result only when the call failed.
/// </summary>
public static class KiroErrorText
{
    private const string ErrorLinePrefix = "error:";

    private static readonly string[] QuotaPhrases = ["monthly usage limit", "overage limit"];
    private static readonly string[] RateLimitPhrases = ["request was throttled", "too many requests"];

    /// <summary>The kind of <paramref name="line"/> (ANSI codes already stripped).</summary>
    public static KiroErrorKind Classify(string? line)
    {
        var trimmed = line?.TrimStart();
        if (trimmed is null || !trimmed.StartsWith(ErrorLinePrefix, StringComparison.OrdinalIgnoreCase))
            return KiroErrorKind.None;
        if (QuotaPhrases.Any(p => trimmed.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return KiroErrorKind.QuotaExhausted;
        if (RateLimitPhrases.Any(p => trimmed.Contains(p, StringComparison.OrdinalIgnoreCase)))
            return KiroErrorKind.RateLimited;
        return KiroErrorKind.None;
    }
}
