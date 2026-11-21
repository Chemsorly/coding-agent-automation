using System.Globalization;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// The usage threshold of the Claude subscription: how readings update the stored state, when a reading pauses the
/// credential, and what a manual resume lets through. Pure.
/// </summary>
public static class CredentialUsageRules
{
    /// <summary>
    /// <paramref name="state"/> with the readings of <paramref name="observations"/> that carry a usage, for the windows
    /// with a threshold (<see cref="CredentialUsageWindows.IsTracked"/>). Readings arrive late and out of order (several
    /// agents, calls that report when they end), so per window a reading of an earlier window is ignored and, within the
    /// same window, the higher usage is kept: usage only grows until the window resets. Resume overrides whose window
    /// has reset are dropped.
    /// </summary>
    public static CredentialUsageState Record(
        CredentialUsageState state, IEnumerable<AgentRateLimitObservation> observations, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(observations);

        var windows = new Dictionary<string, CredentialUsageReading>(state.Windows, StringComparer.Ordinal);
        foreach (var o in observations)
        {
            if (o.Utilization is not { } utilization || !CredentialUsageWindows.IsTracked(o.Window))
                continue;

            var incoming = new CredentialUsageReading(utilization, o.ResetsAt, o.Status, now);
            if (!windows.TryGetValue(o.Window, out var stored) || Replaces(incoming, stored))
                windows[o.Window] = incoming;
        }

        var resumed = state.ResumedThrough
            .Where(r => r.Value > now)
            .ToDictionary(r => r.Key, r => r.Value, StringComparer.Ordinal);
        return new CredentialUsageState(windows, resumed);
    }

    /// <summary>
    /// The window whose last reading reached its threshold and has not reset yet, or null. A reading without a reset
    /// time pauses nothing (the pause could not end by itself), nor does a window a manual resume let through. Of
    /// several, the one that resets last wins, so the pause covers all of them.
    /// </summary>
    public static CredentialUsageBreach? Breach(
        CredentialUsageState state, CredentialUsageThresholds thresholds, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(thresholds);

        return Reached(state, thresholds, now)
            .Where(b => !(state.ResumedThrough.TryGetValue(b.Window, out var through) && b.ResetsAt <= through))
            .MaxBy(b => b.ResetsAt);
    }

    /// <summary>
    /// <paramref name="state"/> after an operator resumed the credential: every window at or over its threshold is let
    /// through until it resets. Windows under their threshold keep it, so a later climb within the same window still
    /// pauses.
    /// </summary>
    public static CredentialUsageState Resume(
        CredentialUsageState state, CredentialUsageThresholds thresholds, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(thresholds);

        var resumed = new Dictionary<string, DateTimeOffset>(state.ResumedThrough, StringComparer.Ordinal);
        foreach (var breach in Reached(state, thresholds, now))
            resumed[breach.Window] = breach.ResetsAt;
        return state with { ResumedThrough = resumed };
    }

    /// <summary>
    /// True when <paramref name="incoming"/> is newer than <paramref name="stored"/>: a later window, or the same window
    /// at the same or a higher usage. A reading without a reset time only replaces one without a reset time either.
    /// </summary>
    private static bool Replaces(CredentialUsageReading incoming, CredentialUsageReading stored) =>
        (incoming.ResetsAt, stored.ResetsAt) switch
        {
            (null, not null) => false,
            (not null, null) => true,
            ({ } i, { } s) when i != s => i > s,
            _ => incoming.Utilization >= stored.Utilization,
        };

    /// <summary>Every window whose last reading is at or over its threshold and resets after <paramref name="now"/>.</summary>
    private static IEnumerable<CredentialUsageBreach> Reached(
        CredentialUsageState state, CredentialUsageThresholds thresholds, DateTimeOffset now)
    {
        foreach (var (window, reading) in state.Windows)
        {
            var threshold = thresholds.PercentOf(window);
            // Readings come with two decimals; the tolerance keeps 0.75 at 75 % despite binary rounding.
            if (threshold > 0 && reading.ResetsAt > now && reading.Utilization * 100 >= threshold - 1e-6)
                yield return new CredentialUsageBreach(window, reading, threshold);
        }
    }

    /// <summary><paramref name="utilization"/> as a whole percent, e.g. 0.755 → "76".</summary>
    public static string Percent(double utilization) =>
        Math.Round(utilization * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture);
}
