using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;

namespace CodingAgent.Api.Client;

/// <summary>
/// Extension methods for registering all Pipeline API clients.
/// </summary>
public static class PipelineApiClientServiceCollectionExtensions
{
    private const string BearerScheme = "Bearer";
    /// <summary>
    /// Registers all typed HTTP clients, the <see cref="IAgentHubConnection"/> factory,
    /// and the <see cref="PipelineApiClientOptions"/> singleton.
    /// </summary>
    /// <exception cref="ArgumentException">Thrown when <see cref="PipelineApiClientOptions.AgentApiKey"/> is null or empty.</exception>
    public static IServiceCollection AddPipelineApiClient(
        this IServiceCollection services,
        PipelineApiClientOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.IsNullOrEmpty(options.AgentApiKey))
            throw new ArgumentException("AgentApiKey must not be null or empty.", nameof(options));

        services.AddSingleton(options);

        // ── Shared HTTP resilience defaults ─────────────────────────────────────
        // Sets PooledConnectionLifetime so stale connections to replaced pod IPs (after a
        // rolling update) are recycled within 90s rather than persisting until the OS closes them.
        // Without this, clients keep TCP connections to old pod IPs that return "Connection refused".
        // Applied via ConfigureHttpClientDefaults so it covers every client registered below.
        //
        // NOTE: resilience options (CircuitBreaker.MinimumThroughput) are tuned per-client below
        // via AddStandardResilienceHandler(options => ...) rather than via ConfigureHttpClientDefaults
        // to avoid stacking a second pipeline when hosts call AddPipelineApiClient more than once
        // in the same DI container (e.g. WorkDistributionRegistration.Consolidation.cs).
        // TODO [WARNING]: No test verifies PooledConnectionLifetime = 90s or CircuitBreaker.MinimumThroughput = 10
        // for these clients. A DI-resolution test asserting HttpStandardResilienceOptions.CircuitBreaker.MinimumThroughput
        // and SocketsHttpHandler.PooledConnectionLifetime would prevent silent regression. See test
        // quality review finding [WARNING] in review-findings.md.
        services.ConfigureHttpClientDefaults(b =>
        {
            b.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromSeconds(90)
            });
        });

        // Work items client — authenticated
        services.AddHttpClient<IPipelineApiWorkItemClient, PipelineApiWorkItemClient>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(BearerScheme, options.AgentApiKey);
        }).AddStandardResilienceHandler(o => o.CircuitBreaker.MinimumThroughput = 10);

        // Run history client — authenticated. /api/pipeline-runs requires the OPERATOR policy
        // after W0-04. Only works when AgentApiKey is the master key (orchestrator process).
        // Per-pod derived keys receive HTTP 403 — same constraint as IPipelineApiAgentClient above.
        services.AddHttpClient<IPipelineApiRunHistoryClient, PipelineApiRunHistoryClient>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(BearerScheme, options.AgentApiKey);
        }).AddStandardResilienceHandler(o => o.CircuitBreaker.MinimumThroughput = 10);

        // Config client — authenticated
        services.AddHttpClient<IPipelineApiConfigClient, PipelineApiConfigClient>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(BearerScheme, options.AgentApiKey);
        }).AddStandardResilienceHandler(o => o.CircuitBreaker.MinimumThroughput = 10);

        // Agent registry client — authenticated. /api/agents requires the OPERATOR policy.
        // Only works when AgentApiKey is the master key (monolith and Job Controller).
        // Per-pod derived keys receive HTTP 403 from this endpoint.
        services.AddHttpClient<IPipelineApiAgentClient, PipelineApiAgentClient>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(BearerScheme, options.AgentApiKey);
        }).AddStandardResilienceHandler(o => o.CircuitBreaker.MinimumThroughput = 10);

        // Health client — no auth (healthz/readyz are anonymous)
        services.AddHttpClient<IPipelineApiHealthClient, PipelineApiHealthClient>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
        }).AddStandardResilienceHandler(o => o.CircuitBreaker.MinimumThroughput = 10);

        // Consolidation run client — authenticated (operator tier; master key required)
        services.AddHttpClient<IPipelineApiConsolidationRunClient, PipelineApiConsolidationRunClient>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(BearerScheme, options.AgentApiKey);
        }).AddStandardResilienceHandler(o => o.CircuitBreaker.MinimumThroughput = 10);

        // Harness suggestion client — authenticated (operator tier; master key required)
        services.AddHttpClient<IPipelineApiHarnessSuggestionClient, PipelineApiHarnessSuggestionClient>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(BearerScheme, options.AgentApiKey);
        }).AddStandardResilienceHandler(o => o.CircuitBreaker.MinimumThroughput = 10);

        // Chat client — authenticated (operator tier; master key required).
        // Chat pod dispatch blocks until the pod connects (up to ChatPodConnectTimeoutSeconds),
        // so the default 30 s HttpClient timeout is too short — set it generously above the
        // maximum pod connect timeout (default 120 s) to avoid a client-side cancellation
        // racing the API's own timeout handling.
        services.AddHttpClient<IPipelineApiChatClient, PipelineApiChatClient>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(BearerScheme, options.AgentApiKey);
            client.Timeout = TimeSpan.FromMinutes(5);
        });

        // Hub connection — transient so each caller owns its own connection lifecycle
        services.AddTransient<IAgentHubConnection>(_ =>
            new AgentHubConnection($"{options.BaseUrl.TrimEnd('/')}/hubs/agent", options.AgentApiKey));

        return services;
    }
}
