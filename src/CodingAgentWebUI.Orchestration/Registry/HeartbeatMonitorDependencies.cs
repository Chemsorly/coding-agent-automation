using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Orchestration.Registry;

/// <summary>
/// Groups the constructor dependencies of <see cref="HeartbeatMonitorService"/>
/// to reduce constructor parameter count (S107). <see cref="ConsolidationService"/> is optional.
/// </summary>
public sealed record HeartbeatMonitorDependencies(
    IAgentRegistryService Registry,
    IOrchestratorRunService RunService,
    IPipelineRunHistoryService HistoryService,
    IConfigurationStore ConfigStore,
    Serilog.ILogger Logger,
    IRunLifecycleManager LifecycleManager,
    IConsolidationService? ConsolidationService = null);
