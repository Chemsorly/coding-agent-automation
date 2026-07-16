using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Factory for creating <see cref="PipelineSignalRReporter"/> instances.
/// Allows <see cref="LocalPipelineExecutor"/> to compose with the reporter via dependency injection
/// rather than direct instantiation, enabling isolated testing of orchestration logic.
/// </summary>
public interface IPipelineReporterFactory
{
    /// <summary>
    /// Creates a new <see cref="PipelineSignalRReporter"/> for the given job execution context.
    /// </summary>
    /// <param name="connection">The SignalR hub connection for progress reporting.</param>
    /// <param name="outputBatcher">Batcher for streaming output lines.</param>
    /// <param name="jobId">The job identifier.</param>
    /// <param name="run">The pipeline run state.</param>
    /// <param name="onStepChanged">Optional callback for step change notifications.</param>
    // TODO: Return an IPipelineReporter interface instead of the concrete PipelineSignalRReporter to improve testability — tests that mock this factory currently cannot substitute a fake reporter without constructing the real sealed class.
    PipelineSignalRReporter Create(
        HubConnection connection,
        OutputBatcher outputBatcher,
        string jobId,
        PipelineRun run,
        Action<PipelineStep?>? onStepChanged);
}
