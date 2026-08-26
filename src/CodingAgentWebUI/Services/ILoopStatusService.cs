using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Read-only view of pipeline loop state for Blazor components.
/// Mirrors the read-only surface of <see cref="CodingAgentWebUI.Pipeline.Interfaces.IPipelineLoopService"/>
/// so <see cref="CodingAgentWebUI.Components.Layout.MainLayout"/> and
/// <see cref="CodingAgentWebUI.Components.Pages.AgentCoding"/> can bind without changes.
/// <para>
/// <see cref="IsSchedulerUnreachable"/> is added to expose transport-layer health so the UI
/// can display a "Scheduler unavailable" warning when polling fails.
/// </para>
/// </summary>
public interface ILoopStatusService
{
    /// <summary>Fired when any loop state property changes.</summary>
    event Action? OnChange;

    bool IsLoopActive { get; }
    string StatusMessage { get; }
    string? CurrentIssueIdentifier { get; }
    int ProcessedCount { get; }
    int FailedCount { get; }
    int QueueCount { get; }
    bool IsCircuitBroken { get; }
    string? LastPollError { get; }
    int CurrentCycleTemplateIndex { get; }
    int CurrentCycleTemplateCount { get; }
    IReadOnlyList<string> ValidationErrors { get; }
    IReadOnlyDictionary<string, ConfigStatusSnapshot> TemplateStatuses { get; }

    /// <summary>
    /// True when the Scheduler's /loop/status endpoint is unreachable.
    /// The UI shows a warning banner; Start/Stop/Resume controls are disabled.
    /// </summary>
    bool IsSchedulerUnreachable { get; }
}
