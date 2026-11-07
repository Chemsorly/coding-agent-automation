namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Spec 056 DD16: the credential an agent profile names is not configured in the agent pod (for example a Claude
/// subscription profile without a token). The agent records it as an <c>auth_failed</c> provider failure, which pauses
/// that credential.
/// </summary>
public sealed class ProviderCredentialMissingException : Exception
{
    public ProviderCredentialMissingException(string credential)
        : base($"No {AgentCredentials.DisplayName(credential)} is configured")
    {
        Credential = credential;
    }

    public ProviderCredentialMissingException(string credential, string message)
        : base(message)
    {
        Credential = credential;
    }

    /// <summary>One of <see cref="AgentCredentials.All"/>.</summary>
    public string Credential { get; }
}
