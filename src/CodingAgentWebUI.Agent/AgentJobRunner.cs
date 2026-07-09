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
    /// Delegate matching the ExecuteAsync signature shared by both <see cref="IPipelineExecutor"/>
    /// and <see cref="IWorkItemExecutor"/>.
    /// </summary>
    public delegate Task<JobCompletionPayload> PipelineExecuteDelegate(
        JobAssignmentMessage assignment,
        HubConnection connection,
        OutputBatcher outputBatcher,
        Action<PipelineStep?>? onStepChanged,
        CancellationToken ct);

    /// <summary>
    /// Executes a pipeline job via the provided executor delegate, handling cancellation and errors uniformly.
    /// Always returns a <see cref="JobCompletionPayload"/> — never throws (except for <see cref="OperationCanceledException"/>
    /// when <paramref name="rethrowOnSigterm"/> is cancelled, allowing the caller's SIGTERM handler to run).
    /// </summary>
    public static async Task<JobCompletionPayload> ExecuteAsync(
        PipelineExecuteDelegate execute,
        JobAssignmentMessage assignment,
        HubConnection connection,
        OutputBatcher outputBatcher,
        Action<PipelineStep?> onStepChanged,
        CancellationToken ct,
        CancellationToken rethrowOnSigterm = default,
        string? cancelledLabel = null)
    {
        try
        {
            return await execute(assignment, connection, outputBatcher, onStepChanged, ct);
        }
        catch (OperationCanceledException) when (rethrowOnSigterm != default && rethrowOnSigterm.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new JobCompletionPayload
            {
                FinalStep = PipelineStep.Cancelled,
                CompletedAt = DateTimeOffset.UtcNow,
                IsRework = assignment.LinkedPullRequest is not null,
                FinalLabel = cancelledLabel
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

    /// <summary>Convenience overload accepting <see cref="IPipelineExecutor"/> directly.</summary>
    public static Task<JobCompletionPayload> ExecuteAsync(
        IPipelineExecutor executor,
        JobAssignmentMessage assignment,
        HubConnection connection,
        OutputBatcher outputBatcher,
        Action<PipelineStep?> onStepChanged,
        CancellationToken ct,
        CancellationToken rethrowOnSigterm = default,
        string? cancelledLabel = null)
        => ExecuteAsync(executor.ExecuteAsync, assignment, connection, outputBatcher, onStepChanged, ct, rethrowOnSigterm, cancelledLabel);

    /// <summary>Convenience overload accepting <see cref="IWorkItemExecutor"/> directly.</summary>
    public static Task<JobCompletionPayload> ExecuteAsync(
        IWorkItemExecutor executor,
        JobAssignmentMessage assignment,
        HubConnection connection,
        OutputBatcher outputBatcher,
        Action<PipelineStep?> onStepChanged,
        CancellationToken ct,
        CancellationToken rethrowOnSigterm = default,
        string? cancelledLabel = null)
        => ExecuteAsync(executor.ExecuteAsync, assignment, connection, outputBatcher, onStepChanged, ct, rethrowOnSigterm, cancelledLabel);

    // ── Backward-compatible overload (used by tests without OutputBatcher) ──

    /// <summary>
    /// Simplified overload that creates an internal <see cref="OutputBatcher"/> (without OnFlush wiring).
    /// Useful for testing or when output streaming is not needed.
    /// </summary>
    public static async Task<JobCompletionPayload> ExecuteAsync(
        IPipelineExecutor executor,
        JobAssignmentMessage assignment,
        HubConnection connection,
        Action<PipelineStep?> onStepChanged,
        CancellationToken ct,
        CancellationToken rethrowOnSigterm = default)
    {
        await using var outputBatcher = new OutputBatcher();
        return await ExecuteAsync(executor, assignment, connection, outputBatcher, onStepChanged, ct, rethrowOnSigterm);
    }
}
