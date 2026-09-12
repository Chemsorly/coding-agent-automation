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
    DispatchStateBuilder StateBuilder)
{
    /// <summary>
    /// Optional provider factory for the pre-dispatch eligibility gate.
    /// When null, the gate is skipped (fail-open). Inject in production to enable the
    /// pre-dispatch eligibility re-check that cancels Pending items before K8s Job creation.
    /// </summary>
    public IProviderFactory? ProviderFactory { get; init; }

    /// <summary>
    /// Optional provider config store for the pre-dispatch eligibility gate.
    /// Required when <see cref="ProviderFactory"/> is set.
    /// When null, the gate is skipped (fail-open).
    /// </summary>
    public IProviderConfigStore? ProviderConfigStore { get; init; }

    /// <summary>
    /// Optional project store for the pre-dispatch eligibility gate.
    /// Used to load templates so that Review items are checked against the correct
    /// repository provider (matched by IssueProviderId) rather than an arbitrary first entry.
    /// When null, the gate falls back to the first available repo config (fail-open if ambiguous).
    /// </summary>
    public IProjectStore? ProjectStore { get; init; }
}
