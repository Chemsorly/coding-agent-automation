using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;

namespace CodingAgent.Orchestration;

/// <summary>
/// Groups the constructor dependencies of <see cref="RunLifecycleManager"/>
/// to reduce constructor parameter count (S107). Optional members default to null.
/// </summary>
public sealed record RunLifecycleManagerDependencies(
    IOrchestratorRunService RunService,
    IPipelineRunHistoryService HistoryService,
    IAgentRegistryService Registry,
    ILabelService LabelService,
    Serilog.ILogger Logger,
    IJobCleanupStrategy? JobCleanup = null,
    IWorkItemFallbackTransitionService? WorkItemFallbackTransition = null);
