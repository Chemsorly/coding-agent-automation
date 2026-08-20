using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.Api.Client;

/// <summary>
/// Extension methods for registering all Pipeline API clients.
/// </summary>
public static class PipelineApiClientServiceCollectionExtensions
{
    /// <summary>
    /// Registers all five typed HTTP clients, the <see cref="IAgentHubConnection"/> factory,
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

        // Work items client — authenticated
        services.AddHttpClient<IPipelineApiWorkItemClient, PipelineApiWorkItemClient>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.AgentApiKey);
        }).AddStandardResilienceHandler();

        // Run history client — authenticated
        services.AddHttpClient<IPipelineApiRunHistoryClient, PipelineApiRunHistoryClient>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.AgentApiKey);
        }).AddStandardResilienceHandler();

        // Config client — authenticated
        services.AddHttpClient<IPipelineApiConfigClient, PipelineApiConfigClient>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.AgentApiKey);
        }).AddStandardResilienceHandler();

        // Agent registry client — authenticated. /api/agents requires the OPERATOR policy, so this
        // client only works when AgentApiKey is the master key (the monolith and Job Controller);
        // a caller configured with a per-pod derived key gets 403.
        services.AddHttpClient<IPipelineApiAgentClient, PipelineApiAgentClient>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.AgentApiKey);
        }).AddStandardResilienceHandler();

        // Health client — no auth (healthz/readyz are anonymous)
        services.AddHttpClient<IPipelineApiHealthClient, PipelineApiHealthClient>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
        }).AddStandardResilienceHandler();

        // Consolidation run client — authenticated (operator tier; master key required)
        services.AddHttpClient<IPipelineApiConsolidationRunClient, PipelineApiConsolidationRunClient>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.AgentApiKey);
        }).AddStandardResilienceHandler();

        // Harness suggestion client — authenticated (operator tier; master key required)
        services.AddHttpClient<IPipelineApiHarnessSuggestionClient, PipelineApiHarnessSuggestionClient>(client =>
        {
            client.BaseAddress = new Uri(options.BaseUrl);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", options.AgentApiKey);
        }).AddStandardResilienceHandler();

        // Hub connection — transient so each caller owns its own connection lifecycle
        services.AddTransient<IAgentHubConnection>(_ =>
            new AgentHubConnection($"{options.BaseUrl.TrimEnd('/')}/hubs/agent", options.AgentApiKey));

        return services;
    }
}
