using System.Collections.Concurrent;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Pending consolidation job awaiting dispatch to an available agent.
/// </summary>
public sealed record PendingConsolidationJob
{
    public required string RunId { get; init; }
    public required ConsolidationRunType Type { get; init; }
    public string? TemplateId { get; init; }
    public required string WorkspacePath { get; init; }
    public required IReadOnlyList<string> RequiredLabels { get; init; }
    public required DateTimeOffset EnqueuedAt { get; init; }
    public int RetryCount { get; set; }
}

/// <summary>
/// Manages the consolidation job queue. Queued consolidation runs are dispatched
/// when an idle agent becomes available via the <see cref="JobQueueDrainService"/>.
/// Registered as a singleton in DI.
/// </summary>
public sealed class ConsolidationQueueService
{
    private readonly ConcurrentQueue<PendingConsolidationJob> _queue = new();
    private readonly ConcurrentDictionary<string, bool> _queuedRunIds = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _cancelledRunIds = new();
    private readonly ILogger _logger;
    private readonly object _queueLock = new();

    /// <summary>Maximum dispatch retry attempts before marking a run as permanently failed.</summary>
    internal const int MaxRetryCount = 5;

    /// <summary>How long cancelled run IDs are retained before eviction.</summary>
    internal static readonly TimeSpan EvictionWindow = TimeSpan.FromMinutes(5);

    public ConsolidationQueueService(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Enqueues a consolidation job for later dispatch.
    /// </summary>
    public void EnqueueJob(PendingConsolidationJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!_queuedRunIds.TryAdd(job.RunId, true))
        {
            _logger.Warning("Consolidation job {RunId} already queued, skipping", job.RunId);
            return;
        }

        _queue.Enqueue(job);
        _logger.Information(
            "Consolidation job enqueued: {RunId} (type={Type}, template={TemplateId})",
            job.RunId, job.Type, job.TemplateId ?? "Global");
    }

    /// <summary>
    /// Dequeues the next compatible job for the given agent (label matching).
    /// Non-matching jobs are re-enqueued at the back.
    /// </summary>
    public PendingConsolidationJob? DequeueForAgent(AgentEntry agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        var cutoff = DateTimeOffset.UtcNow - EvictionWindow;
        foreach (var kvp in _cancelledRunIds)
        {
            if (kvp.Value < cutoff)
                _cancelledRunIds.TryRemove(kvp.Key, out _);
        }

        lock (_queueLock)
        {
            var count = _queue.Count;
            for (var i = 0; i < count; i++)
            {
                if (!_queue.TryDequeue(out var job))
                    break;

                if (LabelMatchHelper.IsLabelMatch(agent.Labels, job.RequiredLabels))
                {
                    _queuedRunIds.TryRemove(job.RunId, out _);
                    return job;
                }

                _queue.Enqueue(job);
            }
        }

        return null;
    }

    /// <summary>
    /// Marks a run as cancelled so it won't be dispatched even if dequeued.
    /// Also removes it from the queue.
    /// </summary>
    public void CancelRun(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        _cancelledRunIds.TryAdd(runId, DateTimeOffset.UtcNow);
        RemoveFromQueue(runId);
    }

    /// <summary>
    /// Checks whether a run has been cancelled (for cancel-during-dispatch race handling).
    /// </summary>
    public bool IsRunCancelled(string runId) => _cancelledRunIds.ContainsKey(runId);

    /// <summary>
    /// Returns the current queue length.
    /// </summary>
    public int QueueLength => _queue.Count;

    /// <summary>
    /// Re-enqueues a job (e.g., after a failed dispatch attempt).
    /// </summary>
    public void ReEnqueue(PendingConsolidationJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        _queuedRunIds.TryAdd(job.RunId, true);
        _queue.Enqueue(job);
    }

    private void RemoveFromQueue(string runId)
    {
        _queuedRunIds.TryRemove(runId, out _);

        lock (_queueLock)
        {
            var count = _queue.Count;
            for (var i = 0; i < count; i++)
            {
                if (!_queue.TryDequeue(out var job))
                    break;

                if (job.RunId != runId)
                    _queue.Enqueue(job);
            }
        }
    }
}
