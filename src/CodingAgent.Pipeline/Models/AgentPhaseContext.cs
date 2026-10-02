using CodingAgent.Pipeline.Interfaces;

namespace CodingAgent.Pipeline.Models;

/// <summary>
/// Bundles the common parameters needed by all agent phase execution methods.
/// Replaces the 10-13 positional parameters on <see cref="IAgentPhaseExecutor"/> methods.
/// </summary>
public sealed record AgentPhaseContext : PipelineContextBase
{
    /// <summary>The issue being worked on.</summary>
    public required IssueDetail Issue { get; init; }

    /// <summary>Parsed issue with structured requirements/acceptance criteria.</summary>
    public required ParsedIssue ParsedIssue { get; init; }

    /// <summary>Downloaded issue/PR images for native vision delivery to agents.</summary>
    public IReadOnlyList<DownloadedImage>? DownloadedImages { get; init; }

    /// <summary>
    /// Project secrets to inject into the child agent process via
    /// <see cref="AgentRequest.EnvironmentVariables"/>. Populated from
    /// <see cref="PipelineStepContext.InjectedSecrets"/> by
    /// <see cref="PipelineStepContext.BuildAgentPhaseContext"/>. Null when no secrets
    /// were configured for this pipeline run.
    /// </summary>
    public IReadOnlyDictionary<string, string>? InjectedSecrets { get; init; }

    /// <summary>
    /// Action to report pipeline run events (agent stalls) server-side (issue #2979).
    /// When non-null, stall_kill and process_death events from <see cref="Services.AgentStallMonitor"/>
    /// are forwarded to the API so <c>pipeline.run.agent_stalls</c> is recorded for all phases,
    /// not just the QGC-retry path.
    /// Null on orchestrator/test paths where no SignalR reporter is wired.
    /// </summary>
    public Action<PipelineRunEventReport>? ReportPipelineRunEvent { get; init; }
}
