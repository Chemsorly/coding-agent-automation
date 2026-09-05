using System.Collections.Concurrent;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Orchestration;

/// <summary>
/// Tracks all active pipeline runs across agents. Replaces the single <c>ActiveRun</c>
/// property with a concurrent collection supporting multiple simultaneous runs.
/// Also manages per-run <see cref="OutputRingBuffer"/> instances.
/// Registered as a singleton in DI.
/// </summary>
public sealed class OrchestratorRunService : IOrchestratorRunService
{
    private readonly ConcurrentDictionary<string, PipelineRun> _activeRuns = new();

    private readonly ConcurrentDictionary<string, OutputRingBuffer> _outputBuffers = new();
    private readonly ConcurrentDictionary<string, DateTimeOffset> _recentlyCompleted = new();
    private readonly int _defaultBufferCapacity;
    private readonly ILogger _logger;
    private readonly TimeProvider _timeProvider;

    private static readonly TimeSpan RecentCompletionTtl = TimeSpan.FromSeconds(120);

    public OrchestratorRunService(ILogger logger, int defaultBufferCapacity = PipelineConstants.DefaultOutputBufferCapacity, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(defaultBufferCapacity, 0);

        _logger = logger;
        _defaultBufferCapacity = defaultBufferCapacity;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>
    /// Returns <c>true</c> if any pipeline runs are currently active.
    /// </summary>
    public bool HasActiveRuns => !_activeRuns.IsEmpty;

    /// <summary>
    /// Checks whether the given issue identifier is being processed by any active run.
    /// </summary>
    public bool IsIssueBeingProcessed(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
    {
        ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value, nameof(issueIdentifier));
        var compositeKey = $"{issueProviderConfigId.Value}:{issueIdentifier}";
        return _activeRuns.Values.Any(r => $"{r.IssueProviderConfigId}:{r.IssueIdentifier}" == compositeKey);
    }

    /// <summary>
    /// Returns all active runs as a read-only snapshot.
    /// </summary>
    public IReadOnlyList<PipelineRun> GetActiveRuns()
    {
        return _activeRuns.Values.ToList().AsReadOnly();
    }

    /// <summary>
    /// Gets a specific run by its <see cref="PipelineRun.RunId"/>.
    /// </summary>
    public PipelineRun? GetRun(RunId runId)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId.Value);
        return _activeRuns.TryGetValue(runId.Value, out var run) ? run : null;
    }

    /// <summary>
    /// Adds a pipeline run to the active runs collection.
    /// Also creates a per-run <see cref="OutputRingBuffer"/>.
    /// </summary>
    public void AddRun(PipelineRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        if (_activeRuns.TryAdd(run.RunId, run))
        {
            _outputBuffers.TryAdd(run.RunId, new OutputRingBuffer(_defaultBufferCapacity));
            _logger.Information(
                "Active run added: {RunId} for issue {IssueIdentifier} (agent={AgentId})",
                run.RunId, run.IssueIdentifier, run.AgentId ?? "local");
        }
        else
        {
            _logger.Warning("Run {RunId} already exists in active runs", run.RunId);
        }
    }

    /// <summary>
    /// Removes a pipeline run from the active runs collection and disposes its output buffer.
    /// Also disposes any open <see cref="PipelineRun.OrchestratorActivity"/> that was not already
    /// stopped by a terminal RunLifecycleManager path (FailRunAsync/CompleteRunAsync/CancelRunAsync).
    /// This safety net prevents orphaned open spans in Tempo when a run is evicted outside the
    /// normal terminal paths (e.g. pod restart, direct removal, or an exception before terminal
    /// transition). The span will appear without a terminal status tag, but it will at least be
    /// exported rather than pinned in memory forever.
    /// </summary>
    public PipelineRun? RemoveRun(RunId runId)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId.Value);

        _activeRuns.TryRemove(runId.Value, out var removed);
        _outputBuffers.TryRemove(runId.Value, out _);

        if (removed is not null)
        {
            _logger.Information("Active run removed: {RunId}", runId);
            // Safety net: dispose any open orchestrator span not already stopped by a
            // terminal lifecycle transition (issue #2255). Activity.Dispose is idempotent —
            // if the span was already stopped by RunLifecycleManager this is a no-op.
            removed.OrchestratorActivity?.Dispose();
        }
        return removed;
    }

    /// <summary>
    /// Replaces an existing run with a new instance for the same RunId.
    /// Used by review dispatch to update a run with review-specific metadata without
    /// creating a gap where IsIssueBeingProcessed returns false.
    /// The output buffer is preserved (not recreated).
    /// If a genuinely different <see cref="PipelineRun"/> object is displaced (i.e. not the same
    /// reference as <paramref name="run"/>), disposes its open <see cref="PipelineRun.OrchestratorActivity"/>
    /// to prevent orphaned open spans in Tempo (issue #2255). Non-terminal callers that follow the
    /// read-mutate-replace pattern (GetRun → mutate → ReplaceRun) pass back the same object reference,
    /// so no dispose occurs in that common path.
    /// </summary>
    public void ReplaceRun(PipelineRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        // Read the existing entry before overwriting it so we can detect a genuinely displaced run.
        // Non-terminal callers follow the read-mutate-replace pattern (GetRun → mutate → ReplaceRun)
        // and pass the same object reference back, so previousRun will be ReferenceEqual to run.
        // Only a review-dispatch scenario (or a future caller) would supply a different object.
        _activeRuns.TryGetValue(run.RunId, out var previousRun);
        _activeRuns[run.RunId] = run;

        // Safety net: dispose any open orchestrator span on the displaced run (issue #2255).
        // Activity.Dispose is idempotent — safe to call even if already stopped.
        // Guard: skip when the same reference is being put back (common non-terminal update path)
        // to avoid stopping the still-active span and truncating it in Tempo.
        if (previousRun is not null && !ReferenceEquals(previousRun, run))
            previousRun.OrchestratorActivity?.Dispose();

        _logger.Debug("Active run replaced: {RunId} for issue {IssueIdentifier}", run.RunId, run.IssueIdentifier);
    }

    /// <summary>
    /// Gets or creates the per-run <see cref="OutputRingBuffer"/> for the specified run.
    /// </summary>
    public OutputRingBuffer GetOutputBuffer(RunId runId)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId.Value);
        return _outputBuffers.GetOrAdd(runId.Value, _ => new OutputRingBuffer(_defaultBufferCapacity));
    }

    /// <summary>
    /// Returns the number of currently active runs.
    /// </summary>
    public int ActiveRunCount => _activeRuns.Count;

    /// <inheritdoc />
    public void MarkRecentlyCompleted(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
    {
        ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value, nameof(issueIdentifier));
        var key = $"{issueProviderConfigId.Value}:{issueIdentifier}";
        _recentlyCompleted[key] = _timeProvider.GetUtcNow();
    }

    /// <inheritdoc />
    public bool WasRecentlyCompleted(IssueIdentifier issueIdentifier, ProviderConfigId issueProviderConfigId)
    {
        ArgumentException.ThrowIfNullOrEmpty(issueIdentifier.Value, nameof(issueIdentifier));
        var key = $"{issueProviderConfigId.Value}:{issueIdentifier}";
        if (_recentlyCompleted.TryGetValue(key, out var completedAt))
        {
            if (_timeProvider.GetUtcNow() - completedAt <= RecentCompletionTtl)
                return true;

            // Expired — remove lazily
            _recentlyCompleted.TryRemove(key, out _);
        }
        return false;
    }

    /// <summary>
    /// Clears all active runs and output buffers. Used by E2E tests for state isolation.
    /// </summary>
    internal void Reset()
    {
        _activeRuns.Clear();
        _outputBuffers.Clear();
        _recentlyCompleted.Clear();
    }

    /// <inheritdoc />
    public void AppendOutputLines(RunId runId, IReadOnlyList<string> lines)
    {
        // No-op: the hub writes directly to the buffer via GetOutputBuffer(jobId).AddRange(lines).
        // DistributedRunService overrides this to write to Redis (distributed persistence).
    }
}
