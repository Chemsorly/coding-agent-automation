using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR.Client;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Shared execution helper for pipeline job execution with error handling.
/// Encapsulates the common try/catch pattern used by both <see cref="AgentWorkerService"/> (SignalR mode)
/// and <see cref="WorkItemAgentService"/> (K8s mode).
/// </summary>
/// <remarks>
/// Handles:
/// <list type="bullet">
///   <item>Successful completion — returns executor's result directly</item>
///   <item><see cref="OperationCanceledException"/> — returns Cancelled payload</item>
///   <item>General exceptions — returns Failed payload with exception message</item>
/// </list>
/// Callers retain control of lifecycle (slot management, completion reporting, exit codes).
/// </remarks>
public static class AgentJobRunner
{
    /// <summary>
    /// Executes a pipeline job via the provided executor, handling cancellation and errors uniformly.
    /// Always returns a <see cref="JobCompletionPayload"/> — never throws (except for <see cref="OperationCanceledException"/>
    /// when <paramref name="rethrowOnSigterm"/> is cancelled, allowing the caller's SIGTERM handler to run).
    /// </summary>
    /// <param name="executor">The pipeline executor to invoke.</param>
    /// <param name="assignment">The job assignment with full context.</param>
    /// <param name="connection">Hub connection for progress reporting (may be null for testing).</param>
    /// <param name="onStepChanged">Callback for step transition notifications.</param>
    /// <param name="ct">Cancellation token. When cancelled, returns a Cancelled payload.</param>
    /// <param name="rethrowOnSigterm">
    /// Optional secondary cancellation token representing SIGTERM. If this token is cancelled
    /// when an <see cref="OperationCanceledException"/> occurs, the exception is rethrown
    /// to allow the caller's SIGTERM handler to manage shutdown.
    /// </param>
    /// <returns>A <see cref="JobCompletionPayload"/> with the final status.</returns>
    public static async Task<JobCompletionPayload> ExecuteAsync(
        IPipelineExecutor executor,
        JobAssignmentMessage assignment,
        HubConnection connection,
        Action<PipelineStep?> onStepChanged,
        CancellationToken ct,
        CancellationToken rethrowOnSigterm = default)
    {
        await using var outputBatcher = new OutputBatcher();

        try
        {
            return await executor.ExecuteAsync(assignment, connection, outputBatcher, onStepChanged, ct);
        }
        catch (OperationCanceledException) when (rethrowOnSigterm != default && rethrowOnSigterm.IsCancellationRequested)
        {
            // SIGTERM path — rethrow to let the caller's SIGTERM handler manage shutdown
            throw;
        }
        catch (OperationCanceledException)
        {
            return new JobCompletionPayload
            {
                FinalStep = PipelineStep.Cancelled,
                CompletedAt = DateTimeOffset.UtcNow,
                IsRework = assignment.LinkedPullRequest is not null
            };
        }
        catch (Exception ex)
        {
            return new JobCompletionPayload
            {
                FinalStep = PipelineStep.Failed,
                FailureReason = ex.Message,
                CompletedAt = DateTimeOffset.UtcNow,
                IsRework = assignment.LinkedPullRequest is not null
            };
        }
    }
}
