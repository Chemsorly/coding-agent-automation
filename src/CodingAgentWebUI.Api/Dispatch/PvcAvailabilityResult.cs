namespace CodingAgentWebUI.Api.Dispatch;

/// <summary>
/// Result of <see cref="DispatchLifecycleService.QueryAvailablePvcsAsync"/>.
/// Contains the list of available PVCs and the count of claimed PVCs (for telemetry).
/// </summary>
internal sealed record PvcAvailabilityResult(List<string> AvailablePvcs, int ClaimedCount);
