using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Null-object implementation of <see cref="IChangeNotifier"/> for the monolith process.
/// The monolith no longer drives state-change notifications directly — change events arrive
/// via <see cref="IAgentHubConnection"/> hub push events (OnStepTransition, OnRunCompleted).
/// This registration satisfies any remaining <see cref="IChangeNotifier"/> constructor
/// dependencies in shared libraries without wiring up a real notification sink.
/// </summary>
internal sealed class NullChangeNotifier : IChangeNotifier
{
#pragma warning disable CS0067 // Event is never used — intentional for null-object pattern
    public event Action? OnChange;
#pragma warning restore CS0067

    /// <inheritdoc />
    /// <remarks>No-op. Change notification in the monolith is handled by hub events.</remarks>
    public void NotifyChange() { }
}
