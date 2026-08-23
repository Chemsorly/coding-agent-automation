using NGitLab;
using NGitLab.Models;
using Serilog;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Infrastructure.GitLab;

/// <summary>
/// GitLab CI/CD pipeline provider that reads pipeline status, waits for completion,
/// and retrieves job logs via the NGitLab client library.
/// Mirrors <see cref="GitHub.GitHubActionsPipelineProvider"/> for consistency.
/// </summary>
public class GitLabCiPipelineProvider : GitLabProviderBase, IPipelineProvider
{
    private readonly TimeSpan _pollInterval;
    private readonly ILogger _logger;

    /// <inheritdoc />
    public PipelineProviderType ProviderType => PipelineProviderType.GitLabCI;

    /// <summary>
    /// Creates a provider with a static access token.
    /// </summary>
    public GitLabCiPipelineProvider(
        string apiUrl,
        string accessToken,
        int projectId,
        TimeSpan pollInterval,
        ILogger? logger = null)
        : base(apiUrl, accessToken, projectId)
    {
        _pollInterval = pollInterval;
        _logger = logger ?? Log.Logger;
    }

    /// <summary>
    /// Creates a provider with a dynamic token provider delegate (for OrchestratorProxy token refresh).
    /// </summary>
    public GitLabCiPipelineProvider(
        string apiUrl,
        Func<CancellationToken, Task<string>> tokenProvider,
        int projectId,
        TimeSpan pollInterval,
        ILogger? logger = null)
        : base(apiUrl, tokenProvider, projectId)
    {
        _pollInterval = pollInterval;
        _logger = logger ?? Log.Logger;
    }

    /// <summary>
    /// Internal constructor for testing with a mock IGitLabClient.
    /// </summary>
    internal GitLabCiPipelineProvider(
        IGitLabClient client,
        int projectId,
        TimeSpan pollInterval,
        ILogger? logger = null)
        : base(client, projectId)
    {
        _pollInterval = pollInterval;
        _logger = logger ?? Log.Logger;
    }

    /// <inheritdoc />
    public async Task<PipelineRunStatus> GetRunStatusAsync(
        string branchName, string? commitSha, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(branchName);

        var query = new PipelineQuery
        {
            Ref = branchName,
            Sha = commitSha,
            OrderBy = PipelineOrderBy.id,
            Sort = PipelineSort.desc,
            PerPage = 1
        };

        var pipelines = await ExecuteWithResilienceAsync(
            client =>
            {
                var pipelineClient = client.GetPipelines(ProjectId);
                return pipelineClient.Search(query).Take(1).ToList();
            },
            "GetRunStatus.SearchPipelines", ct);

        if (pipelines.Count == 0)
        {
            _logger.Debug("No pipeline found for branch {Branch} (commit: {CommitSha})",
                branchName, commitSha ?? "any");

            return new PipelineRunStatus
            {
                State = PipelineRunState.Pending,
                Jobs = Array.Empty<PipelineJobResult>(),
                CommitSha = commitSha
            };
        }

        var pipeline = pipelines[0];
        var pipelineState = MapStatus(pipeline.Status);

        // Get jobs for this pipeline
        var jobs = await GetPipelineJobsAsync(pipeline.Id, ct);

        return new PipelineRunStatus
        {
            State = pipelineState,
            Jobs = jobs,
            Url = pipeline.WebUrl,
            StartedAt = pipeline.CreatedAt != default ? pipeline.CreatedAt : null,
            CompletedAt = IsTerminalState(pipelineState) ? pipeline.UpdatedAt : null,
            CommitSha = commitSha ?? pipeline.Sha.ToString().ToLowerInvariant()
        };
    }

    /// <inheritdoc />
    public async Task<PipelineRunStatus> WaitForCompletionAsync(
        string branchName, string? commitSha, TimeSpan timeout, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(branchName);

        _logger.Information("Polling GitLab CI for branch {Branch} (commit: {CommitSha}, timeout: {Timeout})",
            branchName, commitSha ?? "any", timeout);

        var pollCount = 0;
        PipelineRunStatus? lastStatus = null;

        return await TimeoutHelper.ExecuteWithTimeoutAsync(
            timeout, ct,
            async linkedCt =>
            {
                while (true)
                {
                    linkedCt.ThrowIfCancellationRequested();
                    pollCount++;

                    var status = await GetRunStatusAsync(branchName, commitSha, linkedCt);
                    lastStatus = status;

                    _logger.Information("GitLab CI poll #{PollCount}: {State} — {JobCount} job(s)",
                        pollCount, status.State, status.Jobs.Count);

                    if (IsTerminalState(status.State))
                    {
                        _logger.Information("GitLab CI completed: {State} after {PollCount} poll(s)",
                            status.State, pollCount);

                        if (status.State == PipelineRunState.Failed)
                        {
                            status = await EnrichFailedJobsWithLogsAsync(status, linkedCt);
                        }

                        return status;
                    }

                    await Task.Delay(_pollInterval, linkedCt);
                }
            },
            () =>
            {
                _logger.Warning("GitLab CI polling timed out after {Timeout} ({PollCount} polls). Last state: {State}",
                    timeout, pollCount, lastStatus?.State);
                return Task.FromResult(lastStatus ?? new PipelineRunStatus
                {
                    State = PipelineRunState.Pending,
                    Jobs = Array.Empty<PipelineJobResult>(),
                    CommitSha = commitSha
                });
            });
    }

