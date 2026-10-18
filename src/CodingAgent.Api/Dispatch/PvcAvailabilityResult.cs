namespace CodingAgent.Api.Dispatch;

/// <summary>
/// Result of <see cref="DispatchLifecycleService.QueryAvailablePvcsAsync"/> and
/// <see cref="DispatchLifecycleService.QueryDispatchablePvcsAsync"/>.
/// Contains the list of available PVCs and the count of PVCs active WorkItems claim (for telemetry).
/// </summary>
internal sealed record PvcAvailabilityResult(List<string> AvailablePvcs, int ClaimedCount);
