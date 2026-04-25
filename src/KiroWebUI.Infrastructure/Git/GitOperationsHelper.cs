using LibGit2Sharp;
using LibGit2Sharp.Handlers;

namespace KiroWebUI.Infrastructure.Git;

/// <summary>
/// Common LibGit2Sharp helper methods shared across providers and services.
/// </summary>
// TODO: [ARC-10] GitHubRepositoryProvider still inlines credentials/options instead of using these helpers
internal static class GitOperationsHelper
{
    /// <summary>Creates the standard pipeline commit signature.</summary>
    public static Signature CreatePipelineSignature()
        => new("KiroWebUI Pipeline", "pipeline@kiro.dev", DateTimeOffset.UtcNow);

    /// <summary>Creates credentials for GitHub token-based authentication.</summary>
    internal static UsernamePasswordCredentials CreateTokenCredentials(string token)
        => new() { Username = "x-access-token", Password = token };

    /// <summary>Creates fetch options with token-based credentials.</summary>
    internal static FetchOptions CreateFetchOptions(string token)
        => new() { CredentialsProvider = (_, _, _) => CreateTokenCredentials(token) };

    /// <summary>Creates push options with token-based credentials.</summary>
    internal static PushOptions CreatePushOptions(string token, PushStatusErrorHandler? onError = null)
        => new()
        {
            CredentialsProvider = (_, _, _) => CreateTokenCredentials(token),
            OnPushStatusError = onError ?? (_ => { })
        };
}
