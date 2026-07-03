using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Infrastructure.GitLab;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Infrastructure.Persistence.Services;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Serilog;

namespace CodingAgentWebUI;

/// <summary>
/// Extension methods for registering domain services in the DI container.
/// Extracted from Program.cs to reduce file size and group related registrations.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers infrastructure services: configuration store interfaces, provider factory, and validation services.
    /// </summary>
    public static IServiceCollection AddInfrastructureServices(
        this IServiceCollection services,
        JsonConfigurationStore configStore,
        PipelineConfiguration pipelineConfig)
    {
        services.AddSingleton<IConfigurationStore>(configStore);
        services.AddSingleton<IPipelineConfigStore>(configStore);
        services.AddSingleton<IProviderConfigStore>(configStore);
        services.AddSingleton<IAgentProfileStore>(configStore);
        services.AddSingleton<IQualityGateConfigStore>(configStore);
        services.AddSingleton<IReviewerConfigStore>(configStore);
        services.AddSingleton<IProjectStore>(configStore);

        services.AddSingleton<IProviderFactory>(sp => new ProviderFactory(sp.GetRequiredService<IPipelineConfigStore>()));

        services.AddTransient<GitHubValidationService>(sp =>
            new GitHubValidationService(sp.GetRequiredService<IProviderFactory>()));
        services.AddTransient<GitLabValidationService>();

        return services;
    }

    /// <summary>
    /// Registers infrastructure services WITHOUT config store registrations.
    /// Used in DB mode where PostgresConfigurationStore is registered by AddWorkDistribution.
    /// </summary>
    public static IServiceCollection AddInfrastructureServicesWithoutConfigStore(
        this IServiceCollection services)
    {
        services.AddSingleton<IProviderFactory>(sp => new ProviderFactory(sp.GetRequiredService<IPipelineConfigStore>()));

        services.AddTransient<GitHubValidationService>(sp =>
            new GitHubValidationService(sp.GetRequiredService<IProviderFactory>()));
        services.AddTransient<GitLabValidationService>();

        return services;
    }

    /// <summary>
    /// Registers WebUI-specific pipeline services: orchestration, loop service, lifecycle, and history.
    /// </summary>
    public static IServiceCollection AddPipelineCoreServices(this IServiceCollection services)
    {
        services.AddSingleton<IOpenIssueContextWriter>(sp => new OpenIssueContextWriter(Log.Logger));
        services.AddSingleton<IPipelineRunHistoryService>(sp => new PipelineRunHistoryService(Log.Logger));

        services.AddSingleton(sp => new PipelineRunLifecycleService(
            sp.GetRequiredService<IPipelineRunHistoryService>(),
            sp.GetRequiredService<IOrchestratorRunService>(),
            Log.Logger));
        services.AddSingleton<Pipeline.Interfaces.ILifecycleShutdownAction>(sp =>
            sp.GetRequiredService<PipelineRunLifecycleService>());

        services.AddSingleton<IBrainSyncService>(sp => new BrainSyncService(
            sp.GetRequiredService<IBrainUpdateService>(), Log.Logger));

        services.AddSingleton<Pipeline.Interfaces.IPipelineExecutionFacade>(sp => new PipelineExecutionFacade(
            sp.GetRequiredService<IAgentPhaseExecutor>(),
            sp.GetRequiredService<IQualityGateExecutor>(),
            sp.GetRequiredService<IQualityGateValidator>(),
            sp.GetRequiredService<IBrainSyncService>()));

        services.AddSingleton<Pipeline.Interfaces.IPipelineCompletionFacade>(sp => new PipelineCompletionFacade(
            sp.GetRequiredService<PullRequestOrchestrator>(),
            sp.GetRequiredService<PullRequestFinalizationService>(),
            sp.GetRequiredService<FeedbackService>(),
            sp.GetRequiredService<IPipelineRunHistoryService>()));

        services.AddSingleton<Pipeline.Interfaces.IPipelineCancellationFacade>(sp => new PipelineCancellationFacade(
            sp.GetRequiredService<Pipeline.Interfaces.IJobDeduplicationGuard>(),
            sp.GetRequiredService<Pipeline.Interfaces.IAgentCancellationSender>()));

        services.AddSingleton(sp => new PipelineOrchestrationService(
            sp.GetRequiredService<IConfigurationStore>(),
            sp.GetRequiredService<IProviderFactory>(),
            sp.GetRequiredService<IssueDescriptionParser>(),
            sp.GetRequiredService<Pipeline.Interfaces.IPipelineExecutionFacade>(),
            sp.GetRequiredService<Pipeline.Interfaces.IPipelineCompletionFacade>(),
            sp.GetRequiredService<Pipeline.Interfaces.IPipelineCancellationFacade>(),
            sp.GetRequiredService<PipelineRunLifecycleService>(),
            sp.GetRequiredService<ILabelSwapper>(),
            Log.Logger));
        services.AddSingleton<Pipeline.Interfaces.IOrchestrationShutdownAction>(sp =>
            sp.GetRequiredService<PipelineOrchestrationService>());
        services.AddSingleton<Pipeline.Interfaces.IDispatchRunCreator>(sp =>
            sp.GetRequiredService<PipelineOrchestrationService>());

        // Shutdown signal: cooperative flag to prevent dispatch-during-shutdown races
        services.AddSingleton<Pipeline.Interfaces.IShutdownSignal>(new Pipeline.Services.ShutdownSignal());

        // Graceful shutdown via IHostedLifecycleService (async, 15s timeout, non-blocking)
        services.AddHostedService(sp => new ShutdownService(
            sp.GetRequiredService<Pipeline.Interfaces.ILifecycleShutdownAction>(),
            sp.GetRequiredService<Pipeline.Interfaces.IOrchestrationShutdownAction>(),
            sp.GetRequiredService<Pipeline.Interfaces.IShutdownSignal>(),
            Log.Logger));

        // Readiness drain: marks /readyz as 503 during shutdown, then waits for endpoint removal.
        // Registered AFTER ShutdownService — StoppingAsync fires in REVERSE order,
        // so drain runs FIRST (flips readiness, waits), THEN ShutdownService cancels work.
        services.AddSingleton<ReadinessState>();
        services.AddHostedService(sp => new ReadinessDrainService(
            sp.GetRequiredService<ReadinessState>(),
            Log.Logger));

        services.AddHostedService(sp => new OrphanedLabelRecoveryService(
            sp.GetRequiredService<IOrchestratorRunService>(),
            sp.GetRequiredService<IProjectStore>(),
            sp.GetRequiredService<IProviderConfigStore>(),
            sp.GetRequiredService<IProviderFactory>(),
            sp.GetRequiredService<ILabelSwapper>(),
            Log.Logger));

        services.AddSingleton<IDependencyChecker>(sp => new DependencyChecker(Log.Logger));
        services.AddSingleton<PipelineLoopService>(sp => new PipelineLoopService(
            sp.GetRequiredService<IDispatchRunCreator>(),
            sp.GetRequiredService<IProviderFactory>(),
            sp.GetRequiredService<IPipelineConfigStore>(),
            sp.GetRequiredService<IProviderConfigStore>(),
            sp.GetRequiredService<IProjectStore>(),
            Log.Logger,
            sp.GetService<IWorkDistributor>(),
            sp.GetService<IDispatchOrchestrationService>(),
            sp.GetRequiredService<IDependencyChecker>()));
        services.AddSingleton<IPipelineLoopService>(sp => sp.GetRequiredService<PipelineLoopService>());
        services.AddHostedService(sp => sp.GetRequiredService<PipelineLoopService>());

        // Loop state persistence: auto-resumes loop after pod restart if previously active
        services.AddSingleton(sp => new LoopStatePersistenceService(
            sp.GetRequiredService<IPipelineLoopService>(),
            Log.Logger,
            sp.GetRequiredService<ILoopStateStore>()));
        services.AddHostedService(sp => sp.GetRequiredService<LoopStatePersistenceService>());

        services.AddTransient<IssueDescriptionParser>();

        return services;
    }

    /// <summary>
    /// Registers multi-agent orchestration services: agent registry, job dispatch, token vending,
    /// heartbeat monitoring, and the AgentHub facade.
    /// </summary>
    public static IServiceCollection AddOrchestrationServices(
        this IServiceCollection services,
        PipelineConfiguration pipelineConfig,
        string? workDistributionMode = null)
    {
        services.AddSingleton(sp => new AgentRegistryService(Log.Logger));
        services.AddSingleton<IAgentRegistryService>(sp => sp.GetRequiredService<AgentRegistryService>());
        services.AddSingleton(sp => new JobDispatcherService(
            sp.GetRequiredService<IAgentRegistryService>(),
            Log.Logger));
        services.AddSingleton<Pipeline.Interfaces.IJobDeduplicationGuard>(sp =>
            sp.GetRequiredService<JobDispatcherService>());

        services.AddHttpClient("TokenVending");
        services.AddSingleton<ITokenVendingService>(sp => new TokenVendingService(Log.Logger, sp.GetRequiredService<IHttpClientFactory>()));

        services.AddSingleton(sp => new OrchestratorRunService(
            Log.Logger,
            pipelineConfig.OutputBufferCapacity));
        services.AddSingleton<IOrchestratorRunService>(sp => sp.GetRequiredService<OrchestratorRunService>());

        services.AddSingleton<ILabelSwapper>(sp => new LabelSwapper(
            sp.GetRequiredService<IConfigurationStore>(),
            sp.GetRequiredService<IProviderFactory>(),
            Log.Logger));

        // HeartbeatMonitorService: registered in Legacy and SignalR modes only.
        // In K8s mode, agent liveness is handled by ReconciliationService.
        var isKubernetesMode = string.Equals(workDistributionMode, "Kubernetes", StringComparison.OrdinalIgnoreCase);
        if (!isKubernetesMode)
        {
            services.AddHostedService(sp => new HeartbeatMonitorService(
                sp.GetRequiredService<IAgentRegistryService>(),
                sp.GetRequiredService<IOrchestratorRunService>(),
                sp.GetRequiredService<IPipelineRunHistoryService>(),
                sp.GetRequiredService<JobDispatcherService>(),
                sp.GetRequiredService<ILabelSwapper>(),
                sp.GetRequiredService<IConfigurationStore>(),
                Log.Logger,
                sp.GetService<IConsolidationService>(),
                sp.GetRequiredService<IRunLifecycleManager>()));
        }

        // JobQueueDrainService: registered as singleton always (AgentHubFacade depends on it),
        // but only registered as hosted service (active background loop) in Legacy mode.
        // In DB modes (SignalR/K8s), work distribution via IWorkDistributor — in-memory queue unused.
        services.AddSingleton(sp => new JobQueueDrainService(
            sp.GetRequiredService<JobDispatcherService>(),
            sp.GetRequiredService<IAgentRegistryService>(),
            sp.GetRequiredService<IJobDispatcher>(),
            sp.GetRequiredService<IConfigurationStore>(),
            sp.GetRequiredService<ConsolidationQueueService>(),
            sp.GetRequiredService<IConsolidationService>(),
            sp.GetRequiredService<IConsolidationDispatcher>(),
            sp.GetRequiredService<Pipeline.Interfaces.IShutdownSignal>(),
            Log.Logger));
        var hasDatabase = !string.IsNullOrEmpty(workDistributionMode);
        if (!hasDatabase)
        {
            services.AddHostedService(sp => sp.GetRequiredService<JobQueueDrainService>());
        }

        services.AddSingleton<ProfileResolver>();
        services.AddSingleton<QualityGateResolver>();
        services.AddSingleton<ReviewerResolver>();

        services.AddSingleton(sp => new DispatchResolutionService(
            sp.GetRequiredService<ProfileResolver>(),
            sp.GetRequiredService<QualityGateResolver>(),
            sp.GetRequiredService<ReviewerResolver>(),
            sp.GetRequiredService<IConfigurationStore>(),
            Log.Logger));

        services.AddSingleton(sp => new DispatchInfrastructure(
            sp.GetRequiredService<ITokenVendingService>(),
            sp.GetRequiredService<IProviderFactory>(),
            sp.GetRequiredService<ILabelSwapper>(),
            sp.GetRequiredService<DispatchResolutionService>()));

        services.AddSingleton<IAgentCommunication>(sp => new SignalRAgentCommunication(
            sp.GetRequiredService<IHubContext<AgentHub, IAgentHubClient>>()));

        services.AddSingleton<Pipeline.Interfaces.IAgentCancellationSender>(sp => new AgentCancellationSender(
            sp.GetRequiredService<IAgentRegistryService>(),
            sp.GetRequiredService<IAgentCommunication>(),
            Log.Logger));

        services.AddSingleton<ModelFetchService>(sp => new ModelFetchService(
            sp.GetRequiredService<AgentRegistryService>(),
            sp.GetRequiredService<IAgentCommunication>(),
            Log.Logger));

        // AgentJobDispatcher: registered as singleton (internal class).
        // Consumed by JobQueueDrainService and LegacyWorkDistributor within the same assembly scope.
        services.AddSingleton<IJobDispatcher>(sp => new AgentJobDispatcher(
            sp.GetRequiredService<JobDispatcherService>(),
            sp.GetRequiredService<IAgentRegistryService>(),
            sp.GetRequiredService<IOrchestratorRunService>(),
            sp.GetRequiredService<Pipeline.Interfaces.IDispatchRunCreator>(),
            sp.GetRequiredService<DispatchInfrastructure>(),
            sp.GetRequiredService<IAgentCommunication>(),
            sp.GetRequiredService<Pipeline.Interfaces.IShutdownSignal>(),
            Log.Logger,
            sp.GetService<IRunLifecycleManager>()));

        services.AddSingleton<IAgentHubFacade>(sp => new AgentHubFacade(
            sp.GetRequiredService<AgentRegistryService>(),
            sp.GetRequiredService<OrchestratorRunService>(),
            sp.GetRequiredService<JobDispatcherService>(),
            sp.GetRequiredService<JobQueueDrainService>(),
            sp.GetRequiredService<IPipelineRunHistoryService>(),
            sp.GetRequiredService<IConfigurationStore>(),
            sp.GetRequiredService<IProviderFactory>(),
            sp.GetRequiredService<ILogger<AgentHubFacade>>(),
            sp.GetService<WorkItemTransitionService>(),
            sp.GetService<PendingWorkItemDrainService>()));

        return services;
    }

    /// <summary>
    /// Registers consolidation services: queue, dispatcher, service, and badge service.
    /// </summary>
    public static IServiceCollection AddConsolidationServices(
        this IServiceCollection services,
        PipelineConfiguration pipelineConfig)
    {
        services.AddSingleton<ConsolidationQueueService>(sp => new ConsolidationQueueService(Log.Logger));

        services.AddSingleton<IConsolidationDispatcher>(sp => new ConsolidationDispatcher(
            sp.GetRequiredService<AgentRegistryService>(),
            sp.GetRequiredService<JobDispatcherService>(),
            sp.GetRequiredService<IAgentCommunication>(),
            sp.GetRequiredService<IConfigurationStore>(),
            sp.GetRequiredService<IProjectStore>(),
            sp.GetRequiredService<ITokenVendingService>(),
            pipelineConfig,
            sp.GetRequiredService<ConsolidationQueueService>(),
            sp.GetRequiredService<IPipelineRunHistoryService>(),
            Log.Logger,
            sp.GetRequiredService<IConsolidationRunStore>()));

        services.AddSingleton<IConsolidationService>(sp => new ConsolidationService(
            Log.Logger,
            pipelineConfig,
            sp.GetRequiredService<IProjectStore>(),
            sp.GetRequiredService<IPipelineRunHistoryService>(),
            sp.GetRequiredService<IConsolidationRunStore>(),
            sp.GetRequiredService<IHarnessSuggestionStore>(),
            sp.GetRequiredService<IConsolidationDispatcher>()));

        services.AddSingleton<ConsolidationBadgeService>();

        return services;
    }
}
