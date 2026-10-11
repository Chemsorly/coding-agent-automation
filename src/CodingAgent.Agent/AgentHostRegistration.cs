using System.Net.Http.Headers;
using System.Text;
using CodingAgent.Agent.OpenCode;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using KiroCliLib.Configuration;
using KiroCliLib.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Agent;

/// <summary>
/// Extension methods for registering the shared agent host services that both work-item mode
/// and chat mode require. Called from <c>Program.cs</c> before the mode-conditional branch
/// (<see cref="AgentWorkItemModeRegistration.AddK8sModeServices"/> /
/// <see cref="AgentChatModeRegistration.AddSignalRModeServices"/>).
/// Extracted from Program.cs to allow the DI smoke tests to build the real production
/// container (T23, arch-audit 2026-08-22; issue #3447).
/// </summary>
internal static class AgentHostRegistration
{
    /// <summary>
    /// Registers all shared agent services: Serilog logger, KiroCliLib, pipeline services,
    /// runtime options, OpenCode HTTP client, (conditionally) OpenCode health monitor,
    /// agent identity, hub connection manager, pipeline executor, and consolidation executor.
    /// </summary>
    /// <param name="services">The service collection to register into.</param>
    /// <param name="config">Resolved startup configuration.</param>
    /// <param name="agentProviderType">
    /// Value of <c>AGENT_PROVIDER_TYPE</c> environment variable (empty string when absent).
    /// Used to conditionally register <see cref="OpenCodeHealthMonitor"/>.
    /// </param>
    /// <param name="logger">Serilog logger for the agent process.</param>
    internal static IServiceCollection AddAgentHostServices(
        this IServiceCollection services,
        AgentStartupConfig config,
        string agentProviderType,
        ILogger logger)
    {
        // ── Serilog.ILogger (needed by WorkItemHttpClient and other components) ──
        services.AddSingleton(logger);

        // ── KiroCliLib ──
        // A chat pod's runs take the chat's model and effort from here (AGENT_CHAT_MODEL, AGENT_CHAT_EFFORT);
        // pipeline pods have neither and get them per provider from AgentProviderFactory.
        services.AddSingleton(sp =>
        {
            var runtimeOpts = sp.GetRequiredService<AgentRuntimeOptions>();
            return new Configuration
            {
                KiroCliPath = AgentDefaults.KiroCliPath,
                UseWsl = false, // Agent runs natively in Linux container
                WorkspaceDirectory = "/app/workspaces",
                Model = runtimeOpts.ChatModel,
                Effort = CodingAgent.Agent.KiroCli.KiroCliAgentProvider.ToKiroEffort(
                    AgentEffortLevelExtensions.ParseEffort(runtimeOpts.ChatEffort))
            };
        });
        services.AddSingleton<IKiroCliOrchestrator>(sp =>
        {
            var cfg = sp.GetRequiredService<Configuration>();
            return new KiroCliOrchestrator(cfg, logger);
        });

        // ── Pipeline configuration (will be overridden per-job, but needed for factory construction) ──
        var defaultPipelineConfig = new PipelineConfiguration();
        services.AddSingleton(defaultPipelineConfig);

        // ── Null-safe history service (agent doesn't maintain run history) ──
        services.AddSingleton<IPipelineRunHistoryService, NullPipelineRunHistoryService>();

        // ── Shared pipeline services (IQualityGateValidator, IBrainUpdateService, IAgentPhaseExecutor, IQualityGateExecutor) ──
        services.AddPipelineServices(logger);
        // BrainUpdateService is an Infrastructure impl (Infrastructure.Git) — registered by the host
        // composition root so the Pipeline-side AddPipelineServices helper stays Pipeline-only.
        services.AddSingleton<CodingAgent.Pipeline.Interfaces.IBrainUpdateService>(
            sp => new CodingAgent.Infrastructure.Git.BrainUpdateService(logger));

        // ── Agent runtime options (replaces scattered Environment.GetEnvironmentVariable calls) ──
        // TODO: [WARNING] AgentRuntimeOptions is resolved via a factory lambda (sp.GetRequiredService<IOptions<AgentRuntimeOptions>>().Value),
        // which means ValidateOnBuild = true cannot catch a misconfigured AgentRuntimeOptionsSetup. AgentRuntimeOptionsSetup reads
        // environment variables at first resolution time; if the environment were mutated between container construction and first
        // use the result would be non-deterministic (in practice the env is stable). Consider registering AgentRuntimeOptions
        // eagerly (e.g. resolve and validate at startup) so ValidateOnBuild can cover it.
        services.AddOptions<AgentRuntimeOptions>();
        services.AddSingleton<IConfigureOptions<AgentRuntimeOptions>, AgentRuntimeOptionsSetup>();
        services.AddSingleton<AgentRuntimeOptions>(sp =>
            sp.GetRequiredService<IOptions<AgentRuntimeOptions>>().Value);

        // ── OpenCode named HttpClient (always registered — safe when OPENCODE_SERVER_PASSWORD is absent) ──
        services.AddHttpClient(AgentDefaults.OpenCodeHttpClientName, (sp, client) =>
        {
            var runtimeOpts = sp.GetRequiredService<AgentRuntimeOptions>();
            var baseUrl = runtimeOpts.OpenCodeBaseUrl ?? AgentDefaults.OpenCodeBaseUrl;
            client.BaseAddress = new Uri(baseUrl);
            // OpenCode's message API blocks until the agent finishes, which may take as long as the
            // request's AgentTimeout (up to 24 h). That timeout (TimeoutHelper) bounds each call and
            // aborts the session; a fixed client timeout would cut long turns short without an abort.
            client.Timeout = Timeout.InfiniteTimeSpan;

            var password = runtimeOpts.OpenCodeServerPassword;
            if (!string.IsNullOrEmpty(password))
            {
                client.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Basic",
                        Convert.ToBase64String(Encoding.UTF8.GetBytes($"opencode:{password}")));
            }
        });

