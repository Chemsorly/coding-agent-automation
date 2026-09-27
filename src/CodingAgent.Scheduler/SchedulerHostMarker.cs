using System.Diagnostics.CodeAnalysis;

namespace CodingAgent.Scheduler;

/// <summary>
/// Entry-point marker for <c>WebApplicationFactory&lt;T&gt;</c>.
///
/// The factory only uses its type argument to locate the assembly holding the entry point, and
/// this assembly's generated entry point is the global <c>Program</c> — the same simple name the
/// Web host uses. A test project that hosts both (the E2E harness runs the Scheduler and the
/// Blazor app side by side) cannot name either one unambiguously, so it targets this marker instead.
/// </summary>
[ExcludeFromCodeCoverage]
public sealed class SchedulerHostMarker
{
    public SchedulerHostMarker() { }
}
