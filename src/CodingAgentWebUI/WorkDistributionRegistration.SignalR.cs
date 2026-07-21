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
        // TODO: No ILeaderElectionService fallback registered in SignalR mode when connectionString
        // is null/empty. If SignalR mode is active without a DB connection string, any service that
        // resolves ILeaderElectionService will fail at runtime. Consider registering a no-op
        // implementation as a fallback (single-instance assumed).
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
            sp.GetRequiredService<ILabelService>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SignalRWorkDistributor>>(),
            sp.GetService<Pipeline.Interfaces.IRunLifecycleManager>(),
            sp.GetService<Pipeline.Interfaces.IAgentCancellationSender>()));

        // HeartbeatMonitorService remains registered (handled by AddOrchestrationServices)
        // Queue visibility: queries WorkItems table for Pending status
        services.AddSingleton<IPendingWorkQuery>(sp =>
            new DbPendingWorkQuery(sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>()));

        // PendingWorkItemDrainService: drains Pending WorkItems to idle agents
        services.AddSingleton<PendingWorkItemDrainService>(sp => new PendingWorkItemDrainService(
            sp.GetRequiredService<IDbContextFactory<PipelineDbContext>>(),
            sp.GetRequiredService<ISignalRWorkDistributorAgentResolver>(),
            sp.GetRequiredService<IAgentCommunication>(),
            sp.GetRequiredService<IOrchestratorRunService>(),
            sp.GetRequiredService<WorkItemTransitionService>(),
            sp.GetRequiredService<IPendingWorkQuery>(),
            sp.GetRequiredService<ILabelService>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<PendingWorkItemDrainService>>(),
            sp.GetService<IProjectStore>(),
            sp.GetRequiredService<IConsolidationDispatcher>(),
            sp.GetRequiredService<IConsolidationRunStore>()));
        services.AddHostedService(sp => sp.GetRequiredService<PendingWorkItemDrainService>());

        Log.Information("WorkDistribution: SignalR mode — SignalRWorkDistributor + PendingWorkItemDrainService registered");
    }
}
