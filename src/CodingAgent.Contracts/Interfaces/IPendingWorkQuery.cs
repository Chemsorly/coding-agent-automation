using CodingAgent.Pipeline.Models;

namespace CodingAgent.Pipeline.Interfaces;

/// <summary>
/// Provides a unified view of work items queued for dispatch.
/// Consumed by UI components and telemetry — never by dispatch logic.
/// </summary>
/// <remarks>
/// Tech debt (#2361): the AgentMonitoring page and its entire backing stack were deleted.
/// This interface is kept solely because <c>WorkDistributionModeResolutionTests</c> and
/// <c>WorkDistributionRegistrationTests</c> reference <c>typeof(IPendingWorkQuery)</c> in
/// compile-time assertions verifying its absence from <c>AddWorkDistribution</c>. Deleting
/// the interface would break compilation of both test files.
/// NOTE: <c>ApiBackedPendingWorkQuery</c> and <c>EmptyPendingWorkQuery</c> still exist in
/// <c>src/CodingAgent.Web/Services/ApiBackedPendingWorkQuery.cs</c>.
/// <c>AddAgentMonitoringPageServiceDependencies()</c> was the only DI registration for these
/// classes and it was deleted with the AgentMonitoring stack, so neither class is currently
/// wired into the main application DI container. They are tested directly in unit tests.
/// TODO: Decide whether to delete <c>ApiBackedPendingWorkQuery</c> and
/// <c>EmptyPendingWorkQuery</c> (and update the tests that reference them directly) or wire
/// <c>ApiBackedPendingWorkQuery</c> into the Work page's DI as the live queue implementation.
/// </remarks>
public interface IPendingWorkQuery
{
    /// <summary>Returns all pending jobs ordered FIFO (oldest first).</summary>
    Task<IReadOnlyList<PendingJob>> GetPendingJobsAsync(CancellationToken ct = default);

    /// <summary>Current count of pending jobs (non-async for telemetry gauges).</summary>
    int PendingCount { get; }
}
