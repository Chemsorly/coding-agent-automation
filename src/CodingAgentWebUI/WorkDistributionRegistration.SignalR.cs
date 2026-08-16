using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.LeaderElection;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;

namespace CodingAgentWebUI;

public static partial class WorkDistributionRegistration
{
    private static void RegisterSignalRMode(IServiceCollection services, IConfiguration configuration)
    {
        // No K8s Jobs in SignalR mode — register no-op cleanup strategy
        services.AddSingleton<IJobCleanupStrategy>(new NoOpJobCleanup());

        // ── Postgres advisory lock leader election (multi-replica safety) ────
        // When a connection string is available, use PostgresLeaderElectionService for
        // multi-replica safety. Without one, fall back to AlwaysLeaderElectionService —
        // a no-op that treats the single instance as always-leader. This branch is a
        // defensive guard: AddWorkDistribution currently routes to RegisterLegacyMode when
        // connectionString is null/empty, so this else path cannot be reached today through
        // the normal registration path. It exists to prevent a silent startup crash if that
        // routing logic is ever changed or if RegisterSignalRMode is called directly.
        var connectionString = Services.DatabaseConnectionResolver.Resolve(configuration);
        if (!string.IsNullOrEmpty(connectionString))
        {
            services.Configure<PostgresLeaderElectionOptions>(
                configuration.GetSection(PostgresLeaderElectionOptions.SectionName));
            services.AddSingleton<PostgresLeaderElectionService>(sp =>
                new PostgresLeaderElectionService(
                    connectionString,
                    sp.GetRequiredService<IOptions<PostgresLeaderElectionOptions>>()));
            services.AddSingleton<ILeaderElectionService>(sp =>
                sp.GetRequiredService<PostgresLeaderElectionService>());
            services.AddHostedService(sp => sp.GetRequiredService<PostgresLeaderElectionService>());
        }
        else
        {
            // Single-instance fallback: no Postgres advisory lock, always reports as leader.
            services.AddSingleton<ILeaderElectionService>(new AlwaysLeaderElectionService());
        }

        // Agent resolver (singleton — selects idle label-compatible agent for SignalR push)
        services.AddSingleton<ISignalRWorkDistributorAgentResolver>(sp => new SignalRWorkDistributorAgentResolver(
            sp.GetRequiredService<AgentRegistryService>(),
            sp.GetRequiredService<JobDeduplicationGuardService>()));

        // Work distributor (singleton — uses IDbContextFactory for context-per-operation)
        services.AddSingleton<IWorkDistributor>(sp => new SignalRWorkDistributor(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetRequiredService<IAgentCommunication>(),
            sp.GetRequiredService<WorkItemTransitionService>(),
            sp.GetRequiredService<ISignalRWorkDistributorAgentResolver>(),
            sp.GetRequiredService<IOrchestratorRunService>(),
            sp.GetRequiredService<IProjectStore>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SignalRWorkDistributor>>(),
            sp.GetService<Pipeline.Interfaces.IRunLifecycleManager>(),
            sp.GetService<Pipeline.Interfaces.IAgentCancellationSender>()));

        // HeartbeatMonitorService remains registered (handled by AddOrchestrationServices)
        // Queue visibility: queries WorkItems table for Pending status
        services.AddSingleton<IPendingWorkQuery>(sp =>
            new DbPendingWorkQuery(sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()));

        // PendingWorkItemDrainService: drains Pending WorkItems to idle agents
        // LabelSwapService is a singleton; ILabelService must also be singleton (it is, via AddSingleton
        // in IssueProviderRegistration). maxAttempts=3: one initial + two retries with exponential backoff. (#1868)
        services.AddSingleton<ILabelSwapService>(sp => new LabelSwapService(
            sp.GetRequiredService<ILabelService>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LabelSwapService>>(),
            maxAttempts: 3));
        services.AddSingleton<DispatchRevertService>(sp => new DispatchRevertService(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetRequiredService<ISignalRWorkDistributorAgentResolver>(),
            sp.GetRequiredService<IOrchestratorRunService>(),
            sp.GetRequiredService<WorkItemTransitionService>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<DispatchRevertService>>()));
        // TODO: DispatchAttemptService is constructed with `new` here (and separately inside
        // PendingWorkItemDrainService's constructor), producing two unregistered instances at runtime.
        // Since the class is currently stateless this is not a correctness defect, but it departs from
        // the DI singleton-sharing guarantee. If DispatchAttemptService ever acquires state, the two
        // instances would diverge. Consider registering DispatchAttemptService as a singleton
        // (services.AddSingleton<DispatchAttemptService>()) and resolving it via
        // sp.GetRequiredService<DispatchAttemptService>() in both places.
        services.AddSingleton<IConsolidationDrainDispatcher>(sp => new ConsolidationDrainDispatcher(
            sp.GetRequiredService<IConsolidationDispatchService>(),
            sp.GetRequiredService<IConsolidationRunStore>(),
            new DispatchAttemptService(
                sp.GetRequiredService<WorkItemTransitionService>(),
                sp.GetRequiredService<DispatchRevertService>()),
            sp.GetRequiredService<WorkItemTransitionService>(),
            sp.GetRequiredService<ISignalRWorkDistributorAgentResolver>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ConsolidationDrainDispatcher>>()));
        services.AddSingleton<PendingWorkItemDrainService>(sp => new PendingWorkItemDrainService(
            new DrainServiceDependencies(
                sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
                sp.GetRequiredService<ISignalRWorkDistributorAgentResolver>(),
                sp.GetRequiredService<IAgentCommunication>(),
                sp.GetRequiredService<IOrchestratorRunService>(),
                sp.GetRequiredService<WorkItemTransitionService>(),
                sp.GetRequiredService<IPendingWorkQuery>(),
                sp.GetRequiredService<ILabelSwapService>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PendingWorkItemDrainService>>(),
                sp.GetRequiredService<DispatchRevertService>()),
            sp.GetService<IProjectStore>(),
            sp.GetRequiredService<IConsolidationDrainDispatcher>()));
        services.AddHostedService(sp => sp.GetRequiredService<PendingWorkItemDrainService>());

        Log.Information("WorkDistribution: SignalR mode — SignalRWorkDistributor + PendingWorkItemDrainService registered");

        // IChatJobDispatcher — null-object pattern; AgentChat.razor injects unconditionally
        services.AddSingleton<IChatJobDispatcher, NullChatJobDispatcher>();
    }
}