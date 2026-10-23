namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Thrown when an agent call failed because its model provider was unavailable (rate limit,
/// overload, rejected credentials, a CLI that refused its configuration) rather than because of the
/// work. Ends the analysis retry loop: retrying at once cannot help, and the issue is not at fault.
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
            || IsPermanentProviderFailure(category);

    /// <summary>
    /// Whether <paramref name="category"/> fails every call until an operator fixes the credentials or
    /// the provider settings, so no later call in the run can succeed either.
    /// </summary>
    public static bool IsPermanentProviderFailure(AgentErrorCategory category) =>
        category is AgentErrorCategory.PermanentAuthFailure
            or AgentErrorCategory.ProviderMisconfigured
            or AgentErrorCategory.ProviderBillingLimit;

    /// <summary>
    /// The failure reason for a provider failure: the category and exit code, then the last output
    /// line, where a CLI that refused its configuration says why.
    /// </summary>
    public static string Describe(AgentResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        var reason = $"Agent provider unavailable ({result.ErrorCategory}), exit code {result.ExitCode}";
        var lastLine = result.OutputLines.LastOrDefault(l => !string.IsNullOrWhiteSpace(l));
        return lastLine is null ? reason : $"{reason}: {lastLine.Trim()}";
    }
}
