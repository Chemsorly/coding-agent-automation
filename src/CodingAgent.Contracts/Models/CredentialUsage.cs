using CodingAgent.Pipeline.Services;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// The usage thresholds of the Claude subscription (<see cref="AgentCredentials.ClaudeSubscription"/>): when a reading of
/// a window reaches its threshold, the credential is paused until that window resets. Runs already going finish; only
/// new work waits. 0 switches a window's threshold off. Stored as one JSON entry (<see cref="StorageKey"/>) in the
/// <c>KeyValueStore</c> table; set in Settings → Agents → Credentials.
/// </summary>
public sealed record CredentialUsageThresholds
{
    /// <summary>The <c>KeyValueStore</c> key of the stored thresholds.</summary>
    public const string StorageKey = "agent-credential-usage-thresholds";

    /// <summary>Highest accepted threshold in percent.</summary>
    public const int MaxPercent = 100;

    /// <summary>
    /// What a deployment has before the thresholds are first saved: 75 % of the 5-hour window leaves room for the runs
    /// still going and for using the subscription by hand; the 7-day window pauses for days, so it waits longer.
    /// </summary>
    public static readonly CredentialUsageThresholds Default = new();

    /// <summary>Percent of the 5-hour window at which new work stops; 0 = off.</summary>
    public int FiveHourPercent { get; init; } = 75;

    /// <summary>Percent of the 7-day window at which new work stops; 0 = off.</summary>
    public int SevenDayPercent { get; init; } = 90;

    /// <summary>
    /// The threshold of <paramref name="window"/> in percent; 0 for a window without one. A per-model weekly window
    /// (<c>seven_day_opus</c>) uses the 7-day threshold.
    /// </summary>
    public int PercentOf(string window)
    {
        if (window == CredentialUsageWindows.FiveHour)
            return FiveHourPercent;
        return CredentialUsageWindows.IsWeekly(window) ? SevenDayPercent : 0;
    }

    /// <summary>Every problem with these thresholds; empty when valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (FiveHourPercent is < 0 or > MaxPercent)
            errors.Add($"The 5-hour threshold must be between 0 and {MaxPercent} %.");
        if (SevenDayPercent is < 0 or > MaxPercent)
            errors.Add($"The 7-day threshold must be between 0 and {MaxPercent} %.");
        return errors;
    }
}

/// <summary>The subscription windows a threshold applies to (Claude Code's <c>rateLimitType</c> values).</summary>
public static class CredentialUsageWindows
{
    /// <summary>The 5-hour session window.</summary>
    public const string FiveHour = "five_hour";

    /// <summary>The weekly window.</summary>
    public const string SevenDay = "seven_day";

    /// <summary>The thresholds, in the order the UI lists them.</summary>
    public static IReadOnlyList<string> All { get; } = [FiveHour, SevenDay];

    /// <summary>True for the weekly window and its per-model variants (<c>seven_day_opus</c>, <c>seven_day_sonnet</c>).</summary>
    public static bool IsWeekly(string window) =>
        window == SevenDay || window.StartsWith(SevenDay + "_", StringComparison.Ordinal);

    /// <summary>True for a window a threshold applies to; the overage window and unknown ones are not.</summary>
    public static bool IsTracked(string window) => window == FiveHour || IsWeekly(window);

    /// <summary>The order the UI lists <paramref name="window"/> in: 5-hour, 7-day, then the per-model weekly ones.</summary>
    public static int Order(string window) => window switch
    {
        FiveHour => 0,
        SevenDay => 1,
        _ => 2,
    };

    /// <summary>How <paramref name="window"/> reads in texts, e.g. "7-day window (opus)".</summary>
    public static string Text(string window) => window switch
    {
        FiveHour => "5-hour window",
        SevenDay => "7-day window",
        _ when IsWeekly(window) => $"7-day window ({window[(SevenDay.Length + 1)..]})",
        _ => window,
    };
}

/// <summary>The last reading of one window.</summary>
/// <param name="Utilization">Fraction of the window used (0.0–1.0).</param>
/// <param name="ResetsAt">When the window resets; null when not reported.</param>
/// <param name="Status">The CLI's status: <c>allowed</c>, <c>allowed_warning</c> or <c>rejected</c>.</param>
/// <param name="SeenAt">When the API received it.</param>
public sealed record CredentialUsageReading(double Utilization, DateTimeOffset? ResetsAt, string Status, DateTimeOffset SeenAt);

/// <summary>
/// The last reading per window of one credential, and the windows a manual resume let through. Stored as one JSON entry
/// per credential (<c>agent-credential-usage:&lt;credential&gt;</c>).
/// </summary>
/// <param name="Windows">The last reading per window (<see cref="CredentialUsageWindows"/>).</param>
/// <param name="ResumedThrough">
/// Per window: the reset time up to which an operator's resume overrides its threshold. A reading whose window resets
/// at or before it pauses nothing, so the threshold applies again from the window's next reset.
/// </param>
public sealed record CredentialUsageState(
    IReadOnlyDictionary<string, CredentialUsageReading> Windows,
    IReadOnlyDictionary<string, DateTimeOffset> ResumedThrough)
{
    /// <summary>No readings yet.</summary>
    public static readonly CredentialUsageState Empty =
        new(new Dictionary<string, CredentialUsageReading>(), new Dictionary<string, DateTimeOffset>());
}

/// <summary>A window whose reading reached its threshold.</summary>
/// <param name="Window">One of <see cref="CredentialUsageWindows"/>.</param>
/// <param name="Reading">The reading that reached it.</param>
/// <param name="ThresholdPercent">The threshold it reached.</param>
public sealed record CredentialUsageBreach(string Window, CredentialUsageReading Reading, int ThresholdPercent)
{
    /// <summary>The window resets then, which ends the pause.</summary>
    public DateTimeOffset ResetsAt => Reading.ResetsAt!.Value;

    /// <summary>The pause detail, e.g. "Claude 5-hour window at 76 % (threshold 75 %)".</summary>
    public string Detail =>
        $"Claude {CredentialUsageWindows.Text(Window)} at {CredentialUsageRules.Percent(Reading.Utilization)} % (threshold {ThresholdPercent} %)";
}
