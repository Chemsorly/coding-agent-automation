using System.Collections.Concurrent;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Services;

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
    private readonly int _defaultBufferCapacity;
    private readonly ILogger _logger;

    public OrchestratorRunService(ILogger logger, int defaultBufferCapacity = 10_000)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(defaultBufferCapacity, 0);

        _logger = logger;
        _defaultBufferCapacity = defaultBufferCapacity;
    }

    /// <summary>
    /// Returns <c>true</c> if any pipeline runs are currently active.
    /// </summary>
    public bool HasActiveRuns => !_activeRuns.IsEmpty;

    /// <summary>
    /// Checks whether the given issue identifier is being processed by any active run.
    /// </summary>
    public bool IsIssueBeingProcessed(string issueIdentifier)
    {
        ArgumentNullException.ThrowIfNull(issueIdentifier);
        return _activeRuns.Values.Any(r => r.IssueIdentifier == issueIdentifier);
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
    public PipelineRun? GetRun(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        return _activeRuns.TryGetValue(runId, out var run) ? run : null;
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
    /// </summary>
    public PipelineRun? RemoveRun(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);

        _activeRuns.TryRemove(runId, out var removed);
        _outputBuffers.TryRemove(runId, out _);

        if (removed is not null)
        {
            _logger.Information("Active run removed: {RunId}", runId);
        }

        return removed;
    }

    /// <summary>
    /// Gets or creates the per-run <see cref="OutputRingBuffer"/> for the specified run.
    /// </summary>
    public OutputRingBuffer GetOutputBuffer(string runId)
    {
        ArgumentNullException.ThrowIfNull(runId);
        return _outputBuffers.GetOrAdd(runId, _ => new OutputRingBuffer(_defaultBufferCapacity));
    }

    /// <summary>
    /// Returns the number of currently active runs.
    /// </summary>
    public int ActiveRunCount => _activeRuns.Count;

    /// <summary>
    /// Resets mutable state for test isolation. Called between E2E tests to prevent state leakage.
    /// </summary>
    internal void ResetForTesting()
    {
        _activeRuns.Clear();
        _outputBuffers.Clear();
    }
}
