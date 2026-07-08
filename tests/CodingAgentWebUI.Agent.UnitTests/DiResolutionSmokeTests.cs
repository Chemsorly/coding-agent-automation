using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using KiroCliLib.Configuration;
using KiroCliLib.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http.Resilience;
using Moq;
using Serilog;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Smoke tests that verify the DI container resolves all services registered in K8s mode.
/// Catches missing registrations (like the Serilog.ILogger bug) at CI time instead of at
/// pod startup in the cluster.
/// </summary>
/// <remarks>
/// These tests replicate the DI registrations from Program.cs without starting the actual
/// WebApplication host (which would require network access). They validate that the service
/// provider can construct every critical service without throwing.
/// </remarks>
public class DiResolutionSmokeTests
{
    /// <summary>
    /// Builds a ServiceCollection mirroring K8s-mode registrations from Program.cs.
    /// Uses mocks for infrastructure (IKiroCliOrchestrator) but real DI wiring.
    /// </summary>
    private static ServiceProvider BuildK8sModeContainer()
    {
        var services = new ServiceCollection();

        // ── Serilog.ILogger (the registration that was previously missing) ──
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .CreateLogger();
        services.AddSingleton(Log.Logger);

        // ── KiroCliLib ──
        var kiroConfig = new Configuration
        {
            KiroCliPath = "/usr/local/bin/kiro-cli",
            UseWsl = false,
            WorkspaceDirectory = "/tmp/workspaces"
        };
        services.AddSingleton(kiroConfig);
        services.AddSingleton<IKiroCliOrchestrator>(sp =>
        {
            var cfg = sp.GetRequiredService<Configuration>();
            return new KiroCliOrchestrator(cfg, callbackHandler: null, Log.Logger);
        });

        // ── Pipeline configuration ──
        services.AddSingleton(new PipelineConfiguration());

        // ── Null history service (same as production agent) ──
        services.AddSingleton<IPipelineRunHistoryService, NullPipelineRunHistoryService>();

        // ── Shared pipeline services ──
        services.AddPipelineServices(Log.Logger);

        // ── HttpClient infrastructure (needed by AddHttpClient<T>) ──
        services.AddHttpClient();

        // ── Agent identity ──
        services.AddSingleton(new AgentIdentity("test-agent-di-smoke"));

        // ── Hub connection manager ──
        services.AddSingleton(new HubConnectionManagerFactory(
            "http://localhost:9999", "test-agent-di-smoke", "fake-api-key", Log.Logger));
        services.AddSingleton(sp => sp.GetRequiredService<HubConnectionManagerFactory>().Create());

        // ── Pipeline executor ──
        services.AddSingleton<IOpenIssueContextWriter>(sp => new OpenIssueContextWriter(Log.Logger));
        services.AddSingleton<IPipelineExecutor>(sp => new LocalPipelineExecutor(
            sp.GetRequiredService<IKiroCliOrchestrator>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<PipelineConfiguration>(),
            sp.GetRequiredService<IQualityGateValidator>(),
            Log.Logger,
            sp.GetRequiredService<IBrainUpdateService>(),
            openIssueContextWriter: sp.GetRequiredService<IOpenIssueContextWriter>(),
            agentIdentity: sp.GetRequiredService<AgentIdentity>()));

        // ── Consolidation executor ──
        services.AddSingleton<IConsolidationExecutor>(sp => new LocalConsolidationExecutor(
            sp.GetRequiredService<IKiroCliOrchestrator>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            Log.Logger));

        // ── K8s-mode registrations (the critical path that had the bug) ──
        services.AddHttpClient<WorkItemHttpClient>(client =>
        {
            client.BaseAddress = new Uri("http://localhost:9999");
        })
        .AddStandardResilienceHandler(options =>
        {
            options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(10);
            options.Retry.MaxRetryAttempts = 1;
        });

        // ── IHostApplicationLifetime mock (needed by WorkItemAgentService) ──
        services.AddSingleton(Mock.Of<IHostApplicationLifetime>());

        // ── IWorkItemExecutor (router) ──
        services.AddSingleton<IWorkItemExecutor>(sp => new WorkItemExecutorRouter(
            sp.GetRequiredService<IPipelineExecutor>(),
            sp.GetRequiredService<IConsolidationExecutor>(),
            Log.Logger));

        // ── IWorkItemLifecycleClient ──
        services.AddSingleton<IWorkItemLifecycleClient>(sp =>
            sp.GetRequiredService<WorkItemHttpClient>());

        // ── WorkItemAgentService ──
        services.AddSingleton<IAgentConnectionManager>(sp =>
        {
            var factory = sp.GetRequiredService<HubConnectionManagerFactory>();
            var hubManager = sp.GetRequiredService<HubConnectionManager>();
            return new AgentConnectionManager(
                hubManager,
                factory,
                sp.GetRequiredService<AgentIdentity>(),
                Log.Logger);
        });
        services.AddSingleton<IJobCompletionReporter>(sp =>
            Mock.Of<IJobCompletionReporter>());
        services.AddSingleton(sp => new WorkItemAgentService(
            "smoke-test-work-item-id",
            sp.GetRequiredService<IWorkItemLifecycleClient>(),
            sp.GetRequiredService<IAgentConnectionManager>(),
            sp.GetRequiredService<IWorkItemExecutor>(),
            sp.GetRequiredService<IJobCompletionReporter>(),
            sp.GetRequiredService<AgentIdentity>(),
            sp.GetRequiredService<IHostApplicationLifetime>(),
            Log.Logger));

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    [Fact]
    public async Task K8sMode_CanResolve_SerilogILogger()
    {
        await using var sp = BuildK8sModeContainer();

        var logger = sp.GetService<Serilog.ILogger>();

        Assert.NotNull(logger);
    }

    [Fact]
    public async Task K8sMode_CanResolve_WorkItemHttpClient()
    {
        await using var sp = BuildK8sModeContainer();

        // WorkItemHttpClient is registered via AddHttpClient<T> — resolution
        // exercises the full DI chain including Serilog.ILogger injection.
        var client = sp.GetRequiredService<WorkItemHttpClient>();

        Assert.NotNull(client);
    }

    [Fact]
    public async Task K8sMode_CanResolve_WorkItemAgentService()
    {
        await using var sp = BuildK8sModeContainer();

        var service = sp.GetRequiredService<WorkItemAgentService>();

        Assert.NotNull(service);
    }

    [Fact]
    public async Task K8sMode_CanResolve_IPipelineExecutor()
    {
        await using var sp = BuildK8sModeContainer();

        var executor = sp.GetRequiredService<IPipelineExecutor>();

        Assert.NotNull(executor);
        Assert.IsType<LocalPipelineExecutor>(executor);
    }

    [Fact]
    public async Task K8sMode_CanResolve_IConsolidationExecutor()
    {
        await using var sp = BuildK8sModeContainer();

        var executor = sp.GetRequiredService<IConsolidationExecutor>();

        Assert.NotNull(executor);
        Assert.IsType<LocalConsolidationExecutor>(executor);
    }

    [Fact]
    public async Task K8sMode_CanResolve_AllSharedPipelineServices()
    {
        await using var sp = BuildK8sModeContainer();

        // Services registered by AddPipelineServices()
        Assert.NotNull(sp.GetRequiredService<IQualityGateValidator>());
        Assert.NotNull(sp.GetRequiredService<IBrainUpdateService>());
        Assert.NotNull(sp.GetRequiredService<IAgentPhaseExecutor>());
        Assert.NotNull(sp.GetRequiredService<IQualityGateExecutor>());
    }

    [Fact]
    public async Task K8sMode_CanResolve_HubConnectionManager()
    {
        await using var sp = BuildK8sModeContainer();

        var manager = sp.GetRequiredService<HubConnectionManager>();

        Assert.NotNull(manager);
    }

    /// <summary>
    /// Regression test: Without builder.Services.AddSingleton(Log.Logger), this resolution
    /// throws InvalidOperationException because AddHttpClient&lt;WorkItemHttpClient&gt; uses DI
    /// to resolve non-HttpClient constructor parameters.
    /// </summary>
    [Fact]
    public void K8sMode_WithoutSerilogRegistration_WorkItemHttpClientResolutionFails()
    {
        // Arrange: build a container WITHOUT the Serilog.ILogger singleton
        var services = new ServiceCollection();
        services.AddHttpClient<WorkItemHttpClient>(client =>
        {
            client.BaseAddress = new Uri("http://localhost:9999");
        });
        // No services.AddSingleton(Log.Logger) — this is the bug scenario

        var sp = services.BuildServiceProvider();

        // Act & Assert: resolution should fail for the missing Serilog.ILogger
        Assert.Throws<InvalidOperationException>(() =>
            sp.GetRequiredService<WorkItemHttpClient>());

        sp.Dispose();
    }

    // ══════════════════════════════════════════════════════════════════════
    // SignalR Mode DI Resolution
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds a ServiceCollection mirroring SignalR-mode registrations from Program.cs.
    /// SignalR mode uses AgentWorkerService (not WorkItemHttpClient/WorkItemAgentService).
    /// </summary>
    private static ServiceProvider BuildSignalRModeContainer()
    {
        var services = new ServiceCollection();

        // ── Serilog.ILogger ──
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .CreateLogger();
        services.AddSingleton(Log.Logger);

        // ── KiroCliLib ──
        var kiroConfig = new Configuration
        {
            KiroCliPath = "/usr/local/bin/kiro-cli",
            UseWsl = false,
            WorkspaceDirectory = "/tmp/workspaces"
        };
        services.AddSingleton(kiroConfig);
        services.AddSingleton<IKiroCliOrchestrator>(sp =>
        {
            var cfg = sp.GetRequiredService<Configuration>();
            return new KiroCliOrchestrator(cfg, callbackHandler: null, Log.Logger);
        });

        // ── Pipeline configuration ──
        services.AddSingleton(new PipelineConfiguration());

        // ── Null history service ──
        services.AddSingleton<IPipelineRunHistoryService, NullPipelineRunHistoryService>();

        // ── Shared pipeline services ──
        services.AddPipelineServices(Log.Logger);

        // ── HttpClient infrastructure ──
        services.AddHttpClient();

        // ── Agent identity ──
        services.AddSingleton(new AgentIdentity("test-agent-signalr-smoke"));

        // ── Hub connection manager ──
        services.AddSingleton(new HubConnectionManagerFactory(
            "http://localhost:9999", "test-agent-signalr-smoke", "fake-api-key", Log.Logger));
        services.AddSingleton(sp => sp.GetRequiredService<HubConnectionManagerFactory>().Create());

        // ── Pipeline executor ──
        services.AddSingleton<IOpenIssueContextWriter>(sp => new OpenIssueContextWriter(Log.Logger));
        services.AddSingleton<IPipelineExecutor>(sp => new LocalPipelineExecutor(
            sp.GetRequiredService<IKiroCliOrchestrator>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<PipelineConfiguration>(),
            sp.GetRequiredService<IQualityGateValidator>(),
            Log.Logger,
            sp.GetRequiredService<IBrainUpdateService>(),
            openIssueContextWriter: sp.GetRequiredService<IOpenIssueContextWriter>(),
            agentIdentity: sp.GetRequiredService<AgentIdentity>()));

        // ── Consolidation executor ──
        services.AddSingleton<IConsolidationExecutor>(sp => new LocalConsolidationExecutor(
            sp.GetRequiredService<IKiroCliOrchestrator>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            Log.Logger));

        // ── IHostApplicationLifetime mock ──
        services.AddSingleton(Mock.Of<IHostApplicationLifetime>());

        // ── SignalR-mode: AgentWorkerService (not WorkItemHttpClient) ──
        services.AddSingleton<IJobCompletionReporter>(sp =>
            Mock.Of<IJobCompletionReporter>());
        services.AddSingleton(sp => new AgentWorkerService(
            sp.GetRequiredService<HubConnectionManager>(),
            sp.GetRequiredService<HubConnectionManagerFactory>(),
            sp.GetRequiredService<IPipelineExecutor>(),
            sp.GetRequiredService<IConsolidationExecutor>(),
            sp.GetRequiredService<IJobCompletionReporter>(),
            sp.GetRequiredService<IKiroCliOrchestrator>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<AgentIdentity>(),
            sp.GetRequiredService<IHostApplicationLifetime>(),
            Log.Logger));

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    [Fact]
    public async Task SignalRMode_CanResolve_AgentWorkerService()
    {
        await using var sp = BuildSignalRModeContainer();

        var service = sp.GetRequiredService<AgentWorkerService>();

        Assert.NotNull(service);
    }

    [Fact]
    public async Task SignalRMode_CanResolve_IPipelineExecutor()
    {
        await using var sp = BuildSignalRModeContainer();

        var executor = sp.GetRequiredService<IPipelineExecutor>();

        Assert.NotNull(executor);
        Assert.IsType<LocalPipelineExecutor>(executor);
    }

    [Fact]
    public async Task SignalRMode_CanResolve_IConsolidationExecutor()
    {
        await using var sp = BuildSignalRModeContainer();

        var executor = sp.GetRequiredService<IConsolidationExecutor>();

        Assert.NotNull(executor);
        Assert.IsType<LocalConsolidationExecutor>(executor);
    }

    [Fact]
    public async Task SignalRMode_CanResolve_AllSharedPipelineServices()
    {
        await using var sp = BuildSignalRModeContainer();

        Assert.NotNull(sp.GetRequiredService<IQualityGateValidator>());
        Assert.NotNull(sp.GetRequiredService<IBrainUpdateService>());
        Assert.NotNull(sp.GetRequiredService<IAgentPhaseExecutor>());
        Assert.NotNull(sp.GetRequiredService<IQualityGateExecutor>());
    }

    [Fact]
    public async Task SignalRMode_DoesNotRegister_WorkItemHttpClient()
    {
        await using var sp = BuildSignalRModeContainer();

        // SignalR mode should NOT have WorkItemHttpClient registered
        var client = sp.GetService<WorkItemHttpClient>();

        Assert.Null(client);
    }
}
