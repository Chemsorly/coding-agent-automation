using System.Text.Json;
using System.Text.Json.Nodes;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Reads the triage agent's result file leniently (decisions.md: agent-produced JSON is read leniently). Only
/// a missing or unknown verdict fails the parse: every other unknown value maps to a default and is reported
/// as a warning, so a long investigation is not lost over one odd field.
/// </summary>
public static class TriageResultParser
{
    /// <summary>The parsed result, or the reason it could not be parsed, and what was defaulted.</summary>
    public sealed record ParseOutcome(TriageResult? Result, string? Error, IReadOnlyList<string> Warnings);

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    };

    public static ParseOutcome Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new ParseOutcome(null, "The result file is empty.", []);

        JsonObject root;
        try
        {
            root = JsonNode.Parse(StripCodeFence(json), NodeOptions, DocumentOptions) as JsonObject
                ?? throw new JsonException("The result is not a JSON object.");
        }
        catch (JsonException ex)
        {
            return new ParseOutcome(null, $"The result file is not valid JSON: {ex.Message}", []);
        }

        var warnings = new List<string>();

        var verdictText = Text(root, "verdict");
        var verdict = ParseVerdict(verdictText);
        if (verdict is null)
            return new ParseOutcome(null, $"The result has no known verdict (got '{verdictText ?? "nothing"}').", warnings);

        var result = new TriageResult
        {
            Verdict = verdict.Value,
            Confidence = ParseEnum<TriageConfidence>(Text(root, "confidence"), "confidence", null, warnings),
            Summary = Text(root, "summary") ?? "",
            Impact = Text(root, "impact"),
            Reproduced = Bool(root, "reproduced"),
            Reproduction = Text(root, "reproduction"),
            DuplicateOf = Text(root, "duplicateOf"),
            CausalChain = Objects(root, "causalChain")
                .Select(o => Text(o, "text") is { } text
                    ? new TriageChainStep
                    {
                        Text = text,
                        Kind = ParseEnum(Text(o, "kind"), "causal chain kind", TriageChainKind.Step, warnings),
                        EvidenceIds = Strings(o, "evidenceIds"),
                    }
                    : null)
                .OfType<TriageChainStep>().ToList(),
            Hypotheses = Objects(root, "hypotheses")
                .Select((o, i) => Text(o, "text") is { } text
                    ? new TriageHypothesis
                    {
                        Id = Text(o, "id") ?? $"H{i + 1}",
                        Text = text,
                        State = ParseEnum(Text(o, "state"), "hypothesis state", TriageHypothesisState.Open, warnings),
                    }
                    : null)
                .OfType<TriageHypothesis>().ToList(),
            Evidence = Objects(root, "evidence")
                .Select((o, i) => Text(o, "claim") is { } claim
                    ? new TriageEvidence
                    {
                        Id = Text(o, "id") ?? $"E{i + 1}",
                        Claim = claim,
                        Source = Text(o, "source") ?? "unknown",
                        Query = Text(o, "query"),
                        Link = Text(o, "link"),
                    }
                    : null)
                .OfType<TriageEvidence>().ToList(),
            Investigated = Objects(root, "investigated")
                .Select(o => Text(o, "check") is { } check
                    ? new TriageCheck
                    {
                        Check = check,
                        Where = Text(o, "where") ?? "unknown",
                        For = Text(o, "for"),
                        Result = Text(o, "result") ?? "",
                        EvidenceIds = Strings(o, "evidenceIds"),
                    }
                    : null)
                .OfType<TriageCheck>().ToList(),
            NotChecked = Objects(root, "notChecked")
                .Select(o => Text(o, "what") is { } what ? new TriageGap { What = what, Why = Text(o, "why") ?? "" } : null)
                .OfType<TriageGap>().ToList(),
            Questions = Objects(root, "questions")
                .Select(o => Text(o, "question") is { } q ? new TriageQuestion { Question = q, Why = Text(o, "why") } : null)
                .OfType<TriageQuestion>().ToList(),
            Drafts = Objects(root, "drafts")
                .Select((o, i) => new TriageDraft
                {
                    Id = Text(o, "id") ?? $"d{i + 1}",
                    Kind = ParseEnum(Text(o, "kind"), "draft kind", TriageDraftKind.RootFix, warnings),
                    TargetRepository = Text(o, "targetRepository") ?? "",
                    Title = Text(o, "title") ?? "",
                    Body = Text(o, "body") ?? "",
                    Size = Text(o, "size"),
                })
                .ToList(),
        };

        return new ParseOutcome(result, null, warnings);
    }

    /// <summary>Maps the agent's verdict, accepting spacing, case and dash variants.</summary>
    internal static TriageVerdict? ParseVerdict(string? value) => Normalize(value) switch
    {
        "cause_found" or "causefound" or "found" or "root_cause_found" => TriageVerdict.CauseFound,
        "inconclusive" or "unknown" or "unclear" => TriageVerdict.Inconclusive,
        "not_a_bug" or "notabug" or "not_bug" or "works_as_designed" => TriageVerdict.NotABug,
        "duplicate" or "dup" => TriageVerdict.Duplicate,
        _ => null
    };

    private static T? ParseEnum<T>(string? value, string what, T? fallback, List<string> warnings) where T : struct, Enum
    {
        if (value is null)
            return fallback;

        var normalized = Normalize(value);
        foreach (var candidate in Enum.GetValues<T>())
        {
            if (Normalize(JsonNamingPolicy.SnakeCaseLower.ConvertName(candidate.ToString())) == normalized
                || Normalize(candidate.ToString()) == normalized.Replace("_", "", StringComparison.Ordinal))
                return candidate;
        }

        warnings.Add($"Unknown {what} '{value}'; using {(fallback?.ToString() ?? "none")}.");
        return fallback;
    }

    private static T ParseEnum<T>(string? value, string what, T fallback, List<string> warnings) where T : struct, Enum =>
        ParseEnum<T>(value, what, (T?)fallback, warnings) ?? fallback;

    private static string Normalize(string? value) =>
        (value ?? "").Trim().ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

    private static string? Text(JsonObject o, string name)
    {
        if (!o.TryGetPropertyValue(name, out var node) || node is null)
            return null;
        var text = node is JsonValue value && value.TryGetValue<string>(out var s) ? s : node.ToJsonString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static bool Bool(JsonObject o, string name)
    {
        if (!o.TryGetPropertyValue(name, out var node) || node is not JsonValue value)
            return false;
        if (value.TryGetValue<bool>(out var b))
            return b;
        return value.TryGetValue<string>(out var s) && Normalize(s) is "true" or "yes";
    }

    private static IEnumerable<JsonObject> Objects(JsonObject o, string name) =>
        o.TryGetPropertyValue(name, out var node) && node is JsonArray array
            ? array.OfType<JsonObject>()
            : [];

    private static IReadOnlyList<string> Strings(JsonObject o, string name)
    {
        if (!o.TryGetPropertyValue(name, out var node) || node is null)
            return [];
        if (node is JsonArray array)
            return array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : null)
                .Where(s => !string.IsNullOrEmpty(s)).Select(s => s!).ToList();
        return Text(o, name) is { } single ? [single] : [];
    }

    /// <summary>Agents sometimes wrap the file's JSON in a Markdown code fence.</summary>
    private static string StripCodeFence(string json)
    {
        var trimmed = json.Trim();
        if (!trimmed.StartsWith("```", StringComparison.Ordinal))
            return trimmed;
        var firstNewline = trimmed.IndexOf('\n');
        var lastFence = trimmed.LastIndexOf("```", StringComparison.Ordinal);
        return firstNewline > 0 && lastFence > firstNewline
            ? trimmed[(firstNewline + 1)..lastFence]
            : trimmed;
    }
}
