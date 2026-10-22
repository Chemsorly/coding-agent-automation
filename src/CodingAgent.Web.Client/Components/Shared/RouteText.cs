using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Components.Shared;

/// <summary>How resolved routes read on the Pipelines page and the repository page (spec 053 req 3.3, 3.4).</summary>
public static class RouteText
{
    /// <summary>The text of a route that does not resolve, in the compact form.</summary>
    public const string NoRoute = "no route";

    /// <summary>
    /// The routes in one line, grouped by profile and model: the group that covers most task types first and without
    /// its task types, then the others, for example "Kiro · sonnet; Decomposition, Triage: Claude · opus".
    /// </summary>
    public static string Compact(IReadOnlyList<UiResolvedRoute>? routes)
    {
        if (routes is not { Count: > 0 })
            return "";
        var groups = routes
            .GroupBy(r => r.ProfileName is not null ? $"{r.ProfileName} · {r.Model}" : NoRoute)
            .OrderByDescending(g => g.Count())
            .ThenBy(g => Order(g.First().TaskType))
            .ToList();
        return string.Join("; ", groups.Select((g, i) => i == 0
            ? g.Key
            : $"{string.Join(", ", g.Select(r => AgentRouteLabels.TaskType(r.TaskType)))}: {g.Key}"));
    }

    /// <summary>Profile · tool · model · effort of one resolved route, or its problem.</summary>
    public static string Describe(UiResolvedRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        if (route.ProfileName is null)
            return route.Problem ?? NoRoute;
        var tool = AgentToolCatalog.Find(route.Tool)?.DisplayName ?? "no tool";
        return $"{route.ProfileName} · {tool} · {Model(route.Model)} · effort {route.Effort}";
    }

    /// <summary>Where a resolved route comes from: repository, global task row or global default.</summary>
    public static string Source(UiResolvedRoute route)
    {
        ArgumentNullException.ThrowIfNull(route);
        return route.Source is { } source ? AgentRouteLabels.Source(source) : "—";
    }

    /// <summary>A model as the pages show it; <c>auto</c> says that the tool chooses (req 2.3).</summary>
    public static string Model(string? model) =>
        string.Equals(model, AgentToolCatalog.Auto, StringComparison.Ordinal) ? "auto (the tool chooses)" : model ?? "—";

    private static int Order(WorkItemTaskType taskType)
    {
        var index = AgentRouteLabels.TaskTypes.ToList().IndexOf(taskType);
        return index < 0 ? int.MaxValue : index;
    }
}
