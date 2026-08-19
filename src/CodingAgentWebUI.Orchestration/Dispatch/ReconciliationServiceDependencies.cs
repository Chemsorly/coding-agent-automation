using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Pipeline.LeaderElection;
using CodingAgentWebUI.Pipeline.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CodingAgentWebUI.Orchestration.Dispatch;

/// <summary>
/// Groups the constructor dependencies of <see cref="ReconciliationService"/>
/// to reduce constructor parameter count (S107). Optional members default to null.
/// </summary>
public sealed record ReconciliationServiceDependencies(
    IDbContextFactory<PipelineDbContext> DbFactory,
    ILeaderElectionService LeaderElection,
    k8s.IKubernetes KubeClient,
    WorkItemTransitionService TransitionService,
    IConfiguration Configuration,
    ILabelService? LabelService = null,
    IRunLifecycleManager? LifecycleManager = null,
    IConsolidationService? ConsolidationService = null,
    IConfigurationStore? ConfigStore = null,
    IJobDeduplicationGuard? DedupGuard = null);
