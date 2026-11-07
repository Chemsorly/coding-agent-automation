using CodingAgent.Contracts.Ui;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;

namespace CodingAgent.Api.Ui;

/// <summary>
/// Spec 053 req 3.3, 3.4, 10.2: the routes every reader may see. Per template and task type, the route a work item
/// would get, resolved like the router resolves it: profile name, tool, model, effort, source and job template key,
/// never the profile's tool settings or MCP servers.
/// </summary>
internal static class ResolvedRoutes
{
    /// <summary>The resolved routes of <paramref name="templates"/>, five per template in table order, by template Id.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<UiResolvedRoute>> For(
        IEnumerable<PipelineJobTemplate> templates,
        AgentRouting routing,
        IReadOnlyList<AgentProfile> profiles,
        IReadOnlyDictionary<string, string> toolsByKey,
        IReadOnlyList<AgentCredentialPause>? pauses = null)
    {
        var paused = (pauses ?? []).ToDictionary(p => p.Credential, StringComparer.Ordinal);
        return templates.ToDictionary(
            t => t.Id,
            IReadOnlyList<UiResolvedRoute> (t) => AgentRouteLabels.TaskTypes
                .Select(taskType => Resolve(taskType, t, routing, profiles, toolsByKey, paused))
                .ToList(),
            StringComparer.Ordinal);
    }

    /// <summary>
    /// One task type's route. A refusal sets only the problem. A route whose job template the Helm values do not define
    /// is resolved, as the router resolves it (the dispatcher defers such work), and carries that as its problem.
    /// </summary>
    private static UiResolvedRoute Resolve(
        WorkItemTaskType taskType,
        PipelineJobTemplate template,
        AgentRouting routing,
        IReadOnlyList<AgentProfile> profiles,
        IReadOnlyDictionary<string, string> toolsByKey,
        IReadOnlyDictionary<string, AgentCredentialPause> paused)
    {
        var result = AgentRouteResolver.Resolve(taskType, template, routing, profiles, toolsByKey);
        if (result.Snapshot is not { } route || result.Profile is not { } profile)
            return new UiResolvedRoute(taskType, null, null, null, null, null, null, result.RefusalText);

        var key = profile.JobTemplateKey();
        var problem = toolsByKey.Count > 0 && !toolsByKey.ContainsKey(key)
            ? HoldTexts.NoTemplate(key, profile.DisplayName)
            : null;
        // Spec 056 req 4.3: a row whose credential is paused says so.
        var pausedText = AgentCredentials.For(profile, toolsByKey) is { } credential && paused.TryGetValue(credential, out var pause)
            ? HoldTexts.CredentialPaused(pause)
            : null;
        return new UiResolvedRoute(taskType, route.ProfileName, route.Tool, route.Model, route.Effort, route.Source, key, problem, pausedText);
    }
}
