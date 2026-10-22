using System.Text.Json;
using System.Text.Json.Nodes;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Infrastructure;

/// <summary>
/// An agent profile as stored before spec 053: match labels, an agent provider config and a priority.
/// Frozen for the 053 move; do not use elsewhere.
/// </summary>
internal sealed record LegacyProfile(
    string Id,
    string DisplayName,
    IReadOnlyList<string> MatchLabels,
    string AgentProviderConfigId,
    bool Enabled,
    int Priority,
    string? JobTemplate)
{
    /// <summary>Legacy data (spec 053 DD9 as amended by review-3 M2): a non-empty provider config Id or match labels.</summary>
    public bool IsLegacy => !string.IsNullOrWhiteSpace(AgentProviderConfigId) || MatchLabels.Count > 0;

    /// <summary>
    /// The pre-053 <c>AgentProfile.JobTemplateKey()</c>: the <see cref="JobTemplate"/> split, trimmed and sorted
    /// ordinally, else the sorted <see cref="MatchLabels"/>.
    /// </summary>
    public string JobTemplateKey()
    {
        if (!string.IsNullOrWhiteSpace(JobTemplate))
            return string.Join(",", LegacyRouting.SplitLabels(JobTemplate).OrderBy(l => l, StringComparer.Ordinal));
        return string.Join(",", MatchLabels.OrderBy(l => l, StringComparer.Ordinal));
    }
}

/// <summary>
/// Reads and rewrites agent profile JSON without the <see cref="AgentProfile"/> record, so the move keeps working
/// after the record loses its old members. Property names are matched case-insensitively, as the stores read them.
/// Frozen for the 053 move; do not use elsewhere.
/// </summary>
internal static class LegacyProfileJson
{
    private static readonly string[] LegacyKeys = ["agentProviderConfigId", "matchLabels", "priority"];

    /// <summary>The profile in <paramref name="json"/>, or null when it is not a JSON object with an Id.</summary>
    public static LegacyProfile? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || JsonNode.Parse(json) is not JsonObject node)
            return null;
        var id = Get(node, "id")?.GetValue<string>();
        if (string.IsNullOrEmpty(id))
            return null;

        var labels = Get(node, "matchLabels") is JsonArray array
            ? array.Select(l => l?.GetValue<string>()).OfType<string>().ToList()
            : [];
        return new LegacyProfile(
            id,
            Get(node, "displayName")?.GetValue<string>() ?? id,
            labels,
            Get(node, "agentProviderConfigId")?.GetValue<string>() ?? "",
            Get(node, "enabled")?.GetValue<bool>() ?? true,
            Get(node, "priority") is { } priority ? priority.GetValue<int>() : 0,
            Get(node, "jobTemplate")?.GetValue<string>());
    }

    /// <summary>
    /// <paramref name="json"/> in the spec 053 shape: the legacy keys removed, and <c>jobTemplate</c>,
    /// <c>settings</c> and <c>enabled</c> set. Every other property (Id, name, MCP servers) is kept as it is.
    /// </summary>
    public static string Convert(string json, string jobTemplateKey, IReadOnlyDictionary<string, string> settings, bool enabled)
    {
        var node = JsonNode.Parse(json)!.AsObject();
        foreach (var key in LegacyKeys.Concat(["jobTemplate", "settings", "enabled"]))
            Remove(node, key);

        node["jobTemplate"] = jobTemplateKey;
        var settingsNode = new JsonObject();
        foreach (var (key, value) in settings.OrderBy(s => s.Key, StringComparer.Ordinal))
            settingsNode[key] = value;
        node["settings"] = settingsNode;
        node["enabled"] = enabled;
        return node.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static JsonNode? Get(JsonObject node, string name) =>
        node.FirstOrDefault(p => string.Equals(p.Key, name, StringComparison.OrdinalIgnoreCase)).Value;

    private static void Remove(JsonObject node, string name)
    {
        foreach (var key in node.Select(p => p.Key).Where(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase)).ToList())
            node.Remove(key);
    }
}

