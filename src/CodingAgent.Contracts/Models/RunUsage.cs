namespace CodingAgent.Pipeline.Models;

/// <summary>One run's or one phase's usage, as the UI shows it: the reported cost and the token buckets.</summary>
public sealed record RunUsage
{
    /// <summary>Reported cost in USD, or null when the provider reported none.</summary>
    public decimal? Cost { get; init; }

    /// <summary>Input + output + thinking tokens; cache reads and writes are not included.</summary>
    public long Tokens { get; init; }

    public long InputTokens { get; init; }

    public long OutputTokens { get; init; }

    public long ReasoningTokens { get; init; }

    public long CacheReadTokens { get; init; }

    public long CacheWriteTokens { get; init; }

    /// <summary><see cref="AgentBillingModes.Api"/>, <see cref="AgentBillingModes.Subscription"/>, or null when unknown.</summary>
    public string? BillingMode { get; init; }

    public string? Model { get; init; }

    /// <summary>
    /// Creates a <see cref="RunUsage"/> from a <see cref="PipelineRunSummary"/>.
    /// Token split fields are summed over <see cref="PipelineRunSummary.PhaseBreakdown"/> values;
    /// billing mode is resolved across phases using the rule: none known → null,
    /// all subscription → subscription, otherwise → api.
    /// </summary>
    public static RunUsage From(PipelineRunSummary run)
    {
        ArgumentNullException.ThrowIfNull(run);

        long inputTokens = 0;
        long outputTokens = 0;
        long reasoningTokens = 0;

        if (run.PhaseBreakdown is { } breakdown)
        {
            foreach (var phase in breakdown.Values)
            {
                inputTokens += phase.InputTokens;
                outputTokens += phase.OutputTokens;
                reasoningTokens += phase.ReasoningTokens;
            }
        }

        return new RunUsage
        {
            Cost = run.TotalCost,
            Tokens = run.TotalTokens,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            ReasoningTokens = reasoningTokens,
            CacheReadTokens = run.CacheReadTokens,
            CacheWriteTokens = run.CacheWriteTokens,
            Model = run.ModelName,
            BillingMode = ResolveBillingMode(run.PhaseBreakdown?.Values),
        };
    }

    /// <summary>
    /// Creates a <see cref="RunUsage"/> from a <see cref="PhaseUsage"/>.
    /// Billing mode is kept when it is <see cref="AgentBillingModes.Api"/> or
    /// <see cref="AgentBillingModes.Subscription"/>; unknown/null values are dropped.
    /// </summary>
    public static RunUsage From(PhaseUsage phase)
    {
        ArgumentNullException.ThrowIfNull(phase);

        var billingMode = phase.BillingMode == AgentBillingModes.Api || phase.BillingMode == AgentBillingModes.Subscription
            ? phase.BillingMode
            : null;

        return new RunUsage
        {
            Cost = phase.Cost,
            Tokens = phase.Tokens,
            InputTokens = phase.InputTokens,
            OutputTokens = phase.OutputTokens,
            ReasoningTokens = phase.ReasoningTokens,
            CacheReadTokens = phase.CacheReadTokens,
            CacheWriteTokens = phase.CacheWriteTokens,
            Model = phase.Model,
            BillingMode = billingMode,
        };
    }

    private static string? ResolveBillingMode(IEnumerable<PhaseUsage>? phases)
    {
        if (phases is null)
            return null;

        var knownPhases = phases
            .Where(p => p.BillingMode == AgentBillingModes.Api || p.BillingMode == AgentBillingModes.Subscription)
            .ToList();

        if (knownPhases.Count == 0)
            return null;

        return knownPhases.All(p => p.BillingMode == AgentBillingModes.Subscription)
            ? AgentBillingModes.Subscription
            : AgentBillingModes.Api;
    }
}
