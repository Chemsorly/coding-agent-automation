using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Pipeline.Interfaces;

/// <summary>
/// Abstraction for the pipeline loop — polls for issues and dispatches them to agents.
/// Consumers bind to state properties and the <see cref="OnChange"/> event for UI updates.
/// </summary>
public interface IPipelineLoopService
{
    /// <summary>Fired when loop state changes, for UI binding.</summary>
    event Action? OnChange;

    /// <summary>Whether the loop is currently active (processing or polling).</summary>
    bool IsLoopActive { get; }

    /// <summary>Current status message for UI display.</summary>
    string StatusMessage { get; }

    /// <summary>Identifier of the issue currently being processed, or null.</summary>
    string? CurrentIssueIdentifier { get; }

    /// <summary>Number of issues processed in the current loop activation.</summary>
    int ProcessedCount { get; }

    /// <summary>Number of issues that failed in the current loop activation.</summary>
    int FailedCount { get; }

    /// <summary>Number of agent:next issues remaining in the current queue snapshot.</summary>
    int QueueCount { get; }

    /// <summary>Number of consecutive poll failures since last successful poll.</summary>
    int ConsecutivePollFailures { get; }

    /// <summary>Whether the circuit breaker has tripped due to consecutive poll failures.</summary>
    bool IsCircuitBroken { get; }

    /// <summary>Last poll error message, or null if last poll succeeded.</summary>
    string? LastPollError { get; }

    /// <summary>Per-template status for UI binding (immutable snapshots, atomically swapped).</summary>
    IReadOnlyDictionary<string, ConfigStatusSnapshot> TemplateStatuses { get; }

    /// <summary>Index of the template currently being polled in this cycle (0-based).</summary>
    int CurrentCycleTemplateIndex { get; }

    /// <summary>Total number of enabled templates in the current cycle.</summary>
    int CurrentCycleTemplateCount { get; }

    /// <summary>Validation errors from the last failed StartLoop() call.</summary>
    IReadOnlyList<string> ValidationErrors { get; }

    /// <summary>
    /// Activates the multi-template round-robin loop.
    /// Returns false if no enabled templates exist or validation fails.
    /// </summary>
    Task<bool> StartLoopAsync();

    /// <summary>
    /// Requests the loop to stop. If a run is in progress, it finishes first.
    /// </summary>
    void StopLoop();

    /// <summary>
    /// Resumes the loop after the circuit breaker has tripped.
    /// </summary>
    void ResumeLoop();
}
