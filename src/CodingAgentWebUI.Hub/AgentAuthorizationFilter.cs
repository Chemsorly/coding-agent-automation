using System.Reflection;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Hub;

/// <summary>
/// Marker attribute for hub methods that require the calling agent to have an active job.
/// Convention: the first parameter of methods decorated with this attribute is always <c>jobId</c> (<see cref="JobId"/>).
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresActiveJobAttribute : Attribute;

/// <summary>
/// SignalR hub filter that enforces agent authorization on all hub method invocations.
/// <list type="bullet">
///   <item>All methods except <c>RegisterAgent</c> require the caller to be a registered agent
///         (ConnectionId → agentId lookup in the registry).</item>
///   <item>Methods decorated with <see cref="RequiresActiveJobAttribute"/> additionally validate
///         that the <c>jobId</c> (first parameter) matches the agent's <c>ActiveJobId</c>.</item>
///   <item>Operator-authenticated callers (the Blazor UI circuit — master key, no <c>agentId</c>
///         query parameter) are not agents and never call <c>RegisterAgent</c>. They are allowed
///         to invoke the UI subscription methods only.</item>
/// </list>
/// Mismatched calls throw <see cref="HubException"/> and are logged.
///
/// Must be installed via <c>HubOptions.AddFilter&lt;AgentAuthorizationFilter&gt;()</c> —
/// registering it in DI as <see cref="IHubFilter"/> alone does NOT activate it.
/// </summary>
public sealed class AgentAuthorizationFilter : IHubFilter
{
    /// <summary>
    /// Hub methods an operator-authenticated (non-agent) connection may invoke.
    /// The Blazor UI subscribes to <c>run-{jobId}</c> groups through these.
    /// </summary>
    private static readonly HashSet<string> OperatorAllowedMethods = new(StringComparer.Ordinal)
    {
        nameof(AgentHub.SubscribeToRun),
        nameof(AgentHub.UnsubscribeFromRun)
    };

    private readonly IAgentRegistryService _registry;
    private readonly ILogger _logger;

    public AgentAuthorizationFilter(IAgentRegistryService registry, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(logger);
        _registry = registry;
        _logger = logger;
    }

    public async ValueTask<object?> InvokeMethodAsync(
        HubInvocationContext invocationContext,
        Func<HubInvocationContext, ValueTask<object?>> next)
    {
        // Only apply authorization to AgentHub — skip Blazor's internal ComponentHub and other hubs
        if (invocationContext.Hub is not AgentHub)
        {
            return await next(invocationContext);
        }

        if (IsOperatorConnection(invocationContext))
            GuardOperatorMethod(invocationContext);
        else
            GuardAgentMethod(invocationContext);

        return await next(invocationContext);
    }

    /// <summary>
    /// True when the caller authenticated with the master key and no <c>agentId</c> query
    /// parameter — <c>AgentApiKeyAuthHandler</c> stamps <c>auth_kind=operator</c> for that case.
    /// In practice this is the Blazor UI circuit.
    /// </summary>
    private static bool IsOperatorConnection(HubInvocationContext ctx) =>
        string.Equals(
            ctx.Context.User?.FindFirst("auth_kind")?.Value,
            "operator",
            StringComparison.Ordinal);

    /// <summary>
    /// Operator connections are not agents and never call <c>RegisterAgent</c>. They may join and
    /// leave run groups so the UI can stream output; everything else on this hub is agent-facing.
    /// </summary>
    private void GuardOperatorMethod(HubInvocationContext ctx)
    {
        if (OperatorAllowedMethods.Contains(ctx.HubMethodName))
            return;

        _logger.Warning(
            "Hub method {Method} rejected — operator connection {ConnectionId} may only invoke UI subscription methods",
            ctx.HubMethodName, ctx.Context.ConnectionId);
        throw new HubException($"Method {ctx.HubMethodName} is not available to operator connections");
    }

    /// <summary>
    /// Requires the caller to be a registered agent, and for <see cref="RequiresActiveJobAttribute"/>
    /// methods, to own the job it is addressing. <c>RegisterAgent</c> is exempt — it is how a
    /// connection becomes a registered agent in the first place.
    /// </summary>
    private void GuardAgentMethod(HubInvocationContext ctx)
    {
        if (string.Equals(ctx.HubMethodName, nameof(AgentHub.RegisterAgent), StringComparison.Ordinal))
            return;

        var agent = _registry.GetByConnectionId(ctx.Context.ConnectionId);
        if (agent is null)
        {
            _logger.Warning(
                "Hub method {Method} rejected — connection {ConnectionId} is not a registered agent",
                ctx.HubMethodName, ctx.Context.ConnectionId);
            throw new HubException($"Agent not registered (connection {ctx.Context.ConnectionId})");
        }

        if (ctx.HubMethod.GetCustomAttribute<RequiresActiveJobAttribute>() is not null)
            GuardActiveJob(ctx, agent);
    }

    /// <summary>
    /// Validates the <c>jobId</c> first parameter against the agent's <c>ActiveJobId</c>.
    /// The first-parameter convention is documented on <see cref="RequiresActiveJobAttribute"/>.
    /// </summary>
    private void GuardActiveJob(HubInvocationContext ctx, AgentEntry agent)
    {
        if (ctx.HubMethodArguments.Count == 0 || ctx.HubMethodArguments[0] is not JobId jobId)
        {
            _logger.Warning(
                "Hub method {Method} rejected — missing or invalid jobId parameter from agent {AgentId}",
                ctx.HubMethodName, agent.AgentId);
            throw new HubException($"Method {ctx.HubMethodName} requires a jobId as the first parameter");
        }

        if (!string.Equals(agent.ActiveJobId, jobId.Value, StringComparison.Ordinal))
        {
            _logger.Warning(
                "Hub method {Method} rejected — job {JobId} not assigned to agent {AgentId} (active job: {ActiveJobId})",
                ctx.HubMethodName, jobId.Value, agent.AgentId, agent.ActiveJobId ?? "none");
            throw new HubException($"Job {jobId.Value} is not assigned to agent {agent.AgentId}");
        }
    }
}
