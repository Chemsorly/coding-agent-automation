using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Groups the mandatory constructor dependencies of <see cref="DispatchOrchestrationService"/>
/// to reduce constructor parameter count (S107). All members are required.
/// </summary>
public sealed record DispatchOrchestrationServiceDependencies(
    DispatchInfrastructure Infra,
    IWorkDistributor WorkDistributor,
    IAgentProfileStore AgentProfileStore,
    IProviderConfigStore ProviderConfigStore,
    IPipelineConfigStore PipelineConfigStore);
