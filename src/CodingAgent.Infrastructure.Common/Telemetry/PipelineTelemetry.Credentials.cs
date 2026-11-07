using System.Diagnostics.Metrics;

namespace CodingAgent.Pipeline.Telemetry;

public static partial class PipelineTelemetry
{
    // ── Agent credential pauses (spec 056) ───────────────────────────────────────

    /// <summary>
    /// Counter: agent credentials that changed from available to paused (spec 056 req 8.1), recorded by the API's
    /// pause store. Tags: <c>credential</c>, <c>reason</c> (<c>quota_exhausted</c>, <c>auth_failed</c>, <c>manual</c>),
    /// <c>source</c> (<c>auto</c>, <c>manual</c>).
    /// </summary>
    public static readonly Counter<long> AgentCredentialPauses;

    /// <summary>
    /// Counter: paused agent credentials that became available. Tags: <c>credential</c>, <c>source</c> (<c>manual</c> for
    /// a resume, <c>reset</c> when the pause's end time passed).
    /// </summary>
    public static readonly Counter<long> AgentCredentialResumes;

    // Callback registered once by the Scheduler's WorkItemCountsService; null = the gauge emits nothing.
    private static Func<IEnumerable<Measurement<long>>>? _agentCredentialPausedCallback;

    /// <summary>
    /// Registers the callback of <c>pipeline.agent_credential.paused</c> (1 paused, 0 available, tag <c>credential</c>).
    /// Called once by the Scheduler's <c>WorkItemCountsService</c>; the first registration wins.
    /// </summary>
    public static void RegisterAgentCredentialCallbacks(Func<IEnumerable<Measurement<long>>> pausedCallback)
    {
        ArgumentNullException.ThrowIfNull(pausedCallback);
        if (Interlocked.CompareExchange(ref _agentCredentialPausedCallback, pausedCallback, null) is not null)
        {
            Serilog.Log.Warning(
                "PipelineTelemetry: RegisterAgentCredentialCallbacks called more than once — the first callback stays active.");
        }
    }

    /// <summary>Records a credential that changed from available to paused.</summary>
    public static void RecordAgentCredentialPause(string credential, string reason, string source) =>
        AgentCredentialPauses.Add(1,
            new KeyValuePair<string, object?>("credential", credential),
            new KeyValuePair<string, object?>("reason", reason),
            new KeyValuePair<string, object?>("source", source));

    /// <summary>Records a paused credential that became available; <paramref name="source"/> is <c>manual</c> or <c>reset</c>.</summary>
    public static void RecordAgentCredentialResume(string credential, string source) =>
        AgentCredentialResumes.Add(1,
            new KeyValuePair<string, object?>("credential", credential),
            new KeyValuePair<string, object?>("source", source));

    /// <summary>Span attributes of spec 056 (design "Telemetry").</summary>
    public static class CredentialAttributes
    {
        public const string Credential = "pipeline.agent_credential";
        public const string HoldReason = "pipeline.hold_reason";
        public const string HoldText = "pipeline.hold_text";
        public const string ProviderFailure = "pipeline.provider_failure";
    }
}
