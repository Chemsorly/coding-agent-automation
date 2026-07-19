using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Steps;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Result of <see cref="PipelineExecutionContextBuilder.Build"/> containing all objects
/// constructed during the pipeline context assembly phase.
/// </summary>
// TODO: PipelineExecutionBuildResult holds IDisposable (CancellationTokenSource) and IAsyncDisposable
// (PipelineSignalRReporter) but does not implement IDisposable/IAsyncDisposable. Ownership is implicit
// via PipelineCleanup.RunAsync. Consider implementing IAsyncDisposable to make the contract explicit.
internal sealed class PipelineExecutionBuildResult
{
    public required PipelineRun Run { get; init; }
    public required PipelineExecutionContext ExecutionContext { get; init; }
    public required PipelineSignalRReporter Reporter { get; init; }
    public required CancellationTokenSource LocalCts { get; init; }

    /// <summary>
    /// Emits an output line through the reporter, masking secrets from the step context.
    /// This delegate captures a mutable <see cref="PipelineStepContext"/> reference — callers
    /// must set <see cref="StepContext"/> after calling <c>CreateStepContext</c> so that the
    /// delegate masks secrets correctly.
    /// </summary>
    public required Action<string> EmitOutputLine { get; init; }

    /// <summary>
    /// The step context reference used by <see cref="EmitOutputLine"/> for secret masking.
    /// Set by the caller after <c>CreateStepContext</c> completes.
    /// </summary>
    public PipelineStepContext? StepContext { get; set; }
}
