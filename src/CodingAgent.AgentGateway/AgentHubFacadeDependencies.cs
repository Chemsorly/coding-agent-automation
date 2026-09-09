using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Services;
using Microsoft.Extensions.Logging;

namespace CodingAgent.AgentGateway;

/// <summary>
/// Groups the core dependencies of <see cref="AgentHubFacade"/> to reduce
/// constructor parameter count (S107). Optional members default to null.
/// </summary>
public sealed record AgentHubFacadeDependencies(
    IAgentRegistryService Registry,
    IOrchestratorRunService RunService,
    IPipelineRunHistoryService HistoryService,
    IProviderConfigStore ConfigStore,
    IProviderFactory ProviderFactory,
    ILogger<AgentHubFacadeDependencies> Logger,
    // Direct WorkItem DB access abstracted behind Contracts (Spec 048 Phase 2 — DB isolation).
    // Null in in-memory / test hosts, in which case the facade degrades to no-ops.
    IWorkItemTransitionStore? TransitionStore = null,
    IProjectStore? ProjectStore = null,
    IWorkItemFallbackTransitionService? WorkItemFallbackTransition = null,
    // TimeProvider is always provided by production via GetRequiredService<TimeProvider>().
    // Optional only for test paths that construct the record positionally without specifying it.
    TimeProvider? TimeProvider = null);
