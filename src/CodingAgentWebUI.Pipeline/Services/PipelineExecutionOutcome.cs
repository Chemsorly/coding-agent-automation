using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Discriminated result type representing the outcome of pipeline step execution.
/// Callers pattern-match to decide how to handle each case (return payload, transition state, rethrow, etc.).
/// </summary>
public abstract record PipelineExecutionOutcome(PipelineRun Run)
{
    /// <summary>All steps completed without exception.</summary>
    public sealed record CompletedOutcome(PipelineRun Run) : PipelineExecutionOutcome(Run);

    /// <summary>An <see cref="OperationCanceledException"/> was thrown during step execution.</summary>
    public sealed record CancelledOutcome(PipelineRun Run) : PipelineExecutionOutcome(Run);

    /// <summary>An unhandled exception was thrown during step execution.</summary>
    public sealed record FailedOutcome(PipelineRun Run, Exception Exception) : PipelineExecutionOutcome(Run);

    public static PipelineExecutionOutcome Completed(PipelineRun run) => new CompletedOutcome(run);
    public static PipelineExecutionOutcome Cancelled(PipelineRun run) => new CancelledOutcome(run);
    public static PipelineExecutionOutcome Failed(PipelineRun run, Exception ex) => new FailedOutcome(run, ex);
}
