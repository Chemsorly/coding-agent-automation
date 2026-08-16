using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;

namespace CodingAgentWebUI.Orchestration;

/// <summary>
/// Groups the constructor dependencies of <see cref="RunLifecycleManager"/>
/// to reduce constructor parameter count (S107). Optional members default to null.
/// </summary>
public sealed record RunLifecycleManagerDependencies(
    IOrchestratorRunService RunService,
    IPipelineRunHistoryService HistoryService,
    Registry.AgentRegistryService Registry,
    ILabelService LabelService,
    JobDeduplicationGuardService Dispatcher,
    Serilog.ILogger Logger,
    Infrastructure.Persistence.Services.WorkItemTransitionService? WorkItemTransition = null,
    IJobCleanupStrategy? JobCleanup = null,
    IWorkItemFallbackTransitionService? WorkItemFallbackTransition = null);
