using CodingAgent.Agent;
using CodingAgent.Agent.OpenCode;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using Serilog;

namespace CodingAgent.Agent.UnitTests;

/// <summary>
/// Smoke tests that verify the DI container resolves all services registered in work-item
/// mode and chat mode. Catches missing registrations at CI time instead of at pod startup.
/// </summary>
/// <remarks>
/// These tests build the container through the real production registration methods
/// (<see cref="AgentHostRegistration.AddAgentHostServices"/>,
/// <see cref="AgentWorkItemModeRegistration.AddK8sModeServices"/>,
/// <see cref="AgentChatModeRegistration.AddSignalRModeServices"/>) so that any divergence
/// between Program.cs and the smoke test is caught immediately.
/// </remarks>
public class DiResolutionSmokeTests
{
    /// <summary>
    /// Builds a <see cref="ServiceProvider"/> for either work-item mode or chat mode using
    /// the real production registration methods. The only service the test registers itself
    /// is <see cref="IHostApplicationLifetime"/> (provided by
    /// <c>WebApplication.CreateBuilder</c> in production).
    /// </summary>
    private static ServiceProvider BuildContainer(bool workItemMode, string agentProviderType = "")
    {
        // ── Serilog.ILogger — required by AddK8sModeServices (WorkItemHttpClient reads the static) ──
        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.Console()
            .CreateLogger();

        var services = new ServiceCollection();

        // ── IHostApplicationLifetime — the only service not provided by AddAgentHostServices ──
        services.AddSingleton(Mock.Of<IHostApplicationLifetime>());

        var config = new AgentStartupConfig
        {
            AgentApiKey = "fake-api-key",
            OrchestratorUrl = "http://localhost:9999",
            AgentId = new AgentId("test-agent-di-smoke"),
            WorkItemId = workItemMode ? "smoke-test-work-item-id" : null,
            IsWorkItemMode = workItemMode
        };

        services.AddAgentHostServices(config, agentProviderType, Log.Logger);

        if (workItemMode)
            services.AddK8sModeServices(config, Log.Logger);
        else
            services.AddSignalRModeServices(Log.Logger);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    // ══════════════════════════════════════════════════════════════════════
    // K8s / Work-item Mode
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task K8sMode_CanResolve_SerilogILogger()
    {
        await using var sp = BuildContainer(workItemMode: true);

        var logger = sp.GetService<Serilog.ILogger>();

        Assert.NotNull(logger);
    }

    [Fact]
    public async Task K8sMode_CanResolve_WorkItemHttpClient()
    {
        await using var sp = BuildContainer(workItemMode: true);

        // WorkItemHttpClient is registered via AddHttpClient<T> — resolution
        // exercises the full DI chain including Serilog.ILogger injection.
        var client = sp.GetRequiredService<WorkItemHttpClient>();

        Assert.NotNull(client);
    }

    [Fact]
    public async Task K8sMode_CanResolve_WorkItemAgentService()
    {
        await using var sp = BuildContainer(workItemMode: true);

        var service = sp.GetRequiredService<WorkItemAgentService>();

        Assert.NotNull(service);
    }

    [Fact]
    public async Task K8sMode_CanResolve_IPipelineExecutor()
    {
        await using var sp = BuildContainer(workItemMode: true);

        var executor = sp.GetRequiredService<IPipelineExecutor>();

        Assert.NotNull(executor);
        Assert.IsType<LocalPipelineExecutor>(executor);
    }

    [Fact]
    public async Task K8sMode_CanResolve_IConsolidationExecutor()
    {
        await using var sp = BuildContainer(workItemMode: true);

        var executor = sp.GetRequiredService<IConsolidationExecutor>();

        Assert.NotNull(executor);
        Assert.IsType<LocalConsolidationExecutor>(executor);
    }

    [Fact]
    public async Task K8sMode_CanResolve_AllSharedPipelineServices()
    {
        await using var sp = BuildContainer(workItemMode: true);

        // Services registered by AddPipelineServices()
        // TODO: [WARNING] Assert.NotNull after GetRequiredService is a no-op assertion — GetRequiredService already throws
        // InvalidOperationException if the service is unregistered, so the null check never fires. These pre-existing test
        // names are kept unchanged per acceptance criteria; consider replacing Assert.NotNull with a type assertion or
        // removing it in a future cleanup.
        Assert.NotNull(sp.GetRequiredService<IQualityGateValidator>());
        Assert.NotNull(sp.GetRequiredService<IBrainUpdateService>());
        Assert.NotNull(sp.GetRequiredService<IAgentPhaseExecutor>());
        Assert.NotNull(sp.GetRequiredService<IQualityGateExecutor>());
    }

    [Fact]
    public async Task K8sMode_CanResolve_HubConnectionManager()
    {
        await using var sp = BuildContainer(workItemMode: true);

        var manager = sp.GetRequiredService<IHubConnectionManager>();

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

    [Fact]
    public async Task K8sMode_ResolvesWorkItemAgentServiceAsHostedServiceAndAgentService()
    {
        await using var sp = BuildContainer(workItemMode: true);

        var hostedServices = sp.GetServices<IHostedService>().ToList();
        var workItemService = sp.GetRequiredService<WorkItemAgentService>();
        var agentService = sp.GetRequiredService<IAgentService>();

        Assert.Single(hostedServices.OfType<WorkItemAgentService>());
        Assert.Same(workItemService, agentService);
        // TODO: [WARNING] There is no Assert.Same(workItemService, hostedServices.OfType<WorkItemAgentService>().Single())
        // to verify the IHostedService entry is the same singleton instance as the directly-resolved WorkItemAgentService.
        // A double-registration bug (e.g. services.AddHostedService<WorkItemAgentService>() instead of forwarding to the
        // existing singleton) would pass the current assertions while running two instances in production.
    }

    [Fact]
    public async Task K8sMode_ResolvesHttpPrimaryCompletionReporter()
    {
        await using var sp = BuildContainer(workItemMode: true);

        var reporter = sp.GetRequiredService<IJobCompletionReporter>();

        Assert.IsType<HttpPrimaryCompletionReporter>(reporter);
    }

    // ══════════════════════════════════════════════════════════════════════
    // SignalR / Chat Mode
    // ══════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task SignalRMode_CanResolve_AgentWorkerService()
    {
        await using var sp = BuildContainer(workItemMode: false);

        var service = sp.GetRequiredService<AgentWorkerService>();

        Assert.NotNull(service);
    }

    [Fact]
    public async Task SignalRMode_CanResolve_IPipelineExecutor()
    {
        await using var sp = BuildContainer(workItemMode: false);

        var executor = sp.GetRequiredService<IPipelineExecutor>();

        Assert.NotNull(executor);
        Assert.IsType<LocalPipelineExecutor>(executor);
    }

    [Fact]
    public async Task SignalRMode_CanResolve_IConsolidationExecutor()
    {
        await using var sp = BuildContainer(workItemMode: false);

        var executor = sp.GetRequiredService<IConsolidationExecutor>();

        Assert.NotNull(executor);
        Assert.IsType<LocalConsolidationExecutor>(executor);
    }

    [Fact]
    public async Task SignalRMode_CanResolve_AllSharedPipelineServices()
    {
        await using var sp = BuildContainer(workItemMode: false);

        // TODO: [WARNING] Assert.NotNull after GetRequiredService is a no-op assertion — GetRequiredService already throws
        // InvalidOperationException if the service is unregistered, so the null check never fires. These pre-existing test
        // names are kept unchanged per acceptance criteria; consider replacing Assert.NotNull with a type assertion or
        // removing it in a future cleanup.
        Assert.NotNull(sp.GetRequiredService<IQualityGateValidator>());
        Assert.NotNull(sp.GetRequiredService<IBrainUpdateService>());
        Assert.NotNull(sp.GetRequiredService<IAgentPhaseExecutor>());
        Assert.NotNull(sp.GetRequiredService<IQualityGateExecutor>());
    }

    [Fact]
    public async Task SignalRMode_DoesNotRegister_WorkItemHttpClient()
    {
        await using var sp = BuildContainer(workItemMode: false);

        // SignalR mode should NOT have WorkItemHttpClient registered
        var client = sp.GetService<WorkItemHttpClient>();

        Assert.Null(client);
    }

    [Fact]
    public async Task SignalRMode_ResolvesAgentWorkerServiceAsHostedServiceAndAgentService()
    {
        await using var sp = BuildContainer(workItemMode: false);

        var hostedServices = sp.GetServices<IHostedService>().ToList();
        var workerService = sp.GetRequiredService<AgentWorkerService>();
        var agentService = sp.GetRequiredService<IAgentService>();

        Assert.Single(hostedServices.OfType<AgentWorkerService>());
        Assert.Same(workerService, agentService);
        // TODO: [WARNING] There is no Assert.Same(workerService, hostedServices.OfType<AgentWorkerService>().Single())
        // to verify the IHostedService entry is the same singleton instance as the directly-resolved AgentWorkerService.
        // A double-registration bug (e.g. services.AddHostedService<AgentWorkerService>() instead of forwarding to the
        // existing singleton) would pass the current assertions while running two instances in production.
    }

    [Fact]
    public async Task SignalRMode_ResolvesChatJobExecutorConnectionLifecycleAndRuntimeOptions()
    {
        await using var sp = BuildContainer(workItemMode: false);

        Assert.NotNull(sp.GetRequiredService<ChatJobExecutor>());
        Assert.NotNull(sp.GetRequiredService<AgentConnectionLifecycle>());
        Assert.NotNull(sp.GetRequiredService<AgentRuntimeOptions>());
        Assert.NotNull(sp.GetRequiredService<IHubConnectionManager>());
    }

    [Fact]
    public async Task SignalRMode_OpenCodeProvider_RegistersOpenCodeHealthMonitor()
    {
        await using var sp = BuildContainer(workItemMode: false, agentProviderType: "opencode");

        var hostedServices = sp.GetServices<IHostedService>().ToList();

        Assert.Contains(hostedServices, s => s is OpenCodeHealthMonitor);
    }

    [Fact]
    public async Task SignalRMode_DefaultProvider_DoesNotRegisterOpenCodeHealthMonitor()
    {
        await using var sp = BuildContainer(workItemMode: false, agentProviderType: "");

        var hostedServices = sp.GetServices<IHostedService>().ToList();

        Assert.DoesNotContain(hostedServices, s => s is OpenCodeHealthMonitor);
    }

    [Fact]
    public async Task OpenCodeHttpClient_WithoutPassword_ConfiguresBaseAddressAndTimeout()
    {
        // Exercises the AddHttpClient factory lambda (lines 87-100 in AgentHostRegistration.cs)
        // without a password — covers the if (!string.IsNullOrEmpty(password)) false branch.
        Environment.SetEnvironmentVariable(AgentDefaults.EnvOpenCodeServerPassword, null);
        try
        {
            await using var sp = BuildContainer(workItemMode: false, agentProviderType: "opencode");

            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var client = factory.CreateClient(AgentDefaults.OpenCodeHttpClientName);

            Assert.NotNull(client.BaseAddress);
            Assert.Equal(Timeout.InfiniteTimeSpan, client.Timeout); // each call is bounded by its AgentTimeout instead
            Assert.Null(client.DefaultRequestHeaders.Authorization);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvOpenCodeServerPassword, null);
        }
    }

    [Fact]
    public async Task OpenCodeHttpClient_WithPassword_SetsBasicAuthorizationHeader()
    {
        // Exercises the AddHttpClient factory lambda (lines 87-100 in AgentHostRegistration.cs)
        // with a password set — covers the if (!string.IsNullOrEmpty(password)) true branch.
        Environment.SetEnvironmentVariable(AgentDefaults.EnvOpenCodeServerPassword, "test-password");
        try
        {
            await using var sp = BuildContainer(workItemMode: false, agentProviderType: "opencode");

            var factory = sp.GetRequiredService<IHttpClientFactory>();
            var client = factory.CreateClient(AgentDefaults.OpenCodeHttpClientName);

            Assert.NotNull(client.DefaultRequestHeaders.Authorization);
            Assert.Equal("Basic", client.DefaultRequestHeaders.Authorization.Scheme);
        }
        finally
        {
            Environment.SetEnvironmentVariable(AgentDefaults.EnvOpenCodeServerPassword, null);
        }
    }
}
