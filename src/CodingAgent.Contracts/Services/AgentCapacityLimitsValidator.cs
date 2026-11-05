using System.Text.RegularExpressions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Spec 057 req 1.3, 1.4, 6.3, 8.3: checks <see cref="AgentCapacityLimits"/> before they are saved or imported. One
/// message per problem; an empty list means valid. Pure.
/// </summary>
public static partial class AgentCapacityLimitsValidator
{
    /// <summary>Longest template key: a Kubernetes label value holds at most 63 characters.</summary>
    public const int MaxKeyLength = 63;

    /// <summary>
    /// Normalizes a job template key the way <c>JobTemplateStore.NormalizeLabels</c> does: split on commas, trim, drop
    /// empty entries, sort ordinally, join with commas. Null or whitespace gives <c>""</c>.
    /// </summary>
    public static string NormalizeKey(string? key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return "";

        var parts = key.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Array.Sort(parts, StringComparer.Ordinal);
        return string.Join(",", parts);
    }

    /// <summary>Every problem with <paramref name="limits"/>, in a fixed order; empty when valid.</summary>
    public static IReadOnlyList<string> Validate(AgentCapacityLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        var errors = new List<string>();
        var entries = limits.MaxConcurrentByJobTemplate ?? new Dictionary<string, int>();

        if (entries.Count > AgentCapacityLimits.MaxEntries)
            errors.Add($"At most {AgentCapacityLimits.MaxEntries} job template limits can be set (was {entries.Count}).");

        if (!InRange(limits.MaxConcurrentAgents))
        {
            errors.Add($"The limit for all agents must be between {AgentCapacityLimits.MinLimit} and " +
                $"{AgentCapacityLimits.MaxLimit} (was {limits.MaxConcurrentAgents}).");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var reportedTwice = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (key, value) in entries)
        {
            var normalized = NormalizeKey(key);
            if (normalized.Length > MaxKeyLength || !KeyPattern().IsMatch(normalized))
            {
                var shown = key.Length > MaxKeyLength ? key[..MaxKeyLength] : key;
                errors.Add($"{shown} is not a valid job template key.");
                continue;
            }

            if (!InRange(value))
            {
                errors.Add($"The limit for {normalized} must be between {AgentCapacityLimits.MinLimit} and " +
                    $"{AgentCapacityLimits.MaxLimit} (was {value}).");
            }

            if (!seen.Add(normalized) && reportedTwice.Add(normalized))
                errors.Add($"{normalized} is set twice.");
        }

        return errors;
    }

    private static bool InRange(int value) => value is >= AgentCapacityLimits.MinLimit and <= AgentCapacityLimits.MaxLimit;

    // The empty key is valid: it is the job template whose labels are "".
    [GeneratedRegex("^[A-Za-z0-9._,-]*$")]
    private static partial Regex KeyPattern();
}
