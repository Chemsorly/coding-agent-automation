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
/// <remarks>
/// <para>
/// <b>Design Decision: Intentionally non-sealed.</b>
/// This class is non-sealed to allow E2E test subclasses (specifically
/// <c>ResettableOrchestratorRunService</c> in
/// <c>tests/CodingAgentWebUI.E2ETests/Infrastructure/ResettableServices.cs</c>)
/// to inherit and expose a <c>Reset()</c> method for test isolation.
/// </para>
/// <para>
/// <b>Sealed + Composition vs Non-Sealed + Inheritance Tradeoff:</b>
/// The preferred .NET pattern is to seal classes by default and use composition-based
/// test doubles (e.g., wrapper/decorator pattern with extracted interfaces). The current
/// non-sealed + inheritance approach was chosen for pragmatic E2E test state reset without
/// polluting the production API with reset methods. Migration to sealed + composition
/// requires: (1) extracting an interface (already done: <see cref="IOrchestratorRunService"/>),
/// (2) updating E2E tests to use a wrapper/decorator that delegates to the real service
/// and adds reset capability, and (3) verifying no production code relies on inheritance.
/// This migration is documented as a future improvement — see Requirement 22.
/// </para>
/// </remarks>
public class OrchestratorRunService : IOrchestratorRunService
{
    /// <summary>
    /// Backing store for active pipeline runs. Exposed as <c>protected</c> to allow
    /// E2E test subclasses (e.g., <c>ResettableOrchestratorRunService</c>) to clear state
    /// between tests via <c>_activeRuns.Clear()</c>.
    /// </summary>
    /// <remarks>
    /// The preferred .NET pattern for test access is <c>internal</c> visibility combined with
    /// <c>[InternalsVisibleTo]</c> in the <c>.csproj</c>. The <c>protected</c> modifier is used
    /// here because the E2E test subclass pattern requires inheritance-based access. If migrating
    /// to sealed + composition, this field should become <c>private</c>.
    /// </remarks>
    protected readonly ConcurrentDictionary<string, PipelineRun> _activeRuns = new();

    /// <summary>
    /// Per-run output ring buffers for streaming output to the UI. Exposed as <c>protected</c>
    /// to allow E2E test subclasses to clear state between tests.
    /// </summary>
    /// <remarks>
    /// The preferred .NET pattern for test access is <c>internal</c> visibility combined with
    /// <c>[InternalsVisibleTo]</c> in the <c>.csproj</c>. The <c>protected</c> modifier is used
    /// here because the E2E test subclass pattern requires inheritance-based access. If migrating
    /// to sealed + composition, this field should become <c>private</c>.
    /// </remarks>
    protected readonly ConcurrentDictionary<string, OutputRingBuffer> _outputBuffers = new();
    private readonly int _defaultBufferCapacity;
    private readonly ILogger _logger;

    public OrchestratorRunService(ILogger logger, int defaultBufferCapacity = PipelineConstants.DefaultOutputBufferCapacity)
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
}
