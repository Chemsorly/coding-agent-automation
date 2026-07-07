using System.Globalization;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Formats cost and token values for human-readable display.
/// </summary>
public static class CostFormatter
{
    /// <summary>Formats a cost value as "$0.03" or returns "—" if null/zero.</summary>
    public static string FormatCost(decimal? cost)
    {
        if (cost is null or <= 0m) return "—";
        return FormattableString.Invariant($"${cost.Value:F2}");
    }

    /// <summary>Formats a token count as "12.4K" or "1.2M", or "—" if zero.</summary>
    public static string FormatTokens(long tokens)
    {
        if (tokens <= 0) return "—";
        if (tokens >= 1_000_000) return FormattableString.Invariant($"{tokens / 1_000_000.0:F1}M");
        if (tokens >= 1_000) return FormattableString.Invariant($"{tokens / 1_000.0:F1}K");
        return tokens.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns a compact badge string: cost if available, otherwise tokens, otherwise "—".
    /// </summary>
    public static string FormatBadge(long totalTokens, decimal? totalCost)
    {
        if (totalCost is not null and > 0m) return FormatCost(totalCost);
        if (totalTokens > 0) return $"{FormatTokens(totalTokens)} tok";
        return "—";
    }
}
