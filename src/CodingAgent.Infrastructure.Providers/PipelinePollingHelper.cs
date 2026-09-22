using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Serilog;

namespace CodingAgent.Infrastructure;

/// <summary>
/// Shared helper that encapsulates the CI-pipeline poll loop and failed-job log-enrichment
/// logic used by both <see cref="GitHub.GitHubActionsPipelineProvider"/> and
/// <see cref="GitLab.GitLabCiPipelineProvider"/>.
///
/// Each provider supplies only the API-specific delegates:
/// <list type="bullet">
///   <item><term>getRunStatusAsync</term><description>Fetch current pipeline status from the provider API.</description></item>
///   <item><term>enrichFailedJobsAsync</term><description>Fetch per-job logs and inject them into the status.</description></item>
///   <item><term>isTerminalState</term><description>Determine whether a <see cref="PipelineRunStatus"/> signals completion.</description></item>
/// </list>
/// </summary>
internal static class PipelinePollingHelper
{
    /// <summary>
    /// Polls the CI provider until a terminal state is reached, a timeout fires, or the caller
    /// cancels. On a terminal <see cref="PipelineRunState.Failed"/> state the failed-job log
    /// enrichment delegate is invoked before returning.
    /// </summary>
    /// <param name="getRunStatusAsync">Provider-specific status fetch.</param>
    /// <param name="enrichFailedJobsAsync">Provider-specific log-enrichment for failed jobs.</param>
    /// <param name="isTerminalState">Predicate that returns true when the run has completed.</param>
    /// <param name="pollInterval">Delay between consecutive status checks.</param>
    /// <param name="timeout">Maximum total wall-clock time before the timeout fallback fires.</param>
    /// <param name="logPrefix">Provider label used in log messages, e.g. "CI" or "GitLab CI".</param>
    /// <param name="ct">Caller cancellation token; cancellation always propagates.</param>
    /// <param name="logger">Serilog logger for poll progress and completion messages.</param>
    internal static async Task<PipelineRunStatus> PollUntilCompleteAsync(
        Func<CancellationToken, Task<PipelineRunStatus>> getRunStatusAsync,
        Func<PipelineRunStatus, CancellationToken, Task<PipelineRunStatus>> enrichFailedJobsAsync,
        Func<PipelineRunStatus, bool> isTerminalState,
        TimeSpan pollInterval,
        TimeSpan timeout,
        string logPrefix,
        CancellationToken ct,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(getRunStatusAsync);
        ArgumentNullException.ThrowIfNull(enrichFailedJobsAsync);
        ArgumentNullException.ThrowIfNull(isTerminalState);
        ArgumentNullException.ThrowIfNull(logPrefix);
        ArgumentNullException.ThrowIfNull(logger);

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

                    var status = await getRunStatusAsync(linkedCt);
                    lastStatus = status;

                    logger.Information("{LogPrefix} poll #{PollCount}: {State} — {JobCount} job(s)",
                        logPrefix, pollCount, status.State, status.Jobs.Count);

                    if (isTerminalState(status))
                    {
                        logger.Information("{LogPrefix} completed: {State} after {PollCount} poll(s)",
                            logPrefix, status.State, pollCount);

                        if (status.State == PipelineRunState.Failed)
                        {
                            // TODO: enrichFailedJobsAsync receives linkedCt (the timeout-linked token), not the
                            // caller's ct. This matches the behaviour of the original inlined code and is not a
                            // regression, but callers of EnrichFailedJobsWithLogsAsync directly should be aware
                            // that the timeout deadline applies to log-fetch requests as well.
                            status = await enrichFailedJobsAsync(status, linkedCt);
                        }

                        return status;
                    }

                    await Task.Delay(pollInterval, linkedCt);
                }
            },
            () =>
            {
                logger.Warning("{LogPrefix} polling timed out after {Timeout} ({PollCount} polls). Last state: {State}",
                    logPrefix, timeout, pollCount, lastStatus?.State);
                return Task.FromResult(lastStatus ?? new PipelineRunStatus
                {
                    State = PipelineRunState.Pending,
                    Jobs = Array.Empty<PipelineJobResult>(),
                    // TODO: CommitSha is hardcoded null here, whereas the original providers set it to the
                    // caller-supplied commitSha. This path is only reached when no poll has completed before
                    // the timeout fires (lastStatus == null). To restore the original contract, consider
                    // adding an optional string? fallbackCommitSha parameter to PollUntilCompleteAsync and
                    // threading it through to the synthetic Pending status returned here.
                    CommitSha = null
                });
            });
    }

    /// <summary>
    /// Fetches full log content for each failed job in <paramref name="status"/> and returns a
    /// new <see cref="PipelineRunStatus"/> with <see cref="PipelineJobResult.LogContent"/> injected.
    /// Jobs with <see cref="PipelineJobResult.JobId"/> equal to zero are skipped (no valid API ID).
    /// If no logs are successfully retrieved the original status is returned unchanged.
    /// </summary>
    /// <param name="status">Current pipeline status containing the job list.</param>
    /// <param name="getJobLogsAsync">Provider-specific delegate that returns raw log text or null.</param>
    /// <param name="logPrefix">Provider label used in debug messages, e.g. "job" or "GitLab job".</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="logger">Serilog logger for per-job debug messages.</param>
    internal static async Task<PipelineRunStatus> EnrichFailedJobsWithLogsAsync(
        PipelineRunStatus status,
        Func<long, CancellationToken, Task<string?>> getJobLogsAsync,
        string logPrefix,
        CancellationToken ct,
        ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(getJobLogsAsync);
        ArgumentNullException.ThrowIfNull(logPrefix);
        ArgumentNullException.ThrowIfNull(logger);

        var failedJobIds = status.Jobs
            .Where(j => j.State == PipelineRunState.Failed && j.JobId > 0)
            .Select(j => j.JobId)
            .ToHashSet();

        if (failedJobIds.Count == 0)
            return status;

        var logsByJobId = new Dictionary<long, string>();
        foreach (var jobId in failedJobIds)
        {
            var logContent = await getJobLogsAsync(jobId, ct);
            if (logContent is not null)
            {
                logsByJobId[jobId] = logContent;
                logger.Debug("Fetched {Length} chars of logs for failed {LogPrefix} (id={JobId})",
                    logContent.Length, logPrefix, jobId);
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
}
