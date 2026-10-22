using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.AspNetCore.Components;

namespace CodingAgent.Web.Components.Shared;

/// <summary>
/// Spec 053: the routing editor of the Routing page and of the repository page. A row names a route (profile, model,
/// effort) or uses what it inherits. On the Routing page (six rows) a task row without a route uses the Default row; on
/// the repository page (<see cref="Inherited"/> set, five rows whatever the template's switches say, req 3.7) a row
/// inherits the global table's route. The API validates the rows when they are saved.
/// </summary>
public partial class RouteTable
{
    /// <summary>The rows to edit; the table changes them in place.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<RouteRow> Rows { get; set; } = [];

    /// <summary>The profiles a route can name. A disabled one is offered only on the row that names it.</summary>
    [Parameter, EditorRequired] public IReadOnlyList<UiRoutingProfile> Profiles { get; set; } = [];

    /// <summary>The global routing table the rows inherit; set on the repository page.</summary>
    [Parameter] public AgentRouting? Inherited { get; set; }

    /// <summary>Kiro models from Fetch Models, offered on the rows of Kiro profiles.</summary>
    [Parameter] public IReadOnlyList<string> KiroModels { get; set; } = [];

    private bool Repository => Inherited is not null;

    private string InheritLabel => Repository ? "Inherit" : "Use default";

    private static string RowKey(RouteRow row) => row.TaskType?.ToString().ToLowerInvariant() ?? "default";

    private static string RowLabel(RouteRow row) =>
        row.TaskType is { } taskType ? AgentRouteLabels.TaskType(taskType) : AgentRouteLabels.DefaultRow;

    private UiRoutingProfile? ProfileOf(string? profileId) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.Ordinal));

    private AgentTool? ToolOf(string? profileId) => AgentToolCatalog.Find(ProfileOf(profileId)?.Tool);

    private IEnumerable<UiRoutingProfile> OfferedProfiles(RouteRow row) =>
        Profiles.Where(p => p.Enabled || string.Equals(p.Id, row.ProfileId, StringComparison.Ordinal));

    private static string ProfileLabel(UiRoutingProfile profile)
    {
        var tool = AgentToolCatalog.Find(profile.Tool)?.DisplayName ?? "no tool";
        return profile.Enabled ? $"{profile.Name} ({tool})" : $"{profile.Name} ({tool}, disabled)";
    }

    /// <summary>The models the model field offers besides <c>auto</c>: the tool's built-in list, or Kiro's fetched models.</summary>
    private IReadOnlyList<string> ModelsFor(AgentTool? tool) =>
        tool switch
        {
            null => [],
            _ when tool == AgentToolCatalog.Kiro => KiroModels,
            _ => tool.BuiltInModels,
        };

    private static IReadOnlyList<string> EffortsFor(AgentTool? tool) =>
        tool is null ? AgentToolCatalog.AllEfforts : [AgentToolCatalog.Auto, .. tool.Efforts];

    private void AfterProfileChanged(RouteRow row)
    {
        // An effort the new tool does not take would be refused on save; OpenCode has no effort at all (req 2.5).
        if (ToolOf(row.ProfileId) is { } tool && !AgentToolCatalog.AcceptsEffort(tool, row.Effort))
            row.Effort = AgentToolCatalog.Auto;
    }

    private void SetRoute(RouteRow row)
    {
        var inherited = InheritedRoute(row).Route;
        row.UseRoute = true;
        row.ProfileId = inherited?.ProfileId ?? "";
        row.Model = inherited?.Model ?? AgentToolCatalog.Auto;
        row.Effort = inherited?.Effort ?? AgentToolCatalog.Auto;
    }

    private static void Inherit(RouteRow row) => row.UseRoute = false;

    /// <summary>Req 3.2: puts this row's route on all five task rows.</summary>
    private void UseForAll(RouteRow source)
    {
        foreach (var row in Rows.Where(r => r.TaskType is not null))
        {
            row.UseRoute = true;
            row.ProfileId = source.ProfileId;
            row.Model = source.Model;
            row.Effort = source.Effort;
        }
    }

    /// <summary>What a row without a route uses, and where that comes from.</summary>
    private (AgentRoute? Route, string Source) InheritedRoute(RouteRow row)
    {
        if (!Repository)
        {
            var defaultRow = Rows.FirstOrDefault(r => r.TaskType is null);
            return defaultRow is { ProfileId.Length: > 0 }
                ? (defaultRow.ToRoute(), AgentRouteLabels.DefaultRow.ToLowerInvariant())
                : (null, "");
        }

        // A table read from JSON can carry null rows or no row dictionary; neither is a route.
        if (row.TaskType is { } taskType && Inherited!.ByTaskType?.TryGetValue(taskType, out var taskRow) == true && taskRow is not null)
            return (taskRow, AgentRouteLabels.Source(AgentRouteSource.GlobalTaskRow));
        return Inherited!.Default is { } globalDefault
            ? (globalDefault, AgentRouteLabels.Source(AgentRouteSource.GlobalDefault))
            : (null, "");
    }

    /// <summary>Req 3.2: profile · model · effort · job template key · source of what the row inherits.</summary>
    private string InheritText(RouteRow row)
    {
        var (route, source) = InheritedRoute(row);
        if (route is null)
            return AgentRouteRefusalReason.NoRouteText;
        var profile = ProfileOf(route.ProfileId);
        return string.Join(" · ",
            profile?.Name ?? route.ProfileId,
            RouteText.Model(route.Model),
            $"effort {route.Effort}",
            profile?.JobTemplateKey ?? "?",
            source);
    }
}

