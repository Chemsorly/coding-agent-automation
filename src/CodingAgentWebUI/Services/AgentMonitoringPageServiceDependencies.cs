using CodingAgentWebUI.Api.Client;
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
/// Spec 045: IConfigurationStore replaced by IPipelineApiConfigClient; IPipelineRunHistoryService
/// replaced by IPipelineApiRunHistoryClient; IActiveRunQueryService removed — active runs are now
/// derived from run history via IPipelineApiRunHistoryClient by filtering non-terminal steps.
/// The monolith has no direct Postgres access.
/// </para>
/// </summary>
public sealed record AgentMonitoringPageServiceDependencies(
    IAgentRegistryService Registry,
    JobDeduplicationGuardService Dispatcher,
    IPipelineApiConfigClient ConfigClient,
    IConsolidationService ConsolidationService,
    /// <summary>
    /// Optional — was removed from monolith DI in Spec 045 Req 1.2 (M1 gauge audit).
    /// Will be null in production until migrated to IPipelineApiWorkItemClient.
    /// </summary>
    IPendingWorkQuery? PendingWorkQuery,
    IWorkDistributor WorkDistributor,
    IPipelineApiRunHistoryClient RunHistoryClient);
