using CodingAgent.Infrastructure.Resilience;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using KiroCliLib.Core;
using Polly;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Agent;

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
            // The pre-vended per-job key (HMAC(master, jobName)) is already in config.AgentApiKey.
            // No local derivation — AgentApiKeyAuthHandler re-derives HMAC(master, ?agentId=) server-side
            // and compares, so presenting the pre-vended value directly matches.
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", config.AgentApiKey);
            // DO NOT set client.Timeout — resilience handler manages timeouts
        })
        .AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(90);
            // AttemptTimeout must exceed the server-side "TokenVending" TotalRequestTimeout (30s)
            // so the server can complete its full retry ladder before the agent cancels the request.
            // Prior to this fix, the default 10s AttemptTimeout caused HttpContext.RequestAborted
            // to fire mid-retry, which was laundered into "Aborting dispatch" (issue #2575).
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(35);
            options.Retry.MaxRetryAttempts = 5;
            options.Retry.BackoffType = DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;
            // SamplingDuration must be >= 2 * AttemptTimeout per Polly validation rules.
            // With AttemptTimeout = 35s, minimum is 70s.
            // TODO [WARNING]: Raising SamplingDuration from 30s to 70s increases the observation window
            // required before the circuit can open. With MaxRetryAttempts = 5 and exponential backoff,
            // a sustained failure period may take several minutes to accumulate enough throughput to trip
            // the circuit (absent a MinimumThroughput adjustment). The circuit breaker may be slower to
            // open and slower to recover during a real incident. Consider also adjusting
            // CircuitBreaker.MinimumThroughput and BreakDuration to preserve the original responsiveness
            // intent. The optional per-installation circuit breaker mentioned in issue #2575 was not
            // implemented; this change makes the existing one less aggressive. (.NET Specialist warning)
            options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(70);
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