/// <summary>
/// One row of a <see cref="RouteTable"/>: the Default row (<see cref="TaskType"/> null, always a route) or a task row,
/// which names a route only when <see cref="UseRoute"/> is set.
/// </summary>
public sealed class RouteRow
{
    public WorkItemTaskType? TaskType { get; init; }

    public bool UseRoute { get; set; }

    public string ProfileId { get; set; } = "";

    public string Model { get; set; } = AgentToolCatalog.Auto;

    public string Effort { get; set; } = AgentToolCatalog.Auto;

    public AgentRoute ToRoute() => new(ProfileId, Model.Trim(), Effort);

    /// <summary>The six rows of the global table: Default, then the task types in table order.</summary>
    public static List<RouteRow> ForRouting(AgentRouting routing)
    {
        ArgumentNullException.ThrowIfNull(routing);
        var rows = new List<RouteRow> { From(null, routing.Default, alwaysRoute: true) };
        rows.AddRange(AgentRouteLabels.TaskTypes.Select(t => From(t, routing.ByTaskType?.GetValueOrDefault(t), alwaysRoute: false)));
        return rows;
    }

    /// <summary>The global table of <paramref name="rows"/>. A Default row without a profile is no default route.</summary>
    public static AgentRouting ToRouting(IEnumerable<RouteRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var list = rows.ToList();
        var defaultRow = list.FirstOrDefault(r => r.TaskType is null);
        return new AgentRouting
        {
            Default = defaultRow is { ProfileId.Length: > 0 } ? defaultRow.ToRoute() : null,
            ByTaskType = ToRepositoryRoutes(list),
        };
    }

    /// <summary>The five task rows of a repository.</summary>
    public static List<RouteRow> ForRepository(IReadOnlyDictionary<WorkItemTaskType, AgentRoute>? routes) =>
        AgentRouteLabels.TaskTypes.Select(t => From(t, routes?.GetValueOrDefault(t), alwaysRoute: false)).ToList();

    /// <summary>The task rows of <paramref name="rows"/> that name a route.</summary>
    public static Dictionary<WorkItemTaskType, AgentRoute> ToRepositoryRoutes(IEnumerable<RouteRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        return rows
            .Where(r => r is { TaskType: not null, UseRoute: true })
            .ToDictionary(r => r.TaskType!.Value, r => r.ToRoute());
    }

    private static RouteRow From(WorkItemTaskType? taskType, AgentRoute? route, bool alwaysRoute) => new()
    {
        TaskType = taskType,
        UseRoute = alwaysRoute || route is not null,
        ProfileId = route?.ProfileId ?? "",
        Model = route?.Model ?? AgentToolCatalog.Auto,
        Effort = route?.Effort ?? AgentToolCatalog.Auto,
    };
}
