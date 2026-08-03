using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Groups the mandatory constructor dependencies of <see cref="AgentJobDispatcher"/>
/// to reduce constructor parameter count (S107). <see cref="LifecycleManager"/> is optional.
/// </summary>
public sealed record AgentJobDispatcherDependencies(
    JobDeduplicationGuardService Dispatcher,
    IAgentRegistryService Registry,
    IOrchestratorRunService RunService,
    IDispatchRunCreator Orchestration,
    DispatchInfrastructure Infra,
    IAgentCommunication AgentComm,
    IShutdownSignal ShutdownSignal,
    Serilog.ILogger Logger,
    IRunLifecycleManager? LifecycleManager = null);
