using System.Net.Http.Headers;
using CodingAgent.Infrastructure.Common;
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
    /// Shared circuit-breaker resilience override applied to all registered HTTP clients.
    /// MinimumThroughput is lowered to 10 (from the default 100) so the breaker can trip after
    /// ~10 failures in 30 s. Without this, low-traffic clients stall for the full 3×10 s = 30 s
    /// during a pod blip.
    /// Also used by <see cref="CodingAgent.Api.ApiServiceCollectionExtensions"/> for the internal
    /// TokenVending named client via the transitive assembly reference.
    /// </summary>
    public static readonly Action<HttpStandardResilienceOptions> ConfigureResilienceOptions =
        o => o.CircuitBreaker.MinimumThroughput = 10;

    /// <summary>
    /// Registers all typed HTTP clients and the <see cref="PipelineApiClientOptions"/> singleton.
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
        // Sets PooledConnectionLifetime to 90s via AddStandardHttpClientPoolDefaults so stale
        // connections to replaced pod IPs are recycled after a rolling update. The sentinel guard
        // in AddStandardHttpClientPoolDefaults prevents duplicate handler registration when this
        // method is called more than once in the same DI container.
        //
        // NOTE: resilience options (CircuitBreaker.MinimumThroughput) are tuned per-client via
        // ConfigureResilienceOptions rather than via ConfigureHttpClientDefaults to avoid stacking
        // a second pipeline when hosts call AddPipelineApiClient more than once in the same DI
        // container (e.g. WorkDistributionRegistration.Consolidation.cs).
        services.AddStandardHttpClientPoolDefaults();

        // ── Shared authenticated-client configuration ────────────────────────────
        // Captures BaseUrl and AgentApiKey from the options parameter (closure).
        // Applied to every client except Health (healthz/readyz are anonymous endpoints).
        Action<HttpClient> configureAuthenticatedClient = client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue(BearerScheme, options.AgentApiKey);
        };

        // Work items client — authenticated
        services.AddHttpClient<IPipelineApiWorkItemClient, PipelineApiWorkItemClient>(configureAuthenticatedClient)
            .AddStandardResilienceHandler(ConfigureResilienceOptions);

        // Run history client — authenticated. /api/pipeline-runs requires the OPERATOR policy
        // after W0-04. Only works when AgentApiKey is the master key (orchestrator process).
        // Per-pod derived keys receive HTTP 403 — same constraint as IPipelineApiAgentClient above.
        services.AddHttpClient<IPipelineApiRunHistoryClient, PipelineApiRunHistoryClient>(configureAuthenticatedClient)
            .AddStandardResilienceHandler(ConfigureResilienceOptions);

        // Config client — authenticated
        services.AddHttpClient<IPipelineApiConfigClient, PipelineApiConfigClient>(configureAuthenticatedClient)
            .AddStandardResilienceHandler(ConfigureResilienceOptions);

        // Agent registry client — authenticated. /api/agents requires the OPERATOR policy.
        // Only works when AgentApiKey is the master key (monolith and Job Controller).
        // Per-pod derived keys receive HTTP 403 from this endpoint.
        services.AddHttpClient<IPipelineApiAgentClient, PipelineApiAgentClient>(configureAuthenticatedClient)
            .AddStandardResilienceHandler(ConfigureResilienceOptions);

        // Health client — no auth (healthz/readyz are anonymous)
        services.AddHttpClient<IPipelineApiHealthClient, PipelineApiHealthClient>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
        }).AddStandardResilienceHandler(ConfigureResilienceOptions);

        return services;
    }
}
