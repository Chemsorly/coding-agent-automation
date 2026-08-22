using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;

namespace CodingAgentWebUI.Services;

/// <summary>
/// Groups the core dependencies of <see cref="AgentMonitoringPageService"/> to reduce
/// constructor parameter count (S107). All members are required.
/// </summary>
public sealed record AgentMonitoringPageServiceDependencies(
    IAgentRegistryService Registry,
    JobDeduplicationGuardService Dispatcher,
    IPipelineApiConfigClient ConfigClient,
    IConsolidationService ConsolidationService,
    /// Optional — null when IPendingWorkQuery is not registered (no DB-backed pending work query available).
    /// </summary>
    IPendingWorkQuery? PendingWorkQuery,
    IWorkDistributor WorkDistributor,
    IPipelineApiRunHistoryClient RunHistoryClient);
