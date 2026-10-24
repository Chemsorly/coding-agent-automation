using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Web.Components.Shared;

/// <summary>Formats a <see cref="RunUsage"/> for display: headline text, tooltip, and breakdown line.</summary>
public static class UsageText
{
    /// <summary>
    /// One-line summary of the usage.
    /// Returns <c>"—"</c> when usage is null or empty;
    /// <c>"≈ $x.xx"</c> for subscription-billed cost;
    /// <c>"$x.xx"</c> for API-billed cost;
    /// <c>"N tokens"</c> when only token counts are available.
    /// </summary>
    public static string Headline(RunUsage? usage)
    {
        if (usage is null || (usage.Cost is null or <= 0m && usage.Tokens <= 0))
            return "—";

        if (usage.Cost > 0m && usage.BillingMode == AgentBillingModes.Subscription)
            return "≈ " + CostFormatter.FormatCost(usage.Cost);

        if (usage.Cost > 0m)
            return CostFormatter.FormatCost(usage.Cost);

        return CostFormatter.FormatTokens(usage.Tokens) + " tokens";
    }

    /// <summary>
    /// Multi-line native <c>title</c> tooltip (lines separated with <c>\n</c>).
    /// Returns empty string when <see cref="Headline"/> returns <c>"—"</c>.
    /// Line 1: cost (or "No cost reported") + optional billing mode + optional model.
    /// Line 2 (when Tokens &gt; 0): new tokens with optional per-bucket split in parentheses.
    /// Line 3 (when CacheReadTokens &gt; 0): cache read.
    /// Line 4 (when CacheWriteTokens &gt; 0): cache write.
    /// </summary>
    public static string Tooltip(RunUsage? usage)
    {
        if (Headline(usage) == "—")
            return "";

        var lines = new List<string>();

        // Line 1: cost or "No cost reported", plus billing mode and model.
        var firstParts = new List<string>();
        if (usage!.Cost > 0m)
            firstParts.Add(Headline(usage));
        else
            firstParts.Add("No cost reported");

        if (usage.BillingMode == AgentBillingModes.Api)
            firstParts.Add("API billing");
        else if (usage.BillingMode == AgentBillingModes.Subscription)
            firstParts.Add("subscription (API-price estimate, not billed)");

        if (!string.IsNullOrEmpty(usage.Model))
            firstParts.Add(usage.Model);

        lines.Add(string.Join(" · ", firstParts));

        // Line 2: new tokens, only when > 0.
        if (usage.Tokens > 0)
        {
            var tokenLine = "New tokens " + CostFormatter.FormatTokens(usage.Tokens);
            if (usage.InputTokens + usage.OutputTokens + usage.ReasoningTokens > 0)
            {
                tokenLine += $" (input {CostFormatter.FormatTokens(usage.InputTokens)}" +
                             $" · output {CostFormatter.FormatTokens(usage.OutputTokens)}" +
                             $" · thinking {CostFormatter.FormatTokens(usage.ReasoningTokens)})";
            }
            lines.Add(tokenLine);
        }

        // Line 3: cache read, only when > 0.
        if (usage.CacheReadTokens > 0)
            lines.Add("Cache read " + CostFormatter.FormatTokens(usage.CacheReadTokens));

        // Line 4: cache write, only when > 0.
        if (usage.CacheWriteTokens > 0)
            lines.Add("Cache write " + CostFormatter.FormatTokens(usage.CacheWriteTokens));

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Lines 2–4 of the tooltip joined with <c>" · "</c>; empty string when there are none.
    /// Suitable for an always-visible breakdown line below the headline cost figure.
    /// </summary>
    public static string BreakdownLine(RunUsage? usage)
    {
        if (usage is null)
            return "";

        var parts = new List<string>();

        if (usage.Tokens > 0)
        {
            var tokenLine = "New tokens " + CostFormatter.FormatTokens(usage.Tokens);
            if (usage.InputTokens + usage.OutputTokens + usage.ReasoningTokens > 0)
            {
                tokenLine += $" (input {CostFormatter.FormatTokens(usage.InputTokens)}" +
                             $" · output {CostFormatter.FormatTokens(usage.OutputTokens)}" +
                             $" · thinking {CostFormatter.FormatTokens(usage.ReasoningTokens)})";
            }
            parts.Add(tokenLine);
        }

        if (usage.CacheReadTokens > 0)
            parts.Add("Cache read " + CostFormatter.FormatTokens(usage.CacheReadTokens));

        if (usage.CacheWriteTokens > 0)
            parts.Add("Cache write " + CostFormatter.FormatTokens(usage.CacheWriteTokens));

        return string.Join(" · ", parts);
    }
}
