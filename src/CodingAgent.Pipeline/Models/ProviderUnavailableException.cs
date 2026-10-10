namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Thrown when an agent call failed because its model provider was unavailable (rate limit,
/// overload, rejected credentials) rather than because of the work. Ends the analysis retry loop:
/// retrying at once cannot help, and the issue is not at fault.
/// </summary>
public sealed class ProviderUnavailableException : Exception
{
    public ProviderUnavailableException() : base("The agent's model provider is unavailable.") { }

    public ProviderUnavailableException(string message) : base(message) { }

    public ProviderUnavailableException(string message, Exception? innerException)
        : base(message, innerException) { }

    public ProviderUnavailableException(AgentErrorCategory category, string message) : base(message)
    {
        Category = category;
    }

    /// <summary>What the provider reported.</summary>
    public AgentErrorCategory Category { get; }

    /// <summary>Whether <paramref name="category"/> means the provider, not the work, failed the call.</summary>
    public static bool IsProviderFailure(AgentErrorCategory category) =>
        category is AgentErrorCategory.ProviderRateLimit
            or AgentErrorCategory.ProviderOverload
            or AgentErrorCategory.PermanentAuthFailure;
}
