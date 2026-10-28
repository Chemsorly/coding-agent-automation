using System.Text.Json;

namespace CodingAgent.Pipeline.Brain;

/// <summary>A single line entry in a brain review summary.</summary>
public sealed record BrainReviewSummaryLine(
    string Scope,
    string File,
    string Text,
    string? Reason);

/// <summary>A "check" item from the brain review summary.</summary>
public sealed record BrainReviewSummaryCheck(
    string Cause,
    IReadOnlyList<string> Repositories,
    string SuggestedHome);

/// <summary>A flaky test identified in the brain review summary.</summary>
public sealed record BrainReviewSummaryFlakyTest(
    string Name,
    IReadOnlyList<string> Repositories,
    int Count);

/// <summary>The parsed brain review summary (from .agent/brain-review-summary.json).</summary>
public sealed record BrainReviewSummary(
    IReadOnlyList<BrainReviewSummaryLine> Promoted,
    IReadOnlyList<BrainReviewSummaryLine> Appended,
    IReadOnlyList<BrainReviewSummaryLine> Retired,
    IReadOnlyList<BrainReviewSummaryLine> MissedAgain,
    IReadOnlyList<BrainReviewSummaryCheck> Checks,
    IReadOnlyList<BrainReviewSummaryFlakyTest> FlakyTests,
    int InfrastructureFailures,
    int CiStatusErrors,
    int Timeouts);

/// <summary>Result of parsing a brain review summary JSON.</summary>
public sealed record BrainReviewSummaryParseResult(BrainReviewSummary? Summary, string? Error);

/// <summary>
/// Parses and converts the brain review summary JSON (spec 054, req 7.6, task 2B.4).
/// Pure — no I/O.
/// </summary>
public static partial class BrainReviewSummaryParser
{
    private static readonly JsonSerializerOptions _options = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true
    };

    public static BrainReviewSummaryParseResult Parse(string? json)
    {
        if (json is null)
            return new BrainReviewSummaryParseResult(null, "summary_missing");

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new BrainReviewSummaryParseResult(null, "summary_invalid");

            var summary = new BrainReviewSummary(
                ParseLines(root, "promoted"),
                ParseLines(root, "appended"),
                ParseLines(root, "retired"),
                ParseLines(root, "missedAgain"),
                ParseChecks(root),
                ParseFlakyTests(root),
                GetInt(root, "infrastructureFailures"),
                GetInt(root, "ciStatusErrors"),
                GetInt(root, "timeouts"));

            return new BrainReviewSummaryParseResult(summary, null);
        }
        catch (JsonException)
        {
            return new BrainReviewSummaryParseResult(null, "summary_invalid");
        }
        catch (Exception)
        {
            return new BrainReviewSummaryParseResult(null, "summary_invalid");
        }
    }

    private static IReadOnlyList<BrainReviewSummaryLine> ParseLines(JsonElement root, string key)
    {
        if (!root.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<BrainReviewSummaryLine>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            result.Add(new BrainReviewSummaryLine(
                GetString(item, "scope"),
                GetString(item, "file"),
                GetString(item, "text"),
                GetStringOrNull(item, "reason")));
        }
        return result;
    }

    private static IReadOnlyList<BrainReviewSummaryCheck> ParseChecks(JsonElement root)
    {
        if (!root.TryGetProperty("checks", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<BrainReviewSummaryCheck>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            result.Add(new BrainReviewSummaryCheck(
                GetString(item, "cause"),
                GetStringList(item, "repositories"),
                GetString(item, "suggestedHome")));
        }
        return result;
    }

    private static IReadOnlyList<BrainReviewSummaryFlakyTest> ParseFlakyTests(JsonElement root)
    {
        if (!root.TryGetProperty("flakyTests", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<BrainReviewSummaryFlakyTest>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            result.Add(new BrainReviewSummaryFlakyTest(
                GetString(item, "name"),
                GetStringList(item, "repositories"),
                GetInt(item, "count")));
        }
        return result;
    }

    private static string GetString(JsonElement el, string key)
    {
        if (el.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString() ?? "";
        return "";
    }

    private static string? GetStringOrNull(JsonElement el, string key)
    {
        if (el.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
            return prop.GetString();
        return null;
    }

    private static int GetInt(JsonElement el, string key)
    {
        if (el.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.Number)
            return prop.TryGetInt32(out var v) ? v : 0;
        return 0;
    }

    private static IReadOnlyList<string> GetStringList(JsonElement el, string key)
    {
        if (!el.TryGetProperty(key, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return [];
        var result = new List<string>();
        foreach (var item in arr.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String)
                result.Add(item.GetString() ?? "");
        return result;
    }
}

// Extension methods added for Task 3.5 (spec 054)
public static partial class BrainReviewSummaryParser
{
    /// <summary>
    /// Converts a parsed summary into a <see cref="BrainConsolidationOutcome"/>.
    /// </summary>
    public static CodingAgent.Pipeline.Models.BrainConsolidationOutcome ToOutcome(BrainReviewSummary s)
    {
        var lines = new List<CodingAgent.Pipeline.Models.BrainReviewLine>();
        foreach (var l in s.Promoted)
            lines.Add(new CodingAgent.Pipeline.Models.BrainReviewLine { Action = "promoted", Scope = l.Scope, File = l.File, Text = l.Text });
        foreach (var l in s.Appended)
            lines.Add(new CodingAgent.Pipeline.Models.BrainReviewLine { Action = "appended", Scope = l.Scope, File = l.File, Text = l.Text });
        foreach (var l in s.Retired)
            lines.Add(new CodingAgent.Pipeline.Models.BrainReviewLine { Action = "retired", Scope = l.Scope, File = l.File, Text = l.Text });
        foreach (var l in s.MissedAgain)
            lines.Add(new CodingAgent.Pipeline.Models.BrainReviewLine { Action = "missed_again", Scope = l.Scope, File = l.File, Text = l.Text });

        return new CodingAgent.Pipeline.Models.BrainConsolidationOutcome
        {
            Promoted = s.Promoted.Count,
            Appended = s.Appended.Count,
            Retired = s.Retired.Count,
            MissedAgain = s.MissedAgain.Count,
            Lines = lines,
            Checks = s.Checks.Select(c => new CodingAgent.Pipeline.Models.BrainReviewCheck
            {
                Cause = c.Cause,
                Repositories = c.Repositories,
                SuggestedHome = c.SuggestedHome
            }).ToList(),
            FlakyTests = s.FlakyTests.Select(f => new CodingAgent.Pipeline.Models.BrainFlakyTest
            {
                Name = f.Name,
                Repositories = f.Repositories,
                Count = f.Count
            }).ToList(),
            InfrastructureFailures = s.InfrastructureFailures,
            CiStatusErrors = s.CiStatusErrors,
            Timeouts = s.Timeouts
        };
    }

    /// <summary>
    /// Caps <see cref="BrainConsolidationOutcome"/> so its JSON serialization is ≤ MaxReviewSummaryBytes.
    /// </summary>
    public static CodingAgent.Pipeline.Models.BrainConsolidationOutcome Cap(
        CodingAgent.Pipeline.Models.BrainConsolidationOutcome o)
    {
        var lines = o.Lines.ToList();
        while (lines.Count > 0)
        {
            var candidate = o with { Lines = lines, LinesTruncated = lines.Count < o.Lines.Count };
            var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(candidate);
            if (json.Length <= BrainLayout.MaxReviewSummaryBytes)
                return candidate;
            lines.RemoveAt(lines.Count - 1);
        }
        return o with { Lines = [], LinesTruncated = o.Lines.Count > 0 };
    }
}
