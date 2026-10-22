using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Services;

/// <summary>
/// Why a route was refused: the closed set of the <c>reason</c> tag of <c>pipeline.routing.refusals</c> and of the
/// <c>RefusalReason</c> log property (spec 053 req 9.10, 9.13).
/// </summary>
public static class AgentRouteRefusalReason
{
    /// <summary>No row at any level.</summary>
    public const string NoRoute = "no_route";

    /// <summary>The route names a profile that does not exist or is disabled.</summary>
    public const string ProfileUnavailable = "profile_unavailable";

    /// <summary>The profile's job template has no known tool.</summary>
    public const string NoTool = "no_tool";

    /// <summary>The profile's job template does not exist (consolidation at creation).</summary>
    public const string TemplateMissing = "template_missing";

    /// <summary>The profile's job template is not the one the work item was queued for.</summary>
    public const string TemplateChanged = "template_changed";

    /// <summary>The built agent config's Id is used by another config of the assignment.</summary>
    public const string ConfigCollision = "config_collision";

    public static IReadOnlyList<string> All { get; } =
        [NoRoute, ProfileUnavailable, NoTool, TemplateMissing, TemplateChanged, ConfigCollision];

    /// <summary>The text shown and logged when no route resolves (req 2.9).</summary>
    public const string NoRouteText = "No agent route: set the default route in Settings → Agents → Routing";

    /// <summary>
    /// The work item payload property that holds the reason code of a refused claim. A refused item is Failed with
    /// <c>InfrastructureFailure</c> (req 4.5, no new enum value), the class a late Running report recovers; the code tells
    /// the recovery that no retry changes this failure.
    /// </summary>
    public const string PayloadProperty = "routeRefusal";
}

/// <summary>
/// The outcome of <see cref="AgentRouteResolver.Resolve"/>: a route snapshot and its profile, or a refusal with the
/// reason code (<see cref="AgentRouteRefusalReason"/>) and the text to show.
/// </summary>
public sealed record AgentRouteResult
{
    private AgentRouteResult(AgentRouteSnapshot? snapshot, AgentProfile? profile, string? reason, string? text)
    {
        Snapshot = snapshot;
        Profile = profile;
        RefusalReason = reason;
        RefusalText = text;
    }

    public AgentRouteSnapshot? Snapshot { get; }

    /// <summary>The routed profile; null for a refusal.</summary>
    public AgentProfile? Profile { get; }

    /// <summary>One of <see cref="AgentRouteRefusalReason"/>; null when routed.</summary>
    public string? RefusalReason { get; }

    /// <summary>The text shown on the refusing surface; null when routed.</summary>
    public string? RefusalText { get; }

    public bool IsRefused => Snapshot is null;

    public static AgentRouteResult Routed(AgentRouteSnapshot snapshot, AgentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(profile);
        return new(snapshot, profile, null, null);
    }

    public static AgentRouteResult Refused(string reason, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        return new(null, null, reason, text);
    }
}

/// <summary>
/// Spec 053 req 4.1: the route of a work item is the repository row for its task type, else the global row for its
/// task type, else the global default. The first row found is used; a row that does not resolve is refused and does
/// not fall through to the next level, and nothing picks a profile no row names (D15). Pure.
/// </summary>
public static class AgentRouteResolver
{
    /// <summary>
    /// Resolves the route of <paramref name="taskType"/> work for <paramref name="template"/>.
    /// </summary>
    /// <param name="taskType">The work item's task type.</param>
    /// <param name="template">The template that owns the work item's repository; null applies the global rows only.</param>
    /// <param name="routing">The global routing table.</param>
    /// <param name="profiles">All agent profiles.</param>
    /// <param name="toolsByKey">
    /// Job template key → <c>providerType</c> (<c>JobTemplateStore.ToolsByKey</c>). Empty means no job templates are
    /// known (a host without templates): the template checks are skipped and the snapshot's tool is empty. A missing
    /// job template is not refused here; the dispatcher defers such work and alerts.
    /// </param>
    public static AgentRouteResult Resolve(
        WorkItemTaskType taskType,
        PipelineJobTemplate? template,
        AgentRouting routing,
        IReadOnlyList<AgentProfile> profiles,
        IReadOnlyDictionary<string, string> toolsByKey)
    {
        ArgumentNullException.ThrowIfNull(routing);
        ArgumentNullException.ThrowIfNull(profiles);
        ArgumentNullException.ThrowIfNull(toolsByKey);

        if (!TryFindRow(taskType, template, routing, out var row, out var source))
            return AgentRouteResult.Refused(AgentRouteRefusalReason.NoRoute, AgentRouteRefusalReason.NoRouteText);

        var profile = profiles.FirstOrDefault(p => string.Equals(p.Id, row.ProfileId, StringComparison.Ordinal));
        if (profile is null || !profile.Enabled)
        {
            return AgentRouteResult.Refused(
                AgentRouteRefusalReason.ProfileUnavailable,
                $"The {SourceText(source)} route for {AgentRouteLabels.TaskType(taskType)} names agent profile " +
                $"'{profile?.DisplayName ?? row.ProfileId}', which does not exist or is disabled");
        }

        var key = profile.JobTemplateKey();
        var tool = "";
        if (toolsByKey.Count > 0 && toolsByKey.TryGetValue(key, out var providerType))
        {
            var known = AgentToolCatalog.Find(providerType);
            if (known is null)
                return AgentRouteResult.Refused(AgentRouteRefusalReason.NoTool, NoToolText(key, profile));
            tool = known.Id;
        }

        return AgentRouteResult.Routed(
            new AgentRouteSnapshot(profile.Id, profile.DisplayName, tool, row.Model, row.Effort, source),
            profile);
    }

    /// <summary>The refusal text for a job template without a known tool (design "Route Resolution").</summary>
    public static string NoToolText(string jobTemplateKey, AgentProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        return $"Job template {jobTemplateKey} of agent profile '{profile.DisplayName}' has no providerType";
    }

    private static bool TryFindRow(
        WorkItemTaskType taskType,
        PipelineJobTemplate? template,
        AgentRouting routing,
        out AgentRoute row,
        out AgentRouteSource source)
    {
        // Tables read from JSON can be null or carry null rows; a null row is no row.
        if (template?.AgentRoutes is { } repositoryRows
            && repositoryRows.TryGetValue(taskType, out var repositoryRow) && repositoryRow is not null)
        {
            (row, source) = (repositoryRow, AgentRouteSource.Repository);
            return true;
        }

        if (routing.ByTaskType is { } globalRows
            && globalRows.TryGetValue(taskType, out var globalRow) && globalRow is not null)
        {
            (row, source) = (globalRow, AgentRouteSource.GlobalTaskRow);
            return true;
        }

        if (routing.Default is { } defaultRow)
        {
            (row, source) = (defaultRow, AgentRouteSource.GlobalDefault);
            return true;
        }

        (row, source) = (null!, default);
        return false;
    }

    private static string SourceText(AgentRouteSource source) => source switch
    {
        AgentRouteSource.Repository => "repository",
        AgentRouteSource.GlobalTaskRow => "global",
        _ => "global default",
    };
}
