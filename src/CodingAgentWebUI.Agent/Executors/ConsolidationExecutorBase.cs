using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Agent.Executors;

/// <summary>
/// Abstract base class for consolidation executors. Provides shared helper methods
/// for job ID validation, workspace path resolution, and result construction.
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

    protected static ConsolidationJobResult CreateFailureResult(string jobId, string errorMessage)
        => new() { JobId = jobId, Success = false, ErrorMessage = errorMessage };

    protected static ConsolidationJobResult CreateCancelledResult(string jobId)
        => new() { JobId = jobId, Success = false, ErrorMessage = "Consolidation run was cancelled" };
}
