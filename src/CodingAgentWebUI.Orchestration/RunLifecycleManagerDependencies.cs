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
    // TODO: WorkItemTransition is no longer consumed by RunLifecycleManager — only WorkItemFallbackTransition is used.
    // This dead parameter is kept for backward compatibility with callers (WorkDistributionRegistration still passes it).
    // Remove once all callers are updated. If WorkItemTransition is non-null but WorkItemFallbackTransition is null,
    // all DB transitions will silently be skipped — callers should pass WorkItemFallbackTransition in DB mode.
    Infrastructure.Persistence.Services.WorkItemTransitionService? WorkItemTransition = null,
    IJobCleanupStrategy? JobCleanup = null,
    IWorkItemFallbackTransitionService? WorkItemFallbackTransition = null);
