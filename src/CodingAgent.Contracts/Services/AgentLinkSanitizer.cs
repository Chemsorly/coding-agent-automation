namespace CodingAgent.Contracts.Services;

/// <summary>
/// Validates that a link coming from an untrusted agent pod is an absolute https URL before
/// storing it in the database (spec 054, req 12.8). Returns the value unchanged when it
/// is a valid absolute https URL, otherwise null.
/// </summary>
public static class AgentLinkSanitizer
{
    /// <summary>
    /// Returns <paramref name="url"/> when it is a valid absolute https URL; otherwise null.
    /// </summary>
    public static string? HttpsOrNull(string? url)
    {
        if (string.IsNullOrEmpty(url)) return null;
        return Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps
            ? url
            : null;
    }
}
