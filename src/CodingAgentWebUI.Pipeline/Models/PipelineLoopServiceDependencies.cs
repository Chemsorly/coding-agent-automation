using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Pipeline.Models;

/// <summary>
/// Groups the constructor parameters of <see cref="Services.PipelineLoopService"/>
/// into a single parameter object to satisfy S107. Registered as singleton in DI.
/// </summary>
public sealed record PipelineLoopServiceDependencies
{
    public required IDispatchRunCreator Orchestration { get; init; }
    public required IProviderFactory ProviderFactory { get; init; }
    public required IPipelineConfigStore PipelineConfigStore { get; init; }
    public required IProviderConfigStore ProviderConfigStore { get; init; }
    public required IProjectStore ProjectStore { get; init; }
    public required Serilog.ILogger Logger { get; init; }
    public required IWorkDistributor? WorkDistributor { get; init; }
    public required IDispatchOrchestrationService? DispatchOrchestration { get; init; }
    public required IDependencyChecker? DependencyChecker { get; init; }
    public required IHousekeepingService? HousekeepingService { get; init; }

    /// <summary>
    /// Optional leader-election gate. When non-null (K8s mode),
    /// <see cref="Services.PipelineLoopService"/> waits for leadership before activating
    /// its poll loop and stops cleanly on leadership loss. When null (no
    /// <c>ILeaderElectionService</c> registered, e.g. test environments), the loop runs
    /// unconditionally.
    /// </summary>
    public required ILeaderGate? LeaderElection { get; init; }

    /// <summary>
    /// Optional client for the queue sweep. When non-null, the sweep fetches Pending WorkItems
    /// and cancels those whose issue is no longer in the current eligibility set (requires
    /// <see cref="PipelineConfiguration.QueueSweepEnabled"/> = true). When null, the sweep
    /// is skipped — safe default for environments that do not run the sweep (e.g. test environments).
    /// </summary>
    public IWorkItemSweepClient? WorkItemClient { get; init; }
}
