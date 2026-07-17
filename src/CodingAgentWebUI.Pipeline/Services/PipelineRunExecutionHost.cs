using CodingAgentWebUI.Pipeline.Services.Steps;

namespace CodingAgentWebUI.Pipeline.Services;

/// <summary>
/// Encapsulates the execute-steps → handle-OCE → handle-generic-exception lifecycle
/// shared between <see cref="Steps.PipelineStepRunner"/> callers.
/// Returns a discriminated <see cref="PipelineExecutionOutcome"/> so each caller can
/// map the result to its specific behavior (return payload, transition state, rethrow, etc.).
/// </summary>
/// <remarks>
/// This class does NOT set telemetry tags or perform any side effects — it only routes
/// exceptions into the outcome type. Telemetry and state transitions remain the caller's
/// responsibility, preserving existing behavior exactly.
/// </remarks>
public static class PipelineRunExecutionHost
{
    /// <summary>
    /// Executes pipeline steps via <see cref="PipelineStepRunner.ExecuteAsync"/>,
    /// catching <see cref="OperationCanceledException"/> and generic exceptions into
    /// a discriminated <see cref="PipelineExecutionOutcome"/>.
    /// </summary>
    // TODO: Add ArgumentNullException.ThrowIfNull for 'steps' and 'context' parameters to match
    // the null-guard convention used across the codebase (see PipelineOrchestrationService, BrainSyncService, etc.).
    public static async Task<PipelineExecutionOutcome> ExecuteStepsAsync(
        IReadOnlyList<IPipelineStep> steps,
        PipelineStepContext context,
        CancellationToken ct)
    {
        try
        {
            await PipelineStepRunner.ExecuteAsync(steps, context, ct);
            return PipelineExecutionOutcome.Completed(context.Run);
        }
        catch (OperationCanceledException)
        {
            return PipelineExecutionOutcome.Cancelled(context.Run);
        }
        catch (Exception ex)
        {
            return PipelineExecutionOutcome.Failed(context.Run, ex);
        }
    }
}
