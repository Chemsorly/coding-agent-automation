using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Groups the mandatory constructor dependencies of <see cref="JobQueueDrainService"/>
/// to reduce constructor parameter count (S107). <see cref="ConsolidationRunStore"/> is optional.
/// </summary>
public sealed record JobQueueDrainDependencies(
    JobDeduplicationGuardService Dispatcher,
    IAgentRegistryService Registry,
    IJobDispatcher JobDispatcher,
    IConfigurationStore ConfigStore,
    IConsolidationDispatchService ConsolidationDispatcher,
    IShutdownSignal ShutdownSignal,
    Serilog.ILogger Logger,
    IConsolidationRunStore? ConsolidationRunStore = null);
