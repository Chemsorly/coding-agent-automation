using CodingAgentWebUI.Infrastructure.Resilience;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;
using Polly;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Agent;

/// <summary>
/// Extension methods for registering work-item-mode agent services.
/// Reached when the agent pod is started with <c>--work-item-id</c> (work-item mode).
/// Extracted from Program.cs to reduce top-level statement complexity (T23, arch-audit 2026-08-22).
/// Previously named <c>AgentK8SModeRegistration</c> — renamed because both modes use K8s and SignalR;
/// the essential difference is that this mode owns a durable WorkItem row.
/// </summary>
internal static class AgentWorkItemModeRegistration
{
    internal static IServiceCollection AddK8sModeServices(
        this IServiceCollection services,
        AgentStartupConfig config,
        ILogger logger)
    {
        services.AddHttpClient<WorkItemHttpClient>(client =>
        {
            client.BaseAddress = new Uri(config.OrchestratorUrl.TrimEnd('/'));
            // Derive per-agent key: HMAC(masterKey, agentId).
            // Must match what AgentApiKeyAuthHandler re-derives from ?agentId= on each request.
            var derivedKey = CodingAgentWebUI.Agent.HubConnectionManager.DeriveKey(config.AgentApiKey, config.AgentId.Value);
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", derivedKey);
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

        // Capture agentId at registration time; set it after the typed client is resolved.
        // Using AddSingleton with a factory so AgentId is embedded in the closure rather than
        // requiring DI to inject it (which would break the single-ctor contract for typed clients).
        // Note: AddHttpClient<WorkItemHttpClient> also registers a transient; this singleton wins
        // for GetRequiredService<WorkItemHttpClient>() because it is registered last.
        var agentIdValue = config.AgentId.Value;
        services.AddSingleton<WorkItemHttpClient>(sp =>
        {
            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var httpClient = factory.CreateClient(nameof(WorkItemHttpClient));
            var client = new WorkItemHttpClient(httpClient, Serilog.Log.Logger)
            {
                AgentId = agentIdValue // append ?agentId= to work-item API calls for HMAC key derivation
            };
            return client;
        });

        services.AddSingleton<IWorkItemExecutor>(sp => new WorkItemExecutorRouter(
            sp.GetRequiredService<IPipelineExecutor>(),
            sp.GetRequiredService<IConsolidationExecutor>(),
            logger));

        services.AddSingleton<IWorkItemLifecycleClient>(sp =>
            sp.GetRequiredService<WorkItemHttpClient>());

        services.AddSingleton<IAgentConnectionManager>(sp => new AgentConnectionManager(
            sp.GetRequiredService<IHubConnectionManager>(),
            sp.GetRequiredService<IHubConnectionManagerFactory>(),
            sp.GetRequiredService<AgentId>(),
            logger,
            sp.GetRequiredService<IHostApplicationLifetime>()));

        services.AddSingleton<IJobCompletionReporter>(sp => new HttpPrimaryCompletionReporter(
            config.WorkItemId!,
            sp.GetRequiredService<IWorkItemLifecycleClient>(),
            sp.GetRequiredService<IAgentConnectionManager>(),
            sp.GetRequiredService<AgentId>(),
            logger));

        services.AddSingleton(sp => new WorkItemAgentService(
            new WorkItemAgentServiceDependencies(
                WorkItemId: config.WorkItemId!,
                WorkItemClient: sp.GetRequiredService<IWorkItemLifecycleClient>(),
                ConnectionManager: sp.GetRequiredService<IAgentConnectionManager>(),
                WorkItemExecutor: sp.GetRequiredService<IWorkItemExecutor>(),
                CompletionReporter: sp.GetRequiredService<IJobCompletionReporter>(),
                AgentId: sp.GetRequiredService<AgentId>(),
                Lifetime: sp.GetRequiredService<IHostApplicationLifetime>(),
                Logger: logger,
                ServiceProvider: sp)));
        services.AddHostedService(sp => sp.GetRequiredService<WorkItemAgentService>());
        services.AddSingleton<IAgentService>(sp => sp.GetRequiredService<WorkItemAgentService>());

        return services;
    }
}
