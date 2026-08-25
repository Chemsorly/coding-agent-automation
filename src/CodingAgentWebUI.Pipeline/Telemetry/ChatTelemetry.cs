using System.Diagnostics.Metrics;

namespace CodingAgentWebUI.Pipeline.Telemetry;

/// <summary>
/// Chat-specific metrics using the shared WorkDistribution meter.
/// All instruments tagged with <c>agent_selector</c> (underscore-encoded selector).
/// </summary>
/// <remarks>
/// Reuses <see cref="WorkDistributionTelemetry.Meter"/> so all chat metrics appear under
/// <c>CodingAgent.WorkDistribution</c> — no OTel config changes required.
/// </remarks>
[System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverage(Justification = "OTel metric registration — no unit-testable logic.")]
public static class ChatTelemetry
{
    private static readonly Meter Meter = WorkDistributionTelemetry.Meter;

    /// <summary>Time from <c>DispatchChatPodAsync</c> call to agent connection.</summary>
    public static readonly Histogram<double> DispatchLatency =
        Meter.CreateHistogram<double>("workdistribution.chat.dispatch_latency_seconds", "s",
            "Time from DispatchChatPodAsync call to agent connection");

    /// <summary>Current active chat sessions. +1 in RegisterSession, -1 on terminal/force-delete.</summary>
    public static readonly UpDownCounter<long> SessionsActive =
        Meter.CreateUpDownCounter<long>("workdistribution.chat.sessions_active", "{session}",
            "Currently active chat sessions");

    /// <summary>Chat session lifetime from connect to terminal.</summary>
    public static readonly Histogram<double> SessionDuration =
        Meter.CreateHistogram<double>("workdistribution.chat.session_duration_seconds", "s",
            "Chat session lifetime from connect to terminal");

    /// <summary>Incremented when a chat pod fails to connect within the timeout.</summary>
    public static readonly Counter<long> PodConnectTimeouts =
        Meter.CreateCounter<long>("workdistribution.chat.pod_connect_timeouts", "{timeout}",
            "Chat pod connect timeout count");

    /// <summary>Incremented when TerminateChatSessionAsync force-deletes a job.</summary>
    public static readonly Counter<long> PodForceTerminations =
        Meter.CreateCounter<long>("workdistribution.chat.pod_force_terminations", "{termination}",
            "Chat pod force termination count");

    /// <summary>Active PVC claims for chat sessions. +1 after TryClaimAsync, -1 on release.</summary>
    public static readonly UpDownCounter<long> PvcUtilization =
        Meter.CreateUpDownCounter<long>("workdistribution.chat.pvc_utilization", "{pvc}",
            "Active PVC claims for chat sessions");
}
