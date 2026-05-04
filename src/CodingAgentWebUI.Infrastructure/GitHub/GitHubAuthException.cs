namespace CodingAgentWebUI.Infrastructure.GitHub;

/// <summary>
/// Distinguishes the kind of authentication failure that occurred during GitHub App auth.
/// </summary>
public enum GitHubAuthErrorKind
{
    /// <summary>The base64-encoded private key could not be decoded or is not a valid PEM key.</summary>
    PrivateKeyDecodeFailure,

    /// <summary>The JWT-to-installation-token exchange with GitHub failed.</summary>
    TokenExchangeFailure
}

/// <summary>
/// Structured exception for GitHub App authentication failures.
/// Replaces generic <see cref="InvalidOperationException"/> throws in <see cref="GitHubAppAuthService"/>
/// to enable reliable catch-by-kind matching instead of fragile message substring matching.
/// </summary>
public sealed class GitHubAuthException : Exception
{
    /// <summary>
    /// Gets the kind of authentication error that occurred.
    /// </summary>
    public GitHubAuthErrorKind ErrorKind { get; }

    public GitHubAuthException(GitHubAuthErrorKind errorKind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorKind = errorKind;
    }
}