/// <summary>
/// Spec 053 req 7.3: the profile each kind of work runs on today, computed the way the pre-053 code did. These are
/// frozen copies of <c>LabelResolver</c>, <c>ProfileResolver</c> and <c>ConsolidationSelectorResolver</c>, which later
/// commits delete. The golden-file test checks them against the real code while it still exists.
/// Frozen for the 053 move; do not use elsewhere.
/// </summary>
internal static class LegacyRouting
{
    /// <summary>Splits a comma-separated label string, trimming and dropping empty entries.</summary>
    public static IReadOnlyList<string> SplitLabels(string? labels) =>
        string.IsNullOrWhiteSpace(labels)
            ? []
            : labels.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// <c>LabelResolver.ResolveRequiredLabels</c> with the template's settings: the template's labels, else
    /// <paramref name="defaultRequiredAgentLabels"/>, else none.
    /// </summary>
    public static IReadOnlyList<string> RequiredLabels(PipelineJobTemplate template, string? defaultRequiredAgentLabels) =>
        template.RequiredLabels is { Count: > 0 } labels ? labels : SplitLabels(defaultRequiredAgentLabels);

    /// <summary>
    /// <c>ProfileResolver.ResolveByRequiredLabels</c>: the enabled profiles whose match labels cover every required
    /// label (case-insensitive); the closest fit wins (fewest labels, then the higher priority, then the lower Id).
    /// </summary>
    public static LegacyProfile? ClosestFit(IReadOnlyList<LegacyProfile> profiles, IReadOnlyList<string> requiredLabels)
    {
        var required = new HashSet<string>(requiredLabels, StringComparer.OrdinalIgnoreCase);
        return profiles
            .Where(p => p.Enabled)
            .Where(p => Covers(p.MatchLabels, required))
            .OrderBy(p => p.MatchLabels.Count)
            .ThenByDescending(p => p.Priority)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>
    /// The profile implementation, PR review, decomposition and triage dispatches pick for <paramref name="template"/>
    /// (<c>DispatchOrchestrationService</c>, <c>TriageService</c>). The claim keeps it, because the dispatch pins its Id;
    /// only operator triage pins nothing (<see cref="ClaimProfileForSelector"/>).
    /// </summary>
    public static LegacyProfile? DispatchProfile(
        PipelineJobTemplate template, string? defaultRequiredAgentLabels, IReadOnlyList<LegacyProfile> profiles) =>
        ClosestFit(profiles, RequiredLabels(template, defaultRequiredAgentLabels));

    /// <summary>The profile the claim re-resolves from a bare selector (<c>AssignmentEnricher.ResolveProfile</c>).</summary>
    public static LegacyProfile? ClaimProfileForSelector(string selector, IReadOnlyList<LegacyProfile> profiles) =>
        ClosestFit(profiles, SplitLabels(selector));

    /// <summary>
    /// The profile a consolidation for <paramref name="template"/> runs on: <c>ConsolidationSelectorResolver</c> steps
    /// 1–3 as the API registers it (static configuration <c>new PipelineConfiguration()</c>, live configuration = the
    /// stored one), then the claim's re-resolution of a bare selector, which finds no profile either.
    /// </summary>
    /// <param name="dispatchableKeys">
    /// The job template keys. The API always passes a job template store, so step 3 is filtered, and finds nothing
    /// when no template is known. Null reproduces the Scheduler's unfiltered step 3.
    /// </param>
    public static LegacyProfile? ConsolidationProfile(
        PipelineJobTemplate template,
        string? defaultRequiredAgentLabels,
        IReadOnlyList<LegacyProfile> profiles,
        IReadOnlyCollection<string>? dispatchableKeys)
    {
        // Step 1: the template's labels (the static configuration has no default labels).
        if (template.RequiredLabels is { Count: > 0 } labels)
            return ClosestFit(profiles, labels);

        // Step 2: the live default labels.
        if (!string.IsNullOrWhiteSpace(defaultRequiredAgentLabels))
            return ClosestFit(profiles, SplitLabels(defaultRequiredAgentLabels));

        // Step 3: the highest-priority enabled profile, with a job template when filtered.
        var enabled = profiles
            .Where(p => p.Enabled)
            .OrderByDescending(p => p.Priority)
            .ThenBy(p => p.Id, StringComparer.Ordinal)
            .ToList();
        if (dispatchableKeys is not null)
            enabled = enabled.Where(p => dispatchableKeys.Contains(p.JobTemplateKey())).ToList();
        return enabled.FirstOrDefault();
    }

    private static bool Covers(IReadOnlyList<string> matchLabels, HashSet<string> required)
    {
        if (required.Count == 0)
            return true;
        var match = new HashSet<string>(matchLabels, StringComparer.OrdinalIgnoreCase);
        return required.All(match.Contains);
    }
}
