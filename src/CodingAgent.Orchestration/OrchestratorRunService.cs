using System.Collections.Concurrent;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration;

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
    /// If a run with the same RunId already exists, it is replaced (upsert) and the backlog preserved.
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
            // Upsert: replace the stored run; preserve the existing backlog (TryAdd is a no-op if buffer exists).
            _activeRuns[run.RunId] = run;
            _outputBuffers.TryAdd(run.RunId, new OutputRingBuffer(_defaultBufferCapacity));
            _logger.Information(
                "Active run re-added: {RunId} for issue {IssueIdentifier} (agent={AgentId})",
                run.RunId, run.IssueIdentifier, run.AgentId ?? "local");
        }
    }

    /// <summary>
    /// Removes a pipeline run from the active runs collection and disposes its output buffer.
    /// </summary>
    public PipelineRun? RemoveRun(RunId runId)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId.Value);

        _activeRuns.TryRemove(runId.Value, out var removed);
        _outputBuffers.TryRemove(runId.Value, out _);

        if (removed is not null)
            _logger.Information("Active run removed: {RunId}", runId);
        return removed;
    }

    /// <summary>
    /// Replaces an existing run with a new instance for the same RunId using a CAS loop.
    /// If the RunId is not currently active, logs a warning and does nothing.
    /// </summary>
    public void ReplaceRun(PipelineRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        while (_activeRuns.TryGetValue(run.RunId, out var current))
        {
            if (_activeRuns.TryUpdate(run.RunId, run, current))
            {
                _logger.Debug("Active run replaced: {RunId} for issue {IssueIdentifier}", run.RunId, run.IssueIdentifier);
                return;
            }
        }

        _logger.Warning("ReplaceRun: run {RunId} is not active — ignoring (run may have been removed)", run.RunId);
    }

    /// <summary>
    /// Gets or creates the per-run <see cref="OutputRingBuffer"/> for the specified run.
    /// This method is intentionally NOT on the interface — callers should use
    /// <see cref="AppendOutputLines"/> to write and <see cref="GetOutputBacklogAsync"/> to read.
    /// Unit tests and E2E tests may use this directly via the concrete type.
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
        ArgumentNullException.ThrowIfNull(lines);
        if (lines.Count == 0) return;
        GetOutputBuffer(runId).AddRange(lines);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> GetOutputBacklogAsync(RunId runId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(runId.Value);
        if (!_outputBuffers.TryGetValue(runId.Value, out var buffer))
            return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());

        return Task.FromResult<IReadOnlyList<string>>(buffer.GetAll());
    }

    /// <inheritdoc />
    public Task<HashSet<string>> GetActiveRunBranchesAsync(CancellationToken ct = default)
    {
        var branches = GetActiveRuns()
            .Where(r => r.BranchName != null)
            .Select(r => r.BranchName!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(branches);
    }
}
