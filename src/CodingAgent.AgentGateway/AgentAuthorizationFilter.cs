using System.Reflection;
using CodingAgent.Infrastructure.Common;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Orchestration.Health;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Telemetry;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using ILogger = Serilog.ILogger;

namespace CodingAgent.AgentGateway;

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
///
/// Must be installed via <c>HubOptions.AddFilter&lt;AgentAuthorizationFilter&gt;()</c> —
/// registering it in DI as <see cref="IHubFilter"/> alone does NOT activate it.
/// </summary>
public sealed class AgentAuthorizationFilter : IHubFilter
{
    private const string ReasonTag = "reason";

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
        // Only AgentHub — UiHub checks its callers in its own methods
        if (invocationContext.Hub is not AgentHub)
        {
            return await next(invocationContext);
        }

        var shouldInvoke = GuardAgentMethod(invocationContext);

        // Short-circuit: skip hub method invocation when GuardAgentMethod signals idle-completion
        // (e.g. ReportJobCompleted from an agent that already completed via HTTP, issue #2956).
        if (!shouldInvoke)
            return default;

        return await next(invocationContext);
    }

    /// <summary>
    /// Requires the caller to be a registered agent, and for <see cref="RequiresActiveJobAttribute"/>
    /// methods, to own the job it is addressing. <c>RegisterAgent</c> is exempt — it is how a
    /// connection becomes a registered agent in the first place.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the hub method should be invoked; <c>false</c> if the call should be
    /// silently short-circuited (e.g. <c>ReportJobCompleted</c> with no active job after HTTP completion,
    /// or <c>DeregisterAgent</c> from a connection that is not registered).
    /// Throws <see cref="HubException"/> for all genuine authorization failures.
    /// </returns>
    private bool GuardAgentMethod(HubInvocationContext ctx)
    {
        if (string.Equals(ctx.HubMethodName, nameof(AgentHub.RegisterAgent), StringComparison.Ordinal))
            return true;

        var result = TryResolveAgent(ctx);

        if (result.Agent is null)
        {
            // An agent leaving while its connection is not registered (e.g. its session ended in the
            // reconnect window after an API restart) has nothing to deregister. Skip the call instead of
            // throwing, which SignalR would log as an Error. Nothing runs, so the guard is not weakened.
            if (string.Equals(ctx.HubMethodName, nameof(AgentHub.DeregisterAgent), StringComparison.Ordinal))
            {
                _logger.Debug(
                    "DeregisterAgent from connection {ConnectionId} ignored — the connection is not a registered agent",
                    ctx.Context.ConnectionId);
                return false;
            }

            // Classify: if the request carries an agentId query param, the agent is likely in the
            // reconnect-race window (connected but RegisterAgent not yet complete). Demote to Debug
            // and tag the counter accordingly. Without the query param we treat it as a true
            // unregistered connection and keep the Warning log level.
            // TODO [WARNING]: The reconnect-race heuristic (isReconnectRace = !string.IsNullOrEmpty(queryAgentId))
            // is based solely on a user-controlled HTTP query parameter. Any caller with a valid API key
            // that includes ?agentId=anything will have their rejection silently demoted to Debug and
            // tagged as reconnect_race — even if they are a genuinely unregistered or malicious connection.
            // This over-demotes some true auth failures and inflates the reconnect_race metric. A more
            // precise classification would require a Redis lookup confirming the agentId is a known agent
            // (even if not yet registered on this connection). Acknowledged as an accepted trade-off in
            // the issue spec. (Correctness Review / Security Review)
            var rejectionReason = result.IsReconnectRace
                ? PipelineTelemetry.HubAuthRejectionReasons.ReconnectRace
                : PipelineTelemetry.HubAuthRejectionReasons.NotRegistered;

            PipelineTelemetry.HubAuthRejections.Add(1,
                new KeyValuePair<string, object?>(ReasonTag, rejectionReason));

            if (result.IsReconnectRace)
                _logger.Debug(
                    "Hub method {Method} rejected — connection {ConnectionId} is reconnecting (agentId={AgentId}, likely reconnect-race window)",
                    ctx.HubMethodName, ctx.Context.ConnectionId, LogSanitizer.SanitizeForLog(result.QueryAgentId));
            else
                _logger.Warning(
                    "Hub method {Method} rejected — connection {ConnectionId} is not a registered agent",
                    ctx.HubMethodName, ctx.Context.ConnectionId);

            throw new HubException($"Agent not registered (connection {ctx.Context.ConnectionId})");
        }

        if (ctx.HubMethod.GetCustomAttribute<RequiresActiveJobAttribute>() is not null)
            return GuardActiveJob(ctx, result.Agent);

        return true;
    }

    /// <summary>
    /// Resolves the calling agent from the registry, with a Redis fallback for cold-pod scenarios.
    /// </summary>
    /// <remarks>
    /// Primary path: looks up by <c>ConnectionId</c> in the node-local <c>_connectionIndex</c>.
    /// Fallback: when the primary misses (e.g. after an API restart), reads the <c>agentId</c>
    /// query parameter and checks Redis directly via <see cref="IAgentRegistryService.GetByAgentId"/>.
    ///
    /// SECURITY: the Redis entry is accepted only if its stored <c>ConnectionId</c> already matches
    /// the current connection — meaning <c>Register()</c> has completed on some replica and written
    /// the new <c>connectionId</c>. If Redis still holds a previous connection's ID the entry is
    /// rejected; the Polly retry pipeline on the agent side covers that residual window.
    /// </remarks>
    private AgentLookupResult TryResolveAgent(HubInvocationContext ctx)
    {
        var agent = _registry.GetByConnectionId(ctx.Context.ConnectionId);
        if (agent is not null)
            return new AgentLookupResult(agent, IsReconnectRace: false, QueryAgentId: null);

        // Fallback: _connectionIndex is node-local and empty on a cold pod (after API restart).
        // ctx.Context.Features is guarded explicitly because test infrastructure (Moq loose mocks)
        // may return null for the Features property; GetHttpContext() dereferences Features directly
        // and would throw NullReferenceException rather than returning a safe null in that case.
        var queryAgentId = (string?)null;
        if (ctx.Context.Features is not null)
        {
            queryAgentId = ctx.Context.GetHttpContext()?.Request.Query["agentId"].FirstOrDefault();
            if (!string.IsNullOrEmpty(queryAgentId))
            {
                if (queryAgentId.Length > 128 || !IsValidAgentIdFormat(queryAgentId))
                    return new AgentLookupResult(null, IsReconnectRace: false, QueryAgentId: null);

                var candidate = _registry.GetByAgentId(new AgentId(queryAgentId));
                if (candidate?.ConnectionId == ctx.Context.ConnectionId)
                    return new AgentLookupResult(candidate, IsReconnectRace: false, QueryAgentId: null);
            }
        }

        return new AgentLookupResult(null, IsReconnectRace: !string.IsNullOrEmpty(queryAgentId), QueryAgentId: queryAgentId);
    }

    /// <summary>Result of <see cref="TryResolveAgent"/>.</summary>
    /// <param name="Agent">The resolved agent, or <c>null</c> if not found or security-rejected.</param>
    /// <param name="IsReconnectRace">
    /// <c>true</c> when the <c>agentId</c> query parameter was present (indicating the agent is
    /// likely in the reconnect-race window). Used by the caller to classify the rejection reason
    /// and choose the appropriate log level.
    /// </param>
    /// <param name="QueryAgentId">
    /// The raw (unsanitized) <c>agentId</c> query parameter value, populated only when
    /// <see cref="IsReconnectRace"/> is <c>true</c>. Passed to <see cref="LogSanitizer"/> by the
    /// caller before writing to logs.
    /// </param>
    private sealed record AgentLookupResult(AgentEntry? Agent, bool IsReconnectRace, string? QueryAgentId);

    /// <summary>
    /// Returns <c>true</c> if <paramref name="value"/> consists entirely of ASCII letters,
    /// digits, and hyphens — the allowed character set for agent IDs.
    /// </summary>
    private static bool IsValidAgentIdFormat(string value)
    {
        foreach (var c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
                return false;
        }
        return true;
    }

    /// <summary>
    /// Validates the <c>jobId</c> first parameter against the agent's <c>ActiveJobId</c>.
    /// The first-parameter convention is documented on <see cref="RequiresActiveJobAttribute"/>.
    /// </summary>
    /// <returns>
    /// <c>true</c> if the hub method should be invoked; <c>false</c> if the call should be
    /// silently short-circuited (only for <c>ReportJobCompleted</c> with no active job).
    /// Throws <see cref="HubException"/> for all genuine mismatches.
    /// </returns>
    private bool GuardActiveJob(HubInvocationContext ctx, AgentEntry agent)
    {
        // Short-circuit: ReportJobCompleted from an agent that already completed via HTTP.
        // The HTTP primary path sets agent Idle (ActiveJobId = null) before the SignalR
        // secondary message arrives. Throwing here would log Warning + SignalR Error and trigger
        // 3 client retries — all wasteful, since the result was already recorded (issue #2956).
        // Capture ActiveJobId into a local to guard against concurrent modification.
        // TODO: [WARNING] Capturing agent.ActiveJobId into a local is a snapshot, not an atomic
        // read. A concurrent force-idle or disconnect handler clearing ActiveJobId on a genuinely
        // active agent could cause a legitimate ReportJobCompleted to be silently short-circuited.
        // This is the same pre-existing non-atomic race acknowledged elsewhere in this file
        // (_localSnapshot non-atomic writes); no new race is introduced here. The window is
        // extremely tight and the risk is accepted by the existing codebase design.
        var activeJobId = agent.ActiveJobId;
        if (string.Equals(ctx.HubMethodName, nameof(AgentHub.ReportJobCompleted), StringComparison.Ordinal)
            && activeJobId is null)
        {
            _logger.Debug(
                "GuardActiveJob: {Method} from agent {AgentId} ignored — agent already Idle (HTTP completion already recorded)",
                ctx.HubMethodName, agent.AgentId);
            return false; // do NOT invoke hub method, do NOT log Warning/Error
        }

        if (ctx.HubMethodArguments.Count == 0 || ctx.HubMethodArguments[0] is not JobId jobId)
        {
            PipelineTelemetry.HubAuthRejections.Add(1,
                new KeyValuePair<string, object?>(ReasonTag, PipelineTelemetry.HubAuthRejectionReasons.JobMismatch));
            _logger.Warning(
                "Hub method {Method} rejected — missing or invalid jobId parameter from agent {AgentId}",
                ctx.HubMethodName, agent.AgentId);
            throw new HubException($"Method {ctx.HubMethodName} requires a jobId as the first parameter");
        }

        if (!string.Equals(activeJobId, jobId.Value, StringComparison.Ordinal))
        {
            PipelineTelemetry.HubAuthRejections.Add(1,
                new KeyValuePair<string, object?>(ReasonTag, PipelineTelemetry.HubAuthRejectionReasons.JobMismatch));
            _logger.Warning(
                "Hub method {Method} rejected — job {JobId} not assigned to agent {AgentId} (active job: {ActiveJobId})",
                ctx.HubMethodName, LogSanitizer.SanitizeForLog(jobId.Value), agent.AgentId, activeJobId ?? "none");
            throw new HubException($"Job {LogSanitizer.SanitizeForLog(jobId.Value)} is not assigned to agent {agent.AgentId}");
        }

        return true;
    }
}
