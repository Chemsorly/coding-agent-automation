using CodingAgent.Infrastructure.Persistence;
using CodingAgent.Infrastructure.Persistence.Services;
using CodingAgent.Kubernetes;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.LeaderElection;
using CodingAgent.Pipeline.Models;
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
    // Optional: used by the pre-dispatch eligibility gate. When null, the gate is skipped.
    IProviderFactory? ProviderFactory = null,
    IProviderConfigStore? ProviderConfigStore = null);
