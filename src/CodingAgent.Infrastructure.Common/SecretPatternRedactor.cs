using System.Text.RegularExpressions;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Redacts common secret shapes from text that leaves an agent or reaches the API from outside: <c>key=value</c> pairs
/// whose key names a secret, bearer tokens, GitHub tokens and Anthropic/OpenAI style API keys. Used where the known
/// secret values are not at hand (the API side); the agent masks its own known values with <see cref="SecretMasker"/>.
/// </summary>
public static partial class SecretPatternRedactor
{
    /// <summary>
    /// <paramref name="text"/> with every matched secret replaced by <c>***</c>. A pattern that times out leaves the
    /// text as it was before that pattern; callers that must not keep such text use <see cref="TryRedact"/>.
    /// </summary>
    public static string Redact(string text) => TryRedact(text) ?? "***";

    /// <summary><paramref name="text"/> with every matched secret replaced, or null when a pattern timed out.</summary>
    public static string? TryRedact(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        try
        {
            text = KeyValueSecretRegex().Replace(text, "$1$2***");
            text = BearerTokenRegex().Replace(text, "Bearer ***");
            text = GitHubTokenRegex().Replace(text, "***");
            return ApiKeyRegex().Replace(text, "***");
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"(?i)(token|password|passwd|secret|api[_\-]?key|authorization)(\s*[=:]\s*)(bearer\s+)?\S+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex KeyValueSecretRegex();

    [GeneratedRegex(@"(?i)\bbearer\s+\S+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BearerTokenRegex();

    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9]{20,}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex GitHubTokenRegex();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9_\-]{20,}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ApiKeyRegex();
}
