using System.Text;
using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;

namespace CodingAgent.Web.TestUtilities;

/// <summary>
/// Spec 053 req 12.4: the one builder unit, integration and E2E tests use for agent profiles, routing tables and the
/// job template tools they need. A profile runs on a job template; the route names the profile, a model and an
/// effort; <see cref="ToolsByKey"/> stands in for <c>JobTemplateStore.ToolsByKey()</c>.
/// </summary>
public static class AgentRoutingTestData
{
    /// <summary>The job template key of <see cref="Profile"/> when none is given.</summary>
    public const string DefaultJobTemplate = "dotnet,kiro";

    /// <summary>The tool of <see cref="DefaultJobTemplate"/>.</summary>
    public const string DefaultTool = JobTemplateProviderType.Kiro;

    /// <summary>An agent profile on <paramref name="jobTemplate"/>, with no tool settings and no MCP servers by default.</summary>
    public static AgentProfile Profile(
        string id = "profile-kiro",
        string jobTemplate = DefaultJobTemplate,
        string? displayName = null,
        bool enabled = true,
        IReadOnlyDictionary<string, string>? settings = null,
        IReadOnlyList<McpServerConfig>? mcpServers = null) => new()
    {
        Id = id,
        DisplayName = displayName ?? id,
        JobTemplate = jobTemplate,
        Enabled = enabled,
        Settings = settings ?? new Dictionary<string, string>(),
        McpServers = mcpServers ?? [],
    };

    /// <summary>A route to <paramref name="profileId"/>.</summary>
    public static AgentRoute Route(string profileId = "profile-kiro", string model = "auto", string effort = "auto") =>
        new(profileId, model, effort);

    /// <summary>A routing table whose default row routes to <paramref name="defaultProfileId"/>, plus the given task rows.</summary>
    public static AgentRouting Routing(
        string defaultProfileId = "profile-kiro",
        string model = "auto",
        string effort = "auto",
        params (WorkItemTaskType TaskType, AgentRoute Route)[] taskRows) => new()
    {
        Default = Route(defaultProfileId, model, effort),
        ByTaskType = taskRows.ToDictionary(r => r.TaskType, r => r.Route),
    };

    /// <summary>Job template key → tool. With no arguments: <see cref="DefaultJobTemplate"/> on <see cref="DefaultTool"/>.</summary>
    public static IReadOnlyDictionary<string, string> ToolsByKey(params (string JobTemplateKey, string Tool)[] templates)
    {
        var pairs = templates.Length == 0 ? [(DefaultJobTemplate, DefaultTool)] : templates;
        return pairs.ToDictionary(t => t.JobTemplateKey, t => t.Tool, StringComparer.Ordinal);
    }

    /// <summary>
    /// A job template store with one template per key and tool, as the Helm values define them. With no arguments:
    /// <see cref="DefaultJobTemplate"/> on <see cref="DefaultTool"/>. A claim is refused with <c>no_tool</c> on a
    /// template without a <c>providerType</c> (design DD4), so test hosts register these.
    /// </summary>
    public static JobTemplateStore JobTemplates(params (string JobTemplateKey, string Tool)[] templates)
    {
        var yaml = new StringBuilder();
        foreach (var (key, tool) in ToolsByKey(templates))
            yaml.Append("- labels: \"").Append(key).Append("\"\n  image: agent:test\n  providerType: ").Append(tool).Append('\n');
        return JobTemplateStore.LoadFromYaml(yaml.ToString());
    }

    /// <summary>Stores <paramref name="routing"/>, by default <see cref="Routing"/> with its defaults, as the global routing table.</summary>
    public static Task SeedRoutingAsync(
        IDbContextFactory<PipelineDbContext> dbFactory, AgentRouting? routing = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(dbFactory);
        return new AgentRoutingStore(dbFactory).SaveAsync(routing ?? Routing(), ct);
    }
}
