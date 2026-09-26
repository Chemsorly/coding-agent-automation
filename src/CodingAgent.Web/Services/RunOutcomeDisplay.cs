using CodingAgent.Pipeline.Models;

namespace CodingAgent.Web.Services;

/// <summary>
/// The outcome bucket a run falls into, derived from its <see cref="PipelineRunSummary.FinalStep"/>.
/// </summary>
public enum RunOutcome
{
    /// <summary>Not terminal yet — the run is still in flight.</summary>
    Running,

    /// <summary><see cref="PipelineStep.Completed"/> or <see cref="PipelineStep.PrMerged"/> (the work is done).</summary>
    Succeeded,

    /// <summary><see cref="PipelineStep.Failed"/>.</summary>
    Failed,

    /// <summary><see cref="PipelineStep.Cancelled"/> or <see cref="PipelineStep.PrClosed"/>.</summary>
    Cancelled,

    /// <summary>
    /// <see cref="PipelineStep.ConflictRestart"/> — superseded by a re-dispatched run for the same issue,
    /// so it is neither a success nor a failure.
    /// </summary>
    Restarted,
}

/// <summary>
/// Single source of truth for presenting a run's <see cref="PipelineRunSummary.FinalStep"/>: whether the
/// run is still live, its outcome bucket, its badge label and its badge CSS class.
/// Pages used to carry their own switches and disagreed about the terminal-like steps
/// (<see cref="PipelineStep.ConflictRestart"/>, <see cref="PipelineStep.PrMerged"/>,
/// <see cref="PipelineStep.PrClosed"/>): the Runs list and Overview showed finished runs as running, and
/// the Run page offered "Cancel Pipeline" on them.
/// </summary>
public static class RunOutcomeDisplay
{
    /// <summary>True while the run has not reached a terminal step (see <see cref="PipelineStepExtensions.IsTerminal"/>).</summary>
    public static bool IsActive(PipelineStep finalStep) => !finalStep.IsTerminal();

    public static RunOutcome Classify(PipelineStep finalStep) => finalStep switch
    {
        PipelineStep.Completed or PipelineStep.PrMerged => RunOutcome.Succeeded,
        PipelineStep.Failed => RunOutcome.Failed,
        PipelineStep.Cancelled or PipelineStep.PrClosed => RunOutcome.Cancelled,
        PipelineStep.ConflictRestart => RunOutcome.Restarted,
        _ => RunOutcome.Running,
    };

    public static string Label(PipelineStep finalStep) => finalStep switch
    {
        PipelineStep.Completed => "Completed",
        PipelineStep.Failed => "Failed",
        PipelineStep.Cancelled => "Cancelled",
        PipelineStep.ConflictRestart => "Restarted",
        PipelineStep.PrMerged => "Merged",
        PipelineStep.PrClosed => "Closed",
        _ => "Running",
    };

    public static string BadgeClass(PipelineStep finalStep) => Classify(finalStep) switch
    {
        RunOutcome.Succeeded => "step-completed",
        RunOutcome.Failed => "step-failed",
        RunOutcome.Cancelled => "step-cancelled",
        RunOutcome.Restarted => "step-restart",
        _ => "step-running",
    };

    /// <summary>
    /// Success rate in percent over runs that reached an outcome (succeeded, failed or cancelled).
    /// Running and restarted runs are excluded: a restart is carried on by the re-dispatched run.
    /// Returns null when no run in <paramref name="runs"/> reached an outcome.
    /// </summary>
    public static int? SuccessRate(IEnumerable<PipelineRunSummary> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);

        int succeeded = 0, decided = 0;
        foreach (var outcome in runs.Select(r => Classify(r.FinalStep)))
        {
            if (outcome is RunOutcome.Running or RunOutcome.Restarted)
                continue;
            decided++;
            if (outcome == RunOutcome.Succeeded)
                succeeded++;
        }

        return decided == 0 ? null : (int)Math.Round(100.0 * succeeded / decided);
    }
}
