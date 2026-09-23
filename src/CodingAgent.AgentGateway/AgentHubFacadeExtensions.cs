using CodingAgent.Pipeline.Models;
using ILogger = Serilog.ILogger;

namespace CodingAgent.AgentGateway;

/// <summary>
/// Extension methods for <see cref="IAgentHubFacade"/> that encapsulate common fire-and-forget patterns.
/// </summary>
internal static class AgentHubFacadeExtensions
{
    private const string UpdateFieldFailedTemplate =
        "{CallerContext}: UpdateAgentFieldAsync failed for agent {AgentId} field '{Field}'";

    /// <summary>
    /// Fires and forgets an <see cref="IAgentHubFacade.UpdateAgentFieldAsync"/> call.
    /// If the write fails, logs a Warning with the exception, caller context, agent ID, and field name.
    /// The failure is never propagated — it is intentionally swallowed after logging.
    /// </summary>
    /// <param name="facade">The facade to call.</param>
    /// <param name="agentId">The agent whose field is being updated.</param>
    /// <param name="field">The field name (e.g. "activeJobId").</param>
    /// <param name="value">The new value (null clears the field).</param>
    /// <param name="logger">Logger for the fault continuation.</param>
    /// <param name="callerContext">Short context string included in the log message (e.g. "ResetAgentToIdle").</param>
    internal static void UpdateAgentFieldFireAndForget(
        this IAgentHubFacade facade,
        AgentId agentId,
        string field,
        string? value,
        ILogger logger,
        string callerContext)
    {
        // TODO [WARNING]: ArgumentNullException.ThrowIfNull guards are absent for facade, logger, and
        // callerContext. Null facade surfaces as NullReferenceException from UpdateAgentFieldAsync;
        // null logger surfaces inside the ContinueWith continuation. Method is internal and all current
        // call sites pass non-null arguments, but standard .NET practice is to guard even internal
        // methods. Add ThrowIfNull(facade), ThrowIfNull(logger), ThrowIfNull(callerContext) here.
        // TODO [WARNING]: The helper standardises on t.Exception?.Flatten() for all call sites. The old
        // HandleJobCompletedAsync blocks in AgentJobLifecycleService.cs used t.Exception (without
        // .Flatten()), so the logged exception shape has silently changed for those call sites. This is
        // likely an improvement (flattened AggregateException is easier to read), but alert-rule authors
        // or log parsers that relied on the non-flattened form may need updating.
        _ = facade.UpdateAgentFieldAsync(agentId, field, value)
            .ContinueWith(
                t => logger.Warning(
                    t.Exception?.Flatten(),
                    UpdateFieldFailedTemplate,
                    callerContext, agentId, field),
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted,
                TaskScheduler.Default);
    }
}
