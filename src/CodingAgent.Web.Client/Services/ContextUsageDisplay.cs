using System.Globalization;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Web.Services;

/// <summary>Text for how full the model's context got per phase (<see cref="PhaseUsage.PeakContextTokens"/>).</summary>
public static class ContextUsageDisplay
{
    /// <summary>Whether any phase reports its peak context.</summary>
    public static bool AnyReported(IReadOnlyDictionary<string, PhaseUsage>? phases) =>
        phases?.Values.Any(p => p.PeakContextTokens is > 0) == true;

    /// <summary>"142.0K / 200.0K (71%)", "142.0K" without a known window, "—" without a peak.</summary>
    public static string PhaseText(PhaseUsage phase)
    {
        ArgumentNullException.ThrowIfNull(phase);
        if (phase.PeakContextTokens is not { } peak || peak <= 0)
            return "—";
        if (phase.ContextWindowTokens is not { } window || window <= 0)
            return CostFormatter.FormatTokens(peak);
        var share = string.Create(CultureInfo.InvariantCulture, $"{(double)peak / window * 100:F0}%");
        return $"{CostFormatter.FormatTokens(peak)} / {CostFormatter.FormatTokens(window)} ({share})";
    }

    /// <summary>
    /// The phase whose context came closest to its window; among phases without a known window, the largest peak.
    /// A phase with a window wins over one without, since only it says how close the run came to the limit.
    /// Null when no phase reports a peak.
    /// </summary>
    public static (string Phase, PhaseUsage Usage)? Fullest(IReadOnlyDictionary<string, PhaseUsage>? phases)
    {
        if (!AnyReported(phases)) return null;
        var best = phases!
            .Where(p => p.Value.PeakContextTokens is > 0)
            .OrderByDescending(p => p.Value.ContextWindowTokens is > 0)
            .ThenByDescending(p => p.Value.ContextWindowTokens is long w and > 0
                ? (double)p.Value.PeakContextTokens!.Value / w
                : p.Value.PeakContextTokens!.Value)
            .First();
        return (best.Key, best.Value);
    }
}
