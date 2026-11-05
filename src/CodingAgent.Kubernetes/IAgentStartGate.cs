using CodingAgent.Pipeline.Services;

namespace CodingAgent.Kubernetes;

/// <summary>
/// Spec 057 DD3, DD4: the one gate every agent start passes, work-item dispatch and chat launch alike. It serializes
/// the starts with one lock, so a count and the Job created after it cannot race another start.
/// </summary>
public interface IAgentStartGate
{
    /// <summary>
    /// Takes the <c>dispatch-agents</c> lock, lists the agent Jobs and reads the UI limits. Throws
    /// <see cref="TimeoutException"/> when the lock is not acquired and <see cref="AgentCountUnavailableException"/>
    /// when the Job list fails. On any exception after the lock is taken, the lock is released before the exception
    /// leaves. The caller disposes the lease after it created its Job (or decided not to).
    /// </summary>
    Task<IAgentStartLease> EnterAsync(CancellationToken ct);
}

/// <summary>Holds the start lock until disposed (idempotent) and answers the limit checks for this start.</summary>
public interface IAgentStartLease : IAsyncDisposable
{
    /// <summary>The agent Jobs counted when the lease was entered, and the credential PVCs they hold.</summary>
    AgentJobSnapshot Jobs { get; }

    /// <summary>Whether a pipeline agent may start on <paramref name="templateKey"/> (all four limits).</summary>
    AgentStartDecision CheckWorkItem(string templateKey);

    /// <summary>Whether a chat may start on <paramref name="templateKey"/> (Helm limits only).</summary>
    AgentStartDecision CheckChat(string templateKey);
}

/// <summary>The agent Jobs could not be listed, so no agent may start (spec 057 req 2.4).</summary>
public sealed class AgentCountUnavailableException(Exception inner)
    : Exception("Could not list the agent Jobs.", inner);

/// <summary>A chat launch was refused by a Helm limit (spec 057 req 4.1).</summary>
public sealed class AgentLimitReachedException(AgentStartDecision decision, string message) : Exception(message)
{
    /// <summary>The decision that refused the launch.</summary>
    public AgentStartDecision Decision { get; } = decision;
}

/// <summary>User-facing texts of spec 057.</summary>
public static class AgentStartTexts
{
    /// <summary>The chat refusal text of req 4.1.</summary>
    public static string ChatRefusal(AgentStartDecision decision, string templateKey)
    {
        ArgumentNullException.ThrowIfNull(decision);
        return decision.Scope == LimitScope.Global
            ? $"{decision.Running} of {decision.Limit} agents are running (limit set in the Helm chart). Try again when one finishes."
            : $"{decision.Running} of {decision.Limit} agents are running for {templateKey} (limit set in the Helm chart). Try again when one finishes.";
    }
}
