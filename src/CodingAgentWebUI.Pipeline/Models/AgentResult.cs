namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Classifies the cause of an agent failure to allow the pipeline to distinguish
/// provider-side transient errors from code-level failures.
/// </summary>
public enum AgentErrorCategory
{
    /// <summary>No classification — default for success results and unclassified failures.</summary>
    None,

    /// <summary>HTTP 429: the upstream LLM provider is rate-limiting requests.</summary>
    ProviderRateLimit,

    /// <summary>HTTP 503: the upstream LLM provider is temporarily overloaded.</summary>
    ProviderOverload,

    /// <summary>HTTP 401/403: authentication or authorisation failure — permanent until credentials are fixed.</summary>
    PermanentAuthFailure,
}

public sealed class AgentResult
{
    public required int ExitCode { get; init; }
    public required IReadOnlyList<string> OutputLines { get; init; }
    public bool Success => ExitCode == 0;

    /// <summary>Token usage delta for this specific invocation, or null if unavailable.</summary>
    public TokenUsage? Usage { get; init; }

    /// <summary>Cost in USD for this invocation, or null if unavailable/unknown.</summary>
    public decimal? Cost { get; init; }

    /// <summary>
    /// Classifies the cause of a failure when the agent call was rejected by the provider
    /// rather than failing due to a code-level problem. Defaults to <see cref="AgentErrorCategory.None"/>.
    /// The QG retry loop uses this to skip <c>RetryCount</c> increments on transient provider errors.
    /// </summary>
    public AgentErrorCategory ErrorCategory { get; init; } = AgentErrorCategory.None;
}
