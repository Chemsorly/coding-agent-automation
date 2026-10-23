namespace CodingAgent.Pipeline.Models;

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

    /// <summary>
    /// The agent CLI refused its own configuration before doing any work (for example an agent name it
    /// does not know) — permanent until the agent provider's settings are fixed.
    /// </summary>
    ProviderMisconfigured,

    /// <summary>
    /// The provider refused the call for billing reasons, for example the API credit balance is too low —
    /// permanent until the account is topped up or the profile switches to another auth mode.
    /// </summary>
    ProviderBillingLimit,
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
    /// Further usage the provider reports for this invocation (turns, billing mode, per-model
    /// breakdown, rate limits), or null when the provider reports none.
    /// </summary>
    public AgentUsageDetails? UsageDetails { get; init; }

    /// <summary>
    /// Classifies the cause of a failure when the agent call was rejected by the provider
    /// rather than failing due to a code-level problem. Defaults to <see cref="AgentErrorCategory.None"/>.
    /// The QG retry loop uses this to skip <c>RetryCount</c> increments on transient provider errors.
    /// </summary>
    public AgentErrorCategory ErrorCategory { get; init; } = AgentErrorCategory.None;

    /// <summary>
    /// MCP and web tool calls of this invocation, keyed <c>server/tool</c> (<see cref="McpToolKeys"/>).
    /// Built-in file and shell tools are not counted. Null when the provider reported none.
    /// </summary>
    public IReadOnlyDictionary<string, McpCallCounts>? ToolCalls { get; init; }

    /// <summary>
    /// MCP servers this invocation could not use, by name, with a <see cref="McpServerStatuses"/> value.
    /// Null when there were none or the provider cannot tell.
    /// </summary>
    public IReadOnlyDictionary<string, string>? UnavailableMcpServers { get; init; }
}
