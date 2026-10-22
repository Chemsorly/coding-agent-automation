using CodingAgent.Pipeline.Models;

namespace CodingAgent.Contracts.Ui;

/// <summary>
/// A Helm job template as the Agent Profiles and Routing pages list it: its key and its tool (the <c>providerType</c>;
/// empty when the template sets none).
/// </summary>
public sealed record UiJobTemplate(string Key, string Tool);

/// <summary>
/// An agent profile as the routing editors offer it (spec 053): enough to pick it for a route and to say what a row
/// inherits, and none of its tool settings or MCP servers.
/// </summary>
/// <param name="Tool">The tool of its job template; empty when the template is missing or has no tool.</param>
public sealed record UiRoutingProfile(string Id, string Name, string Tool, string JobTemplateKey, bool Enabled);

/// <summary>
/// The Routing page (<c>GET /api/ui/settings/agent-routing</c>, admin): the global routing table, the profiles a route
/// can name and the job templates they run on. Repository rows are edited on the repository page.
/// </summary>
public sealed record UiAgentRoutingPage(
    AgentRouting Routing,
    IReadOnlyList<UiRoutingProfile> Profiles,
    IReadOnlyList<UiJobTemplate> JobTemplates);

/// <summary>
/// The Agent Profiles page (<c>GET /api/ui/settings/agent-profiles</c>, admin): the profiles, MCP server secrets
/// masked, and the job templates a profile can run on.
/// </summary>
public sealed record UiAgentProfilesPage(IReadOnlyList<AgentProfile> Profiles, IReadOnlyList<UiJobTemplate> JobTemplates);

/// <summary>
/// The route a template's work of one task type resolves to (spec 053 req 3.3, D17): what every reader may see of it.
/// When the route does not resolve, only <paramref name="Problem"/> is set, with the refusal text a work item would get.
/// A route to a job template the Helm values do not define is resolved and names that as its problem.
/// </summary>
public sealed record UiResolvedRoute(
    WorkItemTaskType TaskType,
    string? ProfileName,
    string? Tool,
    string? Model,
    string? Effort,
    AgentRouteSource? Source,
    string? JobTemplateKey,
    string? Problem);
