using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Pipeline.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Groups the core (non-config) constructor dependencies of <see cref="DispatchService"/>
/// to reduce constructor parameter count (S107). Optional members default to null.
/// </summary>
internal sealed record DispatchServiceCoreDependencies(
    IDbContextFactory<PipelineDbContext> DbFactory,
    LeaderElection.ILeaderElectionService LeaderElection,
    DispatchLifecycleService Lifecycle,
    ILabelService? LabelService = null,
    IAgentProfileStore? AgentProfileStore = null,
    IOrchestratorRunService? RunService = null);
