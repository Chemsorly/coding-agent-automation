using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Infrastructure.GitLab;
using CodingAgentWebUI.Infrastructure.Persistence;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.SignalR;
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

        services.AddSingleton<IProviderFactory>(sp => new ProviderFactory(pipelineConfig));

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
            sp.GetRequiredService<OrchestratorRunService>(),
            Log.Logger));

        services.AddSingleton(sp => new PipelineOrchestrationService(
            sp.GetRequiredService<IConfigurationStore>(),
            sp.GetRequiredService<IProviderFactory>(),
            sp.GetRequiredService<IssueDescriptionParser>(),
            sp.GetRequiredService<IAgentPhaseExecutor>(),
            sp.GetRequiredService<IQualityGateExecutor>(),
            Log.Logger,
            sp.GetRequiredService<IBrainUpdateService>(),
            sp.GetRequiredService<IPipelineRunHistoryService>(),
            sp.GetRequiredService<OrchestratorRunService>(),
            sp.GetRequiredService<PipelineRunLifecycleService>(),
            sp.GetRequiredService<IQualityGateValidator>(),
            sp.GetRequiredService<ILabelSwapper>()));

        services.AddSingleton<IDependencyChecker>(sp => new DependencyChecker(Log.Logger));
        services.AddSingleton<PipelineLoopService>(sp => new PipelineLoopService(
            sp.GetRequiredService<PipelineOrchestrationService>(),
            sp.GetRequiredService<IProviderFactory>(),
            sp.GetRequiredService<IPipelineConfigStore>(),
            sp.GetRequiredService<IProviderConfigStore>(),
            sp.GetRequiredService<IProjectStore>(),
            Log.Logger,
            sp.GetRequiredService<IJobDispatcher>(),
            sp.GetRequiredService<IDependencyChecker>()));
        services.AddHostedService(sp => sp.GetRequiredService<PipelineLoopService>());

        services.AddTransient<IssueDescriptionParser>();

        return services;
    }

    /// <summary>
    /// Registers multi-agent orchestration services: agent registry, job dispatch, token vending,
    /// heartbeat monitoring, and the AgentHub facade.
    /// </summary>
    public static IServiceCollection AddOrchestrationServices(
        this IServiceCollection services,
        PipelineConfiguration pipelineConfig)
    {
        services.AddSingleton(sp => new AgentRegistryService(Log.Logger));
        services.AddSingleton(sp => new JobDispatcherService(
            sp.GetRequiredService<AgentRegistryService>(),
            Log.Logger));

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

        services.AddHostedService(sp => new HeartbeatMonitorService(
            sp.GetRequiredService<AgentRegistryService>(),
            sp.GetRequiredService<OrchestratorRunService>(),
            sp.GetRequiredService<IPipelineRunHistoryService>(),
            sp.GetRequiredService<JobDispatcherService>(),
            sp.GetRequiredService<ILabelSwapper>(),
            sp.GetRequiredService<IConfigurationStore>(),
            Log.Logger));

        services.AddSingleton(sp => new JobQueueDrainService(
            sp.GetRequiredService<JobDispatcherService>(),
            sp.GetRequiredService<AgentRegistryService>(),
            sp.GetRequiredService<IJobDispatcher>(),
            sp.GetRequiredService<ConsolidationQueueService>(),
            sp.GetRequiredService<IConsolidationService>(),
            sp.GetRequiredService<IConsolidationDispatcher>(),
            Log.Logger));
        services.AddHostedService(sp => sp.GetRequiredService<JobQueueDrainService>());

        services.AddSingleton<ProfileResolver>();
        services.AddSingleton<QualityGateResolver>();
        services.AddSingleton<ReviewerResolver>();

        services.AddSingleton<IAgentCommunication>(sp => new SignalRAgentCommunication(
            sp.GetRequiredService<IHubContext<AgentHub, IAgentHubClient>>()));

        services.AddSingleton<ModelFetchService>(sp => new ModelFetchService(
            sp.GetRequiredService<AgentRegistryService>(),
            sp.GetRequiredService<IAgentCommunication>(),
            Log.Logger));

        services.AddSingleton<IJobDispatcher>(sp => new AgentJobDispatcher(
            sp.GetRequiredService<JobDispatcherService>(),
            sp.GetRequiredService<AgentRegistryService>(),
            sp.GetRequiredService<OrchestratorRunService>(),
            sp.GetRequiredService<PipelineOrchestrationService>(),
            sp.GetRequiredService<ITokenVendingService>(),
            sp.GetRequiredService<IConfigurationStore>(),
            sp.GetRequiredService<IProviderFactory>(),
            sp.GetRequiredService<ILabelSwapper>(),
            sp.GetRequiredService<ProfileResolver>(),
            sp.GetRequiredService<QualityGateResolver>(),
            sp.GetRequiredService<ReviewerResolver>(),
            sp.GetRequiredService<IAgentCommunication>(),
            Log.Logger));

        services.AddSingleton<IAgentHubFacade>(sp => new AgentHubFacade(
            sp.GetRequiredService<AgentRegistryService>(),
            sp.GetRequiredService<OrchestratorRunService>(),
            sp.GetRequiredService<JobDispatcherService>(),
            sp.GetRequiredService<JobQueueDrainService>(),
            sp.GetRequiredService<IPipelineRunHistoryService>(),
            sp.GetRequiredService<IConfigurationStore>(),
            sp.GetRequiredService<IProviderFactory>()));

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
            Log.Logger));

        services.AddSingleton<IConsolidationService>(sp => new ConsolidationService(
            Log.Logger,
            pipelineConfig,
            sp.GetRequiredService<IProjectStore>(),
            sp.GetRequiredService<IPipelineRunHistoryService>(),
            sp.GetRequiredService<IConsolidationDispatcher>()));

        services.AddSingleton<ConsolidationBadgeService>();

        return services;
    }
}
