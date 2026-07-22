using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services.Steps;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Result of <see cref="PipelineExecutionContextBuilder.Build"/> containing all objects
/// constructed during the pipeline context assembly phase.
/// </summary>
internal sealed class PipelineExecutionBuildResult : IAsyncDisposable
{
    private bool _disposed;

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

    /// <summary>
    /// Disposes <see cref="LocalCts"/> and <see cref="Reporter"/>. Idempotent — subsequent
    /// calls are no-ops. Used on the <see cref="PipelineExecutionContextBuilder.Build"/> failure
    /// path to prevent resource leaks. The normal success path continues to use
    /// <see cref="PipelineCleanup.RunAsync"/> for disposal.
    /// </summary>
    // TODO: The _disposed guard prevents double-dispose within this class, but cannot prevent
    // external code from calling reporter.DisposeAsync() directly via PipelineCleanup.RunAsync.
    // PipelineSignalRReporter.DisposeAsync is not idempotent (see TODO at PipelineSignalRReporter.cs:304).
    // If a future refactor wraps buildResult in `await using` inside LocalPipelineExecutor's try block,
    // PipelineCleanup.RunAsync in the finally block would call reporter.DisposeAsync() a second time,
    // triggering ObjectDisposedException. Consider making PipelineSignalRReporter.DisposeAsync idempotent.
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        LocalCts.Dispose();
        await Reporter.DisposeAsync();
    }
}
