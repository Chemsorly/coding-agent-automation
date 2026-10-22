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

    private static bool Names(AgentRoute? route, string profileId) =>
        route is not null && string.Equals(route.ProfileId, profileId, StringComparison.Ordinal);
}
