using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.LeaderElection;
using CodingAgent.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CodingAgent.Api.Dispatch;

/// <summary>
/// Groups the core dependencies of <see cref="ConsolidationWorkItemDispatchService"/> to reduce
/// constructor parameter count (S107). Optional members default to null.
/// </summary>
internal sealed record ConsolidationWorkItemDispatchServiceDependencies(
    IDbContextFactory<PipelineDbContext> DbFactory,
    ILeaderElectionService LeaderElection,
    DispatchLifecycleService Lifecycle,
    JobTemplateStore TemplateProvider,
    IConfiguration Configuration,
    WorkItemTransitionService TransitionService,
    // StateBuilder has a null-guard in the ConsolidationWorkItemDispatchService constructor.
    // Production always provides it via GetRequiredService. Optional only for legacy test paths
    // that test pre-StateBuilder behavior.
    IConsolidationRunStore? ConsolidationRunStore = null,
    IConsolidationService? ConsolidationService = null,
    IConsolidationJobPreparationService? ConsolidationJobPreparer = null,
    IProjectStore? ProjectStore = null,
    IAgentProfileStore? AgentProfileStore = null,
    DispatchStateBuilder? StateBuilder = null);
