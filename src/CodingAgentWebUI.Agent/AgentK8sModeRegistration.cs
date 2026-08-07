using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;
using Polly;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Extension methods for registering K8s-mode agent services.
/// Extracted from Program.cs to reduce top-level statement complexity.
/// </summary>
internal static class AgentK8SModeRegistration
{
    internal static IServiceCollection AddK8sModeServices(
        this IServiceCollection services,
        AgentStartupConfig config,
        ILogger logger)
    {
        services.AddHttpClient<WorkItemHttpClient>(client =>
        {
            client.BaseAddress = new Uri(config.OrchestratorUrl.TrimEnd('/'));
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AgentApiKey);
            // DO NOT set client.Timeout — resilience handler manages timeouts
        })
        .AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(90);
            options.Retry.MaxRetryAttempts = 5;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
        });

        services.AddSingleton<IWorkItemExecutor>(sp => new WorkItemExecutorRouter(
            sp.GetRequiredService<IPipelineExecutor>(),
            sp.GetRequiredService<IConsolidationExecutor>(),
            logger));

        services.AddSingleton<IWorkItemLifecycleClient>(sp =>
            sp.GetRequiredService<WorkItemHttpClient>());

        services.AddSingleton<IAgentConnectionManager>(sp => new AgentConnectionManager(
            sp.GetRequiredService<HubConnectionManager>(),
            sp.GetRequiredService<HubConnectionManagerFactory>(),
            sp.GetRequiredService<AgentId>(),
            logger));

        services.AddSingleton<IJobCompletionReporter>(sp => new HttpPrimaryCompletionReporter(
            config.WorkItemId!,
            sp.GetRequiredService<IWorkItemLifecycleClient>(),
            sp.GetRequiredService<IAgentConnectionManager>(),
            sp.GetRequiredService<AgentId>(),
            logger));

        services.AddSingleton(sp => new WorkItemAgentService(
            config.WorkItemId!,
            sp.GetRequiredService<IWorkItemLifecycleClient>(),
            sp.GetRequiredService<IAgentConnectionManager>(),
            sp.GetRequiredService<IWorkItemExecutor>(),
            sp.GetRequiredService<IJobCompletionReporter>(),
            sp.GetRequiredService<AgentId>(),
            sp.GetRequiredService<IHostApplicationLifetime>(),
            logger,
            serviceProvider: sp));
        services.AddHostedService(sp => sp.GetRequiredService<WorkItemAgentService>());
        services.AddSingleton<IAgentService>(sp => sp.GetRequiredService<WorkItemAgentService>());

        return services;
    }
}
