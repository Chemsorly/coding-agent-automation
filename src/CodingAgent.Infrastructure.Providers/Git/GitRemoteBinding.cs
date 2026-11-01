using CodingAgent.Infrastructure.Resilience;
using LibGit2Sharp;
using LibGit2Sharp.Handlers;
using Serilog;

namespace CodingAgent.Infrastructure.Git;

/// <summary>
/// Keeps a repository's token on the provider's own server: what git may contact comes from the provider's
/// configuration, never from the workspace, whose git config the agent CLI can write.
/// </summary>
internal static class GitRemoteBinding
{
    internal const string StandardFetchRefSpec = "+refs/heads/*:refs/remotes/origin/*";

    // Keys are normalized by libgit2: lower-case section and variable, for example "url.https://x/.insteadof".
    private const string OverridingKeys = @"^(url\.|http\.|include\.|includeif\.|remote\.origin\.)";

    /// <summary>Credentials for <paramref name="boundUrl"/>'s server only; any other server is refused.</summary>
    internal static CredentialsHandler CredentialsFor(string boundUrl, string tokenUsername, string token) =>
        (url, _, _) =>
        {
            ThrowUnlessSameServer(url, boundUrl);
            return new UsernamePasswordCredentials { Username = tokenUsername, Password = token };
        };

    /// <summary>
    /// Removes everything from the repository's own config that can redirect git, then writes the canonical
    /// remote. Reads nothing from the config but the names of the keys it removes.
    /// </summary>
    internal static void ResetRemoteConfig(Repository repo, string canonicalCloneUrl)
    {
        var config = repo.Config;
        var keys = config.Find(OverridingKeys, ConfigurationLevel.Local)
            .Select(entry => entry.Key)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        foreach (var key in keys)
            config.UnsetAll(key, ConfigurationLevel.Local);

        config.Set("remote.origin.url", canonicalCloneUrl, ConfigurationLevel.Local);
        config.Set("remote.origin.fetch", StandardFetchRefSpec, ConfigurationLevel.Local);
    }

    private static void ThrowUnlessSameServer(string url, string boundUrl)
    {
        var actual = Uri.TryCreate(url, UriKind.Absolute, out var parsed) ? parsed : null;
        if (actual is not null
            && Uri.TryCreate(boundUrl, UriKind.Absolute, out var expected)
            && string.Equals(actual.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase)
            && string.Equals(actual.Host, expected.Host, StringComparison.OrdinalIgnoreCase)
            && actual.Port == expected.Port)
        {
            return;
        }

        var host = actual?.Host ?? "unknown";
        Log.Warning("Refused git credentials for remote host {RefusedRemoteHost}", host);
        throw new InvalidOperationException($"{PushErrorClassifier.RemoteRefusedMarker} '{host}'.");
    }
}
