using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Spec 053 req 1.5: the routes that name an agent profile, which keep it from being disabled or deleted. Pure.
/// </summary>
public static class AgentRouteReferences
{
    /// <summary>
    /// The global and repository rows that name <paramref name="profileId"/>, as "Global · Decomposition" and
    /// "Repository X · Implementation", global rows first and in table order.
    /// </summary>
    public static IReadOnlyList<string> Find(
        string profileId, AgentRouting? routing, IEnumerable<PipelineJobTemplate> templates)
    {
        ArgumentNullException.ThrowIfNull(templates);
        var found = new List<string>();
        if (Names(routing?.Default, profileId))
            found.Add($"Global · {AgentRouteLabels.DefaultRow}");
        found.AddRange(RowsNaming(routing?.ByTaskType, profileId).Select(t => $"Global · {t}"));
        foreach (var template in templates.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            found.AddRange(RowsNaming(template.AgentRoutes, profileId).Select(t => $"Repository {template.Name} · {t}"));
        return found;
    }

    /// <summary>
    /// The rows naming <paramref name="profile"/> whose effort the tool of its job template does not accept (req 2.5), one
    /// line per row and labelled like <see cref="Find"/>. A profile saved onto a job template of another tool must not
    /// leave such rows, or every later save of the routing table is refused. Empty when the tool is unknown.
    /// </summary>
    public static IReadOnlyList<string> EffortsItsToolRefuses(
        AgentProfile profile, AgentRouting? routing, IEnumerable<PipelineJobTemplate> templates,
        IReadOnlyDictionary<string, string> toolsByKey)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(templates);
        ArgumentNullException.ThrowIfNull(toolsByKey);
        if (!toolsByKey.TryGetValue(profile.JobTemplateKey(), out var providerType) || AgentToolCatalog.Find(providerType) is not { } tool)
            return [];

        var rows = new List<(string Label, AgentRoute Route)>();
        if (routing?.Default is { } defaultRoute)
            rows.Add(($"Global · {AgentRouteLabels.DefaultRow}", defaultRoute));
        rows.AddRange(Routes(routing?.ByTaskType).Select(r => ($"Global · {r.Label}", r.Route)));
        foreach (var template in templates.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
            rows.AddRange(Routes(template.AgentRoutes).Select(r => ($"Repository {template.Name} · {r.Label}", r.Route)));

        return rows
            .Where(r => Names(r.Route, profile.Id))
            .SelectMany(r => AgentRouteValidator.ValidateEffort(r.Label, r.Route.Effort, tool))
            .ToList();
    }

    /// <summary>The refusal of <paramref name="action"/> ("disabled", "deleted") while <paramref name="routes"/> name the profile.</summary>
    public static string RefusalText(string profileName, string action, IReadOnlyList<string> routes)
    {
        ArgumentNullException.ThrowIfNull(routes);
        return $"Agent profile '{profileName}' cannot be {action} while routes name it: {string.Join(", ", routes)}. " +
            "Change those routes first.";
    }

    private static IEnumerable<string> RowsNaming(IReadOnlyDictionary<WorkItemTaskType, AgentRoute>? rows, string profileId) =>
        rows is null
            ? []
            : AgentRouteLabels.TaskTypes
                .Where(t => rows.TryGetValue(t, out var route) && Names(route, profileId))
                .Select(AgentRouteLabels.TaskType);

    private static IEnumerable<(string Label, AgentRoute Route)> Routes(IReadOnlyDictionary<WorkItemTaskType, AgentRoute>? rows) =>
        rows is null
            ? []
            : AgentRouteLabels.TaskTypes
                .Where(rows.ContainsKey)
                .Select(t => (AgentRouteLabels.TaskType(t), rows[t]));

    private static bool Names(AgentRoute? route, string profileId) =>
        route is not null && string.Equals(route.ProfileId, profileId, StringComparison.Ordinal);
}
