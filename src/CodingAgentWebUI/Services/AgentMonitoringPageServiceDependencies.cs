using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.AspNetCore.SignalR;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Groups the core dependencies of <see cref="AgentMonitoringPageService"/> to reduce
/// constructor parameter count (S107). All members are required.
/// </summary>
public sealed record AgentMonitoringPageServiceDependencies(
    IActiveRunQueryService ActiveRunQuery,
    IAgentRegistryService Registry,
    JobDeduplicationGuardService Dispatcher,
    IOrchestratorRunService RunService,
    PipelineRunLifecycleService Lifecycle,
    IConfigurationStore ConfigStore,
    IConsolidationService ConsolidationService,
    IPendingWorkQuery PendingWorkQuery,
    IWorkDistributor WorkDistributor,
    IHubContext<AgentHub, IAgentHubClient> HubContext,
    IPipelineRunHistoryService HistoryService,
    IRunLifecycleManager LifecycleManager);
