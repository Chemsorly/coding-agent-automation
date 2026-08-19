using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Services;

/// <summary>
/// No-op implementation of <see cref="IChangeNotifier"/> for the monolith after Spec 044.
/// The monolith no longer owns in-memory run state — <see cref="IOrchestratorRunService"/>
/// has been moved to <c>CodingAgentWebUI.Api</c>. This stub keeps DI satisfied without a
/// DI exception until Spec 045 removes the IChangeNotifier injection from Blazor components entirely.
/// </summary>
internal sealed class NullChangeNotifier : IChangeNotifier
{
    // Spec 044: no-op — monolith has no in-memory run state to notify about.
    // The event add/remove are intentionally no-ops; no subscriber will receive events.
#pragma warning disable CS0067 // The event is never used
    public event Action? OnChange;
#pragma warning restore CS0067

    public void NotifyChange() { /* no-op */ }
}