    /// <inheritdoc />
    public async Task<string?> GetJobLogsAsync(long jobId, CancellationToken ct)
    {
        try
        {
            var trace = await ExecuteWithResilienceAsync(
                async client =>
                {
                    var jobClient = client.GetJobs(ProjectId);
                    return await jobClient.GetTraceAsync(jobId, ct);
                },
                "GetJobLogs", ct);

            return string.IsNullOrEmpty(trace) ? null : trace;
        }
        catch (Exception ex)
        {
            _logger.Warning(ex, "Failed to fetch logs for GitLab job (id={JobId})", jobId);
            return null;
        }
    }

    /// <summary>
    /// Fetches log content for each failed job and returns an enriched status.
    /// </summary>
    private async Task<PipelineRunStatus> EnrichFailedJobsWithLogsAsync(
        PipelineRunStatus status, CancellationToken ct)
    {
        var failedJobIds = status.Jobs
            .Where(j => j.State == PipelineRunState.Failed && j.JobId > 0)
            .Select(j => j.JobId)
            .ToHashSet();

        if (failedJobIds.Count == 0)
            return status;

        var logsByJobId = new Dictionary<long, string>();
        foreach (var jobId in failedJobIds)
        {
            var logContent = await GetJobLogsAsync(jobId, ct);
            if (logContent is not null)
            {
                logsByJobId[jobId] = logContent;
                _logger.Debug("Fetched {Length} chars of logs for failed GitLab job (id={JobId})",
                    logContent.Length, jobId);
            }
        }

        if (logsByJobId.Count == 0)
            return status;

        var enrichedJobs = status.Jobs.Select(job =>
        {
            if (logsByJobId.TryGetValue(job.JobId, out var content))
            {
                return new PipelineJobResult
                {
                    Name = job.Name,
                    State = job.State,
                    FailureReason = job.FailureReason,
                    LogUrl = job.LogUrl,
                    JobId = job.JobId,
                    LogContent = content
                };
            }
            return job;
        }).ToList();

        return new PipelineRunStatus
        {
            State = status.State,
            Jobs = enrichedJobs,
            Url = status.Url,
            StartedAt = status.StartedAt,
            CompletedAt = status.CompletedAt,
            CommitSha = status.CommitSha
        };
    }

    /// <summary>
    /// Retrieves all jobs for a given pipeline ID.
    /// </summary>
    private async Task<IReadOnlyList<PipelineJobResult>> GetPipelineJobsAsync(
        long pipelineId, CancellationToken ct)
    {
        var jobQuery = new PipelineJobQuery { PipelineId = pipelineId };

        var jobs = await ExecuteWithResilienceAsync(
            client =>
            {
                var pipelineClient = client.GetPipelines(ProjectId);
                return pipelineClient.GetJobs(jobQuery).ToList();
            },
            "GetRunStatus.GetJobs", ct);

        return jobs.Select(job => new PipelineJobResult
        {
            Name = job.Name,
            State = MapStatus(job.Status),
            FailureReason = job.Status == JobStatus.Failed
                ? job.FailureReason ?? $"Job '{job.Name}' failed"
                : null,
            LogUrl = job.WebUrl,
            JobId = job.Id
        }).ToList();
    }

    /// <summary>
    /// Maps a GitLab <see cref="JobStatus"/> to the internal <see cref="PipelineRunState"/>.
    /// </summary>
    internal static PipelineRunState MapStatus(JobStatus status)
    {
        return status switch
        {
            JobStatus.Pending => PipelineRunState.Pending,
            JobStatus.WaitingForResource => PipelineRunState.Pending,
            JobStatus.Preparing => PipelineRunState.Pending,
            JobStatus.Created => PipelineRunState.Pending,
            JobStatus.Manual => PipelineRunState.Pending,
            JobStatus.Scheduled => PipelineRunState.Pending,
            JobStatus.Running => PipelineRunState.Running,
            JobStatus.Success => PipelineRunState.Passed,
            JobStatus.Failed => PipelineRunState.Failed,
            JobStatus.Canceled => PipelineRunState.Cancelled,
            JobStatus.Canceling => PipelineRunState.Cancelled,
            // Skipped maps to Cancelled because PipelineRunState has no Skipped variant,
            // and skipped jobs don't block the pipeline (they are non-blocking like cancelled jobs).
            JobStatus.Skipped => PipelineRunState.Cancelled,
            _ => PipelineRunState.Pending
        };
    }

    /// <summary>
    /// Determines whether a <see cref="PipelineRunState"/> is a terminal state.
    /// </summary>
    private static bool IsTerminalState(PipelineRunState state)
        => state is PipelineRunState.Passed or PipelineRunState.Failed or PipelineRunState.Cancelled;
}
