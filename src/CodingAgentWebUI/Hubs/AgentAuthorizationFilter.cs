using System.Reflection;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.SignalR;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Hubs;

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
/// </list>
/// Mismatched calls throw <see cref="HubException"/> and are logged.
/// </summary>
public sealed class AgentAuthorizationFilter : IHubFilter
{
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

        var connectionId = invocationContext.Context.ConnectionId;
        var methodName = invocationContext.HubMethodName;

        // RegisterAgent is the only method that doesn't require a registered agent
        if (!string.Equals(methodName, nameof(AgentHub.RegisterAgent), StringComparison.Ordinal))
        {
            var agent = _registry.GetByConnectionId(connectionId);
            if (agent is null)
            {
                _logger.Warning(
                    "Hub method {Method} rejected — connection {ConnectionId} is not a registered agent",
                    methodName, connectionId);
                throw new HubException($"Agent not registered (connection {connectionId})");
            }

            // Methods with [RequiresActiveJob] validate jobId (always first parameter)
            var requiresActiveJob = invocationContext.HubMethod.GetCustomAttribute<RequiresActiveJobAttribute>() is not null;
            if (requiresActiveJob)
            {
                if (invocationContext.HubMethodArguments.Count == 0 || invocationContext.HubMethodArguments[0] is not JobId jobId)
                {
                    _logger.Warning(
                        "Hub method {Method} rejected — missing or invalid jobId parameter from agent {AgentId}",
                        methodName, agent.AgentId);
                    throw new HubException($"Method {methodName} requires a jobId as the first parameter");
                }

                if (!string.Equals(agent.ActiveJobId, jobId.Value, StringComparison.Ordinal))
                {
                    _logger.Warning(
                        "Hub method {Method} rejected — job {JobId} not assigned to agent {AgentId} (active job: {ActiveJobId})",
                        methodName, jobId.Value, agent.AgentId, agent.ActiveJobId ?? "none");
                    throw new HubException($"Job {jobId.Value} is not assigned to agent {agent.AgentId}");
                }
            }
        }

        return await next(invocationContext);
    }
}
