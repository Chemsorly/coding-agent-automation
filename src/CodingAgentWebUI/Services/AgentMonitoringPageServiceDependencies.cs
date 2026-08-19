using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Groups the core dependencies of <see cref="AgentMonitoringPageService"/> to reduce
/// constructor parameter count (S107). All members are required.
/// <para>
/// Spec 044: IOrchestratorRunService, IRunLifecycleManager, IHubContext, and PipelineRunLifecycleService
/// removed — the monolith no longer owns in-memory run state or agent hub connections.
/// Components now read history from the Pipeline API.
/// </para>
/// </summary>
public sealed record AgentMonitoringPageServiceDependencies(
    IActiveRunQueryService ActiveRunQuery,
    IAgentRegistryService Registry,
    JobDeduplicationGuardService Dispatcher,
    IConfigurationStore ConfigStore,
    IConsolidationService ConsolidationService,
    IPendingWorkQuery PendingWorkQuery,
    IWorkDistributor WorkDistributor,
    IPipelineRunHistoryService HistoryService);