        // ── OpenCode health monitor (only when provider type is OpenCode) ──
        // TODO: [WARNING] This guard compares agentProviderType against AgentDefaults.OpenCodeHttpClientName ("OpenCode"),
        // which is the HTTP-client name constant rather than a dedicated provider-type constant. AgentChatModeRegistration.cs:31
        // uses the same constant for its own provider-type resolution. If a second canonical name for the opencode provider
        // is ever introduced, this condition and AgentChatModeRegistration.cs:31 could diverge silently. Consider introducing
        // a dedicated AgentDefaults.OpenCodeProviderType constant for provider-type comparisons.
        if (agentProviderType.Equals(AgentDefaults.OpenCodeHttpClientName, StringComparison.OrdinalIgnoreCase))
        {
            services.AddHostedService<OpenCodeHealthMonitor>(sp =>
                new OpenCodeHealthMonitor(sp.GetRequiredService<IHttpClientFactory>(), logger));
        }

        // ── Agent identity (single source of truth for AGENT_ID) ──
        services.Add(ServiceDescriptor.Singleton(typeof(AgentId), config.AgentId));

        // ── Hub connection manager ──
        services.AddSingleton<IHubConnectionManagerFactory>(sp =>
            new HubConnectionManagerFactory(config.OrchestratorUrl, config.AgentId, config.AgentApiKey, logger));
        services.AddSingleton<IHubConnectionManager>(sp =>
            sp.GetRequiredService<IHubConnectionManagerFactory>().Create());

        // ── Pipeline executor ──
        services.AddSingleton<IPipelineReporterFactory>(sp => new PipelineReporterFactory(logger));
        services.AddSingleton<IPipelineExecutor>(sp => new LocalPipelineExecutor(
            new LocalPipelineExecutorDependencies(
                sp.GetRequiredService<IKiroCliOrchestrator>(),
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<PipelineConfiguration>(),
                sp.GetRequiredService<IQualityGateValidator>(),
                logger,
                sp.GetRequiredService<IBrainUpdateService>(),
                AgentIdentity: sp.GetRequiredService<AgentId>(),
                ReporterFactory: sp.GetRequiredService<IPipelineReporterFactory>())));

        // ── Consolidation executor ──
        services.AddSingleton<IConsolidationExecutor>(sp => new LocalConsolidationExecutor(
            sp.GetRequiredService<IKiroCliOrchestrator>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            logger,
            sp.GetRequiredService<IBrainUpdateService>()));

        return services;
    }
}
