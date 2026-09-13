using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.LeaderElection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace CodingAgent.Api.Dispatch;

/// <summary>
/// Groups the core dependencies of <see cref="WorkItemDispatchService"/> to keep
/// the constructor parameter count below the S107 limit.
/// </summary>
internal sealed record WorkItemDispatchServiceDependencies(
    IDbContextFactory<PipelineDbContext> DbFactory,
    ILeaderElectionService LeaderElection,
    DispatchLifecycleService Lifecycle,
    JobTemplateStore TemplateProvider,
    IConfiguration Configuration,
    WorkItemTransitionService TransitionService,
    // StateBuilder is required — always provided via GetRequiredService in production.
    DispatchStateBuilder StateBuilder,
    // Optional: when non-null, a pre-dispatch eligibility gate runs before K8s Job creation.
    // When null (e.g. tests that do not wire providers), the gate is skipped — fail-open.
    IProviderConfigStore? ProviderConfigStore = null,
    IProviderFactory? ProviderFactory = null);
