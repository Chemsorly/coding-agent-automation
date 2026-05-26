using System.Collections.Concurrent;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Manages the queue of pending consolidation jobs awaiting dispatch.
/// Mirrors <see cref="JobDispatcherService"/> but for consolidation-specific jobs
/// with different concurrency semantics (keyed by RunId, not issue identifier).
/// </summary>
public class ConsolidationQueueService
{
    private readonly ConcurrentQueue<PendingConsolidationJob> _queue = new();
    private readonly ConcurrentDictionary<string, bool> _queuedRunIds = new();
    // TODO: _cancelledRunIds grows unboundedly — entries are never removed. Add cleanup after
    // the drain service confirms the cancelled job is no longer in the queue.
    private readonly ConcurrentDictionary<string, bool> _cancelledRunIds = new();
    private readonly ILogger _logger;
    private readonly object _queueLock = new();

    public ConsolidationQueueService(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>Returns the current number of jobs in the queue.</summary>
    public int QueueLength => _queue.Count;

    /// <summary>
    /// Enqueues a consolidation job. Rejects duplicates (same RunId).
    /// </summary>
    public bool EnqueueJob(PendingConsolidationJob job)
    {
        ArgumentNullException.ThrowIfNull(job);

        if (!_queuedRunIds.TryAdd(job.RunId, true))
        {
            _logger.Warning("Consolidation job {RunId} rejected — already queued", job.RunId);
            return false;
        }

        _queue.Enqueue(job);
        _logger.Information("Consolidation job enqueued: {RunId} (type={Type})", job.RunId, job.Type);
        return true;
    }

    /// <summary>
    /// Dequeues the next compatible job for the given agent (label matching).
    /// Non-matching jobs are re-enqueued at the back.
    /// </summary>
    public PendingConsolidationJob? DequeueForAgent(AgentEntry agent)
    {
        ArgumentNullException.ThrowIfNull(agent);

        lock (_queueLock)
        {
            var count = _queue.Count;
            for (var i = 0; i < count; i++)
            {
                if (!_queue.TryDequeue(out var job))
                    break;

                if (IsLabelMatch(agent.Labels, job.RequiredLabels))
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
    /// Removes a job from the queue by RunId (e.g., on cancel).
    /// </summary>
    public bool RemoveFromQueue(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);

        if (!_queuedRunIds.TryRemove(runId, out _))
            return false;

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

        _logger.Information("Removed consolidation job {RunId} from queue", runId);
        return true;
    }

    /// <summary>Marks a run as cancelled so the drain service skips it if already dequeued.</summary>
    public void MarkCancelled(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        _cancelledRunIds.TryAdd(runId, true);
    }

    /// <summary>Checks whether a run has been cancelled.</summary>
    public bool IsCancelled(string runId) => _cancelledRunIds.ContainsKey(runId);

    /// <summary>Checks whether a run is currently queued.</summary>
    public bool IsRunQueued(string runId) => _queuedRunIds.ContainsKey(runId);

    /// <summary>Returns all currently queued jobs as a snapshot.</summary>
    public IReadOnlyList<PendingConsolidationJob> GetQueuedJobs() => _queue.ToArray().ToList().AsReadOnly();

    private static bool IsLabelMatch(IReadOnlyList<string> agentLabels, IReadOnlyList<string> requiredLabels)
    {
        if (requiredLabels.Count == 0)
            return true;

        var agentLabelSet = new HashSet<string>(agentLabels, StringComparer.OrdinalIgnoreCase);
        return requiredLabels.All(label => agentLabelSet.Contains(label));
    }
}
