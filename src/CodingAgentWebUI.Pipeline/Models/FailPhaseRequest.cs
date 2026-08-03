using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Groups the 7 parameters of <see cref="Services.AgentPhaseExecutor"/>'s internal
/// <c>FailPhaseAsync</c> helper into a single parameter object to satisfy S107.
/// </summary>
internal sealed record FailPhaseRequest(
    PipelineRun Run,
    string FailureReason,
    string Label,
    PipelineStep Step,
    IAgentIssueOperations IssueOps,
    IPipelineCallbacks Callbacks,
    CancellationToken Ct);
