using MessagePack;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// How an agent's LLM calls are paid for. Tag values for usage metrics and <see cref="PhaseUsage.BillingMode"/>.
/// </summary>
public static class AgentBillingModes
{
    /// <summary>Billed per token through an API key; the reported cost is what is billed.</summary>
    public const string Api = "api";

    /// <summary>Covered by a flat subscription plan; the reported cost is an estimate, not a bill.</summary>
    public const string Subscription = "subscription";

    /// <summary>The provider did not say how its calls are paid for.</summary>
    public const string Unknown = "unknown";

    /// <summary>All closed-set values, for metric pre-initialization.</summary>
    public static readonly string[] All = [Api, Subscription, Unknown];
}

/// <summary>
/// Usage a provider reports for one agent invocation beyond <see cref="AgentResult.Usage"/> and
/// <see cref="AgentResult.Cost"/>. Every field is a delta for that invocation.
/// </summary>
public sealed record AgentUsageDetails
{
    /// <summary>How the calls were paid for (<see cref="AgentBillingModes"/>), or null if unknown.</summary>
    public string? BillingMode { get; init; }

    /// <summary>Agent turns (model round trips) in the invocation.</summary>
    public int Turns { get; init; }

    /// <summary>Time spent waiting for the model API, in seconds.</summary>
    public double ApiDurationSeconds { get; init; }

    /// <summary>Web search requests the model made.</summary>
    public int WebSearchRequests { get; init; }

    /// <summary>Usage per model that served the invocation (the main model, subagents, helper models).</summary>
    public IReadOnlyDictionary<string, AgentModelUsage> ModelUsage { get; init; } =
        new Dictionary<string, AgentModelUsage>();

    /// <summary>Subscription rate-limit state seen during the invocation.</summary>
    public IReadOnlyList<AgentRateLimitObservation> RateLimits { get; init; } = [];
}

/// <summary>Token and cost usage for one model within an agent invocation.</summary>
public sealed record AgentModelUsage
{
    public long InputTokens { get; init; }

    /// <summary>Output tokens, excluding <see cref="ReasoningTokens"/>.</summary>
    public long OutputTokens { get; init; }

    public long ReasoningTokens { get; init; }

    public long CacheReadTokens { get; init; }

    public long CacheWriteTokens { get; init; }

    public int WebSearchRequests { get; init; }

    /// <summary>Cost in USD as the provider estimates it, or null if not reported.</summary>
    public decimal? CostUsd { get; init; }
}

/// <summary>
/// One subscription rate-limit reading (Claude Code <c>rate_limit_event</c>).
/// Wire-serialized in <see cref="JobCompletionPayload.RateLimits"/>.
/// </summary>
[MessagePackObject]
public sealed record AgentRateLimitObservation
{
    /// <summary>Normalized provider tag (e.g. "claude").</summary>
    [Key(0)]
    public string? Provider { get; init; }

    /// <summary>Rate-limit window, e.g. "five_hour", "seven_day", "seven_day_opus", "overage".</summary>
    [Key(1)]
    public string Window { get; init; } = "unknown";

    /// <summary>"allowed", "allowed_warning" or "rejected".</summary>
    [Key(2)]
    public string Status { get; init; } = "unknown";

    /// <summary>Fraction of the window used (0.0–1.0), when reported.</summary>
    [Key(3)]
    public double? Utilization { get; init; }

    /// <summary>When the window resets, when reported.</summary>
    [Key(4)]
    public DateTimeOffset? ResetsAt { get; init; }
}
