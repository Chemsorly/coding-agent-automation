using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent.Executors;

/// <summary>
/// Abstract base class for consolidation executors. Provides shared helper methods
/// for job ID validation, workspace path resolution, result construction,
/// agent execution with exit-code checking, and cancellation/error handling.
/// </summary>
public abstract class ConsolidationExecutorBase
{
    protected Serilog.ILogger Logger { get; }
    protected abstract string WorkspaceSuffix { get; }
    protected abstract string ExecutorName { get; }

    protected ConsolidationExecutorBase(Serilog.ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        Logger = logger;
    }

    protected ConsolidationJobResult? ValidateJobId(ConsolidationJobMessage job)
    {
        if (!Guid.TryParse(job.JobId, out _))
        {
            Logger.Warning("Invalid JobId format: {JobId}", job.JobId);
            return new ConsolidationJobResult
            {
                JobId = job.JobId,
                Success = false,
                ErrorMessage = "Invalid JobId format"
            };
        }
        return null;
    }

    protected string ResolveWorkspacePath(ConsolidationJobMessage job)
    {
        return job.WorkspacePath is not null
            ? Path.Combine(job.WorkspacePath, WorkspaceSuffix)
            : Path.Combine(Path.GetTempPath(), "consolidation", job.JobId, WorkspaceSuffix);
    }

    /// <summary>
    /// Executes an agent request and checks the result for success. If the agent exits
    /// with a non-zero code, logs a warning and returns a failure result.
    /// </summary>
    protected async Task<(AgentResult Result, ConsolidationJobResult? Failure)> ExecuteAgentAndCheckAsync(
        IAgentProvider agentProvider,
        AgentRequest request,
        string jobId,
        CancellationToken ct,
        Action<string>? onOutputLine = null)
    {
        var agentResult = await agentProvider.ExecuteAsync(request, ct, onOutputLine);

        if (!agentResult.Success)
        {
            Logger.Warning("{ExecutorName} agent exited with code {ExitCode} for run {RunId}",
                ExecutorName, agentResult.ExitCode, jobId);
            return (agentResult, CreateFailureResult(jobId, $"Agent exited with code {agentResult.ExitCode}"));
        }

        return (agentResult, null);
    }

    /// <summary>
    /// Wraps an async action with the standard cancellation and error handling pattern.
    /// Returns <see cref="CreateCancelledResult"/> on <see cref="OperationCanceledException"/>
    /// when the token is cancelled, or <see cref="CreateFailureResult"/> on any other exception.
    /// </summary>
    protected async Task<ConsolidationJobResult> WrapWithCancellationHandlingAsync(
        string jobId,
        Func<Task<ConsolidationJobResult>> action,
        CancellationToken ct)
    {
        try
        {
            return await action();
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return CreateCancelledResult(jobId);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "{ExecutorName} run {RunId} failed: {Message}", ExecutorName, jobId, ex.Message);
            return CreateFailureResult(jobId, ex.Message);
        }
    }

    protected static ConsolidationJobResult CreateFailureResult(string jobId, string errorMessage)
        => new() { JobId = jobId, Success = false, ErrorMessage = errorMessage };

    protected static ConsolidationJobResult CreateCancelledResult(string jobId)
        => new() { JobId = jobId, Success = false, ErrorMessage = "Consolidation run was cancelled" };
}
