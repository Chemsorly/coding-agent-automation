using System.Globalization;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Components.Pages;

/// <summary>The unit a settings field shows a value in.</summary>
public enum SettingUnit
{
    /// <summary>The setting's own number.</summary>
    None,
    Seconds,
    Minutes,
    Days,
    /// <summary>A byte count shown in MB.</summary>
    Megabytes,
}

/// <summary>
/// Input limits for a settings field, taken from the setting's [Range] (see <see cref="PipelineSettingsValidator.RangeOf"/>)
/// and converted to the unit the field shows. The API checks the same range when settings are saved.
/// </summary>
public static class SettingLimits
{
    private const double BytesPerMegabyte = 1_048_576d;

    /// <summary>The lowest value the field accepts, for a setting path such as <c>"MaxRetries"</c>.</summary>
    public static long Min(string setting, SettingUnit unit = SettingUnit.None) =>
        (long)Math.Ceiling(Convert(PipelineSettingsValidator.RangeOf(setting)?.Minimum, unit) ?? 0);

    /// <summary>The highest value the field accepts; <see cref="int.MaxValue"/> when the setting has no range.</summary>
    public static long Max(string setting, SettingUnit unit = SettingUnit.None) =>
        (long)Math.Floor(Convert(PipelineSettingsValidator.RangeOf(setting)?.Maximum, unit) ?? int.MaxValue);

    /// <summary>Converts a stored byte count to whole MB for display.</summary>
    public static int ToMegabytes(long bytes) => (int)Math.Round(bytes / BytesPerMegabyte);

    /// <summary>Converts MB entered in a field to the stored byte count.</summary>
    public static long FromMegabytes(int megabytes) => (long)(megabytes * BytesPerMegabyte);

    private static double? Convert(object? limit, SettingUnit unit) => limit switch
    {
        null => null,
        string text => ConvertTimeSpan(TimeSpan.Parse(text, CultureInfo.InvariantCulture), unit),
        _ => unit == SettingUnit.Megabytes
            ? System.Convert.ToDouble(limit, CultureInfo.InvariantCulture) / BytesPerMegabyte
            : System.Convert.ToDouble(limit, CultureInfo.InvariantCulture),
    };

    private static double ConvertTimeSpan(TimeSpan value, SettingUnit unit) => unit switch
    {
        SettingUnit.Seconds => value.TotalSeconds,
        SettingUnit.Minutes => value.TotalMinutes,
        SettingUnit.Days => value.TotalDays,
        _ => throw new ArgumentException($"A time setting needs a time unit, not {unit}.", nameof(unit)),
    };
}
