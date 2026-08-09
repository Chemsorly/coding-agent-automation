using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Groups the core dependencies of <see cref="ConsolidationDispatchHandler"/> to reduce
/// constructor parameter count (S107). Optional members default to null.
/// </summary>
internal sealed record ConsolidationDispatchHandlerDependencies(
    IDbContextFactory<PipelineDbContext> DbFactory,
    ILeaderElectionService LeaderElection,
    DispatchLifecycleService Lifecycle,
    JobTemplateStore TemplateProvider,
    IConfiguration Configuration,
    WorkItemTransitionService TransitionService,
    IConsolidationRunStore? ConsolidationRunStore = null,
    IConsolidationService? ConsolidationService = null,
    IConsolidationJobPreparationService? ConsolidationJobPreparer = null,
    IPipelineConfigStore? PipelineConfigStore = null,
    IProjectStore? ProjectStore = null,
    IAgentProfileStore? AgentProfileStore = null);
