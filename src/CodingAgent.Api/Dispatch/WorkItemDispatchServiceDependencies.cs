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
    // ProviderFactory + ProviderConfigStore + ProjectStore are used by the pre-dispatch eligibility gate.
    // Nullable/optional — when null, the gate is skipped (fail-open) for that check type.
    IProviderFactory? ProviderFactory = null,
    IProviderConfigStore? ProviderConfigStore = null,
    IProjectStore? ProjectStore = null);
