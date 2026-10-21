using System.Text.RegularExpressions;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Spec 053 req 2.2–2.6, 3.5, 7.12, 10.5: checks routes before they are saved or imported. Every method returns one
/// line per problem ("Decomposition: model is required"); an empty list means valid. Pure.
/// </summary>
public static partial class AgentRouteValidator
{
    /// <summary>The longest model name a route accepts.</summary>
    public const int MaxModelLength = 200;

    /// <summary>
    /// Checks the global routing table. The default row is required once any profile exists. When
    /// <paramref name="toolsByKey"/> is empty (no job templates known), the job template checks are skipped.
    /// </summary>
    public static IReadOnlyList<string> ValidateRouting(
        AgentRouting? routing,
        IReadOnlyList<AgentProfile> profiles,
        IReadOnlyDictionary<string, string> toolsByKey)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(toolsByKey);
        if (routing is null)
            return ["The routing table is missing"];

        var errors = new List<string>();
        if (routing.Default is null)
        {
            if (profiles.Count > 0)
                errors.Add($"{AgentRouteLabels.DefaultRow}: a default route is required");
        }
        else
        {
            errors.AddRange(ValidateRoute(AgentRouteLabels.DefaultRow, routing.Default, profiles, toolsByKey));
        }

        errors.AddRange(ValidateRows(routing.ByTaskType, profiles, toolsByKey));
        return errors;
    }

    /// <summary>
    /// Checks a template's repository rows (req 3.5). Null means "keep the stored rows" and is valid.
    /// </summary>
    public static IReadOnlyList<string> ValidateTemplateRoutes(
        IReadOnlyDictionary<WorkItemTaskType, AgentRoute>? rows,
        IReadOnlyList<AgentProfile> profiles,
        IReadOnlyDictionary<string, string> toolsByKey)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(toolsByKey);
        return rows is null ? [] : ValidateRows(rows, profiles, toolsByKey);
    }

    /// <summary>
    /// Checks the routing of an imported bundle (req 7.12): its shape only. Task type keys, model rules, the effort
    /// when the profile's tool is known, and that each route's profile is in the bundle. Missing job templates are not
    /// refused: a bundle from another cluster imports, and the Agent Profiles page flags them. A bundle without
    /// routing (an older bundle) is valid; the configuration move writes it.
    /// </summary>
    public static IReadOnlyList<string> ValidateImport(
        AgentRouting? routing,
        IReadOnlyList<AgentProfile> bundleProfiles,
        IReadOnlyDictionary<string, string> toolsByKey)
    {
        ArgumentNullException.ThrowIfNull(bundleProfiles);
        ArgumentNullException.ThrowIfNull(toolsByKey);
        if (routing is null)
            return [];

        var errors = new List<string>();
        if (routing.Default is not null)
            errors.AddRange(ValidateImportedRoute(AgentRouteLabels.DefaultRow, routing.Default, bundleProfiles, toolsByKey));

        foreach (var (taskType, route) in routing.ByTaskType ?? new Dictionary<WorkItemTaskType, AgentRoute>())
        {
            if (!Enum.IsDefined(taskType))
            {
                errors.Add(UnknownTaskType(taskType));
                continue;
            }
            errors.AddRange(ValidateImportedRoute(AgentRouteLabels.TaskType(taskType), route, bundleProfiles, toolsByKey));
        }
        return errors;
    }

    /// <summary>
    /// Checks one route: the profile exists and is enabled and, when job templates are known, its template exists and
    /// has a tool; the model (<see cref="ValidateModel"/>); the effort for that tool (<see cref="ValidateEffort"/>).
    /// </summary>
    public static IReadOnlyList<string> ValidateRoute(
        string rowLabel,
        AgentRoute? route,
        IReadOnlyList<AgentProfile> profiles,
        IReadOnlyDictionary<string, string> toolsByKey)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(toolsByKey);
        if (route is null)
            return [$"{rowLabel}: the route is missing"];

        var errors = new List<string>();
        AgentTool? tool = null;
        var profile = profiles.FirstOrDefault(p => string.Equals(p.Id, route.ProfileId, StringComparison.Ordinal));
        if (string.IsNullOrWhiteSpace(route.ProfileId))
        {
            errors.Add($"{rowLabel}: an agent profile is required");
        }
        else if (profile is null)
        {
            errors.Add($"{rowLabel}: agent profile '{route.ProfileId}' does not exist");
        }
        else if (!profile.Enabled)
        {
            errors.Add($"{rowLabel}: agent profile '{profile.DisplayName}' is disabled");
        }
        else if (toolsByKey.Count > 0)
        {
            var key = profile.JobTemplateKey();
            if (!toolsByKey.TryGetValue(key, out var providerType))
                errors.Add($"{rowLabel}: job template {key} of agent profile '{profile.DisplayName}' does not exist in the Helm values");
            else if ((tool = AgentToolCatalog.Find(providerType)) is null)
                errors.Add($"{rowLabel}: {AgentRouteResolver.NoToolText(key, profile)}");
        }

        errors.AddRange(ValidateModel(rowLabel, route.Model));
        errors.AddRange(ValidateEffort(rowLabel, route.Effort, tool));
        return errors;
    }

    /// <summary>
    /// The model is required, at most <see cref="MaxModelLength"/> characters, and of letters, digits, <c>.</c>,
    /// <c>_</c>, <c>-</c> and <c>/</c> (req 2.3). <c>auto</c> is valid.
    /// </summary>
    public static IReadOnlyList<string> ValidateModel(string rowLabel, string? model)
    {
        if (string.IsNullOrWhiteSpace(model))
            return [$"{rowLabel}: model is required (auto lets the tool choose)"];
        if (model.Length > MaxModelLength)
            return [$"{rowLabel}: model is longer than {MaxModelLength} characters"];
        if (!ModelPattern().IsMatch(model))
            return [$"{rowLabel}: model may contain only letters, digits, '.', '_', '-' and '/'"];
        return [];
    }

    /// <summary>
    /// The effort is <c>auto</c> or one <paramref name="tool"/> accepts (req 2.5). When the tool is not known, any
    /// effort of any tool is accepted.
    /// </summary>
    public static IReadOnlyList<string> ValidateEffort(string rowLabel, string? effort, AgentTool? tool)
    {
        if (string.IsNullOrWhiteSpace(effort))
            return [$"{rowLabel}: effort is required (auto uses the tool's default)"];
        if (tool is null)
        {
            return AgentToolCatalog.AllEfforts.Contains(effort, StringComparer.Ordinal)
                ? []
                : [$"{rowLabel}: effort '{effort}' is unknown"];
        }
        if (AgentToolCatalog.AcceptsEffort(tool, effort))
            return [];
        return tool.Efforts.Count == 0
            ? [$"{rowLabel}: {tool.DisplayName} has no effort setting; use auto"]
            : [$"{rowLabel}: {tool.DisplayName} does not accept effort '{effort}' (use auto, {string.Join(", ", tool.Efforts)})"];
    }

    private static List<string> ValidateRows(
        IReadOnlyDictionary<WorkItemTaskType, AgentRoute>? rows,
        IReadOnlyList<AgentProfile> profiles,
        IReadOnlyDictionary<string, string> toolsByKey)
    {
        var errors = new List<string>();
        foreach (var (taskType, route) in rows ?? new Dictionary<WorkItemTaskType, AgentRoute>())
        {
            if (!Enum.IsDefined(taskType))
            {
                errors.Add(UnknownTaskType(taskType));
                continue;
            }
            errors.AddRange(ValidateRoute(AgentRouteLabels.TaskType(taskType), route, profiles, toolsByKey));
        }
        return errors;
    }

    private static List<string> ValidateImportedRoute(
        string rowLabel,
        AgentRoute? route,
        IReadOnlyList<AgentProfile> bundleProfiles,
        IReadOnlyDictionary<string, string> toolsByKey)
    {
        if (route is null)
            return [$"{rowLabel}: the route is missing"];

        var errors = new List<string>();
        AgentTool? tool = null;
        var profile = bundleProfiles.FirstOrDefault(p => string.Equals(p.Id, route.ProfileId, StringComparison.Ordinal));
        if (profile is null)
            errors.Add($"{rowLabel}: agent profile '{route.ProfileId}' is not in the bundle");
        else if (toolsByKey.TryGetValue(profile.JobTemplateKey(), out var providerType))
            tool = AgentToolCatalog.Find(providerType);

        errors.AddRange(ValidateModel(rowLabel, route.Model));
        errors.AddRange(ValidateEffort(rowLabel, route.Effort, tool));
        return errors;
    }

    private static string UnknownTaskType(WorkItemTaskType taskType) =>
        $"Task type {(int)taskType} is unknown";

    [GeneratedRegex(@"^[a-zA-Z0-9._\-/]+$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex ModelPattern();
}
