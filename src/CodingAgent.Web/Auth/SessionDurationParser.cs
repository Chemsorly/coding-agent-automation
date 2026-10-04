using System.Globalization;

namespace CodingAgent.Web.Auth;

/// <summary>
/// Parses the session duration from the Helm values: <c>12h</c>, <c>90m</c>, or a .NET TimeSpan
/// string with colons (<c>1.00:00:00</c>). A bare number is rejected because
/// <see cref="TimeSpan.TryParse(string?, out TimeSpan)"/> would read it as days.
/// </summary>
public static class SessionDurationParser
{
    public static bool TryParse(string? value, out TimeSpan duration)
    {
        duration = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();
        var unit = text[^1];
        if (unit is 'h' or 'm')
        {
            if (!int.TryParse(text.AsSpan(0, text.Length - 1), NumberStyles.None, CultureInfo.InvariantCulture, out var amount))
                return false;
            duration = unit == 'h' ? TimeSpan.FromHours(amount) : TimeSpan.FromMinutes(amount);
            return duration > TimeSpan.Zero;
        }

        return text.Contains(':')
            && TimeSpan.TryParse(text, CultureInfo.InvariantCulture, out duration)
            && duration > TimeSpan.Zero;
    }
}
