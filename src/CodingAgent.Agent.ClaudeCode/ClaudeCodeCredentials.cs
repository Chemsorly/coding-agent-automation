using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Agent.ClaudeCode;

/// <summary>
/// A credential handed to the Claude Code CLI.
/// </summary>
/// <param name="EnvironmentVariable">The CLI's own variable name for the credential.</param>
/// <param name="Value">The secret value.</param>
/// <param name="BillingMode">How calls made with it are paid for (<see cref="AgentBillingModes"/>).</param>
internal sealed record ClaudeCredential(string EnvironmentVariable, string Value, string BillingMode)
{
    // Keep the secret out of logs and exception messages that format the record.
    public override string ToString() => $"{EnvironmentVariable} ({BillingMode})";
}

/// <summary>
/// Selects the credential the Claude Code CLI runs with and puts exactly that one into its environment.
/// </summary>
/// <remarks>
/// The CLI picks credentials in a fixed order and an API key always beats the subscription token
/// (https://code.claude.com/docs/en/authentication#authentication-precedence), so every other
/// credential variable is removed from the child environment before the selected one is set.
/// </remarks>
internal static class ClaudeCodeCredentials
{
    internal const string AnthropicApiKey = "ANTHROPIC_API_KEY";
    internal const string AnthropicAuthToken = "ANTHROPIC_AUTH_TOKEN";
    internal const string ClaudeCodeOAuthToken = "CLAUDE_CODE_OAUTH_TOKEN";

    /// <summary>Every variable the CLI (or the pipeline on its behalf) reads an Anthropic credential from.</summary>
    internal static readonly string[] CredentialVariables =
    [
        AnthropicApiKey,
        AnthropicAuthToken,
        ClaudeCodeOAuthToken,
        AgentDefaults.EnvClaudeApiKey,
        AgentDefaults.EnvClaudeOAuthToken
    ];

    internal static bool IsCredentialVariable(string name) =>
        CredentialVariables.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the credential for <paramref name="authMode"/>. The pipeline variables injected from
    /// the chart Secret (<see cref="AgentDefaults.EnvClaudeApiKey"/>, <see cref="AgentDefaults.EnvClaudeOAuthToken"/>)
    /// win; the CLI's own variables are the fallback for agents run outside Kubernetes.
    /// </summary>
    /// <returns>
    /// The credential, or null when the mode's credential is not configured. In auto mode null means
    /// neither is configured and the CLI falls back to a stored <c>/login</c>, if any.
    /// </returns>
    internal static ClaudeCredential? Resolve(string? authMode, Func<string, string?> getEnvironmentVariable)
    {
        ArgumentNullException.ThrowIfNull(getEnvironmentVariable);

        var apiKey = FirstNonEmpty(
            getEnvironmentVariable(AgentDefaults.EnvClaudeApiKey), getEnvironmentVariable(AnthropicApiKey));
        var oauthToken = FirstNonEmpty(
            getEnvironmentVariable(AgentDefaults.EnvClaudeOAuthToken), getEnvironmentVariable(ClaudeCodeOAuthToken));

        var apiKeyCredential = apiKey is null ? null : new ClaudeCredential(AnthropicApiKey, apiKey, AgentBillingModes.Api);
        var subscriptionCredential = oauthToken is null
            ? null
            : new ClaudeCredential(ClaudeCodeOAuthToken, oauthToken, AgentBillingModes.Subscription);

        return ClaudeCodeAuthModes.Normalize(authMode) switch
        {
            ClaudeCodeAuthModes.ApiKey => apiKeyCredential,
            ClaudeCodeAuthModes.Subscription => subscriptionCredential,
            _ => apiKeyCredential ?? subscriptionCredential
        };
    }

    /// <summary>
    /// Removes every credential variable from <paramref name="environment"/> and then sets
    /// <paramref name="credential"/>, if any, under the CLI's variable name.
    /// </summary>
    internal static void Apply(IDictionary<string, string?> environment, ClaudeCredential? credential)
    {
        ArgumentNullException.ThrowIfNull(environment);

        foreach (var key in environment.Keys.Where(IsCredentialVariable).ToList())
            environment.Remove(key);

        if (credential is not null)
            environment[credential.EnvironmentVariable] = credential.Value;
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
}
