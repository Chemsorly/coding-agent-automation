using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Groups the core dependencies of <see cref="AgentMonitoringPageService"/> to reduce
/// constructor parameter count (S107). All members are required.
/// T19 (arch-audit 2026-08-22): PendingWorkQuery is no longer nullable — ApiBackedPendingWorkQuery
/// is registered unconditionally so the job-queue panel is always populated.
/// </summary>
public sealed record AgentMonitoringPageServiceDependencies(
    IAgentRegistryService Registry,
    JobDeduplicationGuardService Dispatcher,
    IPipelineApiConfigClient ConfigClient,
    IConsolidationService ConsolidationService,
    IPendingWorkQuery PendingWorkQuery,
    IWorkDistributor WorkDistributor,
    IPipelineApiRunHistoryClient RunHistoryClient,
    IPipelineApiWorkItemClient WorkItemClient);
