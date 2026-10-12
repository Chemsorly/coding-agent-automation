using System.Reflection;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Services;
using CodingAgent.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgent.Web.IntegrationTests.Smoke;

[Collection("SmokeTests")]
public class DiContainerTests : IClassFixture<CustomWebApplicationFactory>
{
    private readonly CustomWebApplicationFactory _factory;

    public DiContainerTests(CustomWebApplicationFactory factory)
    {
        _factory = factory;
    }

    [Theory]
    [InlineData(typeof(IConfigurationStore))]
    [InlineData(typeof(IProviderFactory))]
    [InlineData(typeof(IQualityGateValidator))]
    // PipelineLoopService was moved to CodingAgent.Scheduler in Spec 047 — no longer
    // registered in the WebUI DI container. Removed from this smoke test.
    [InlineData(typeof(IPipelineRunHistoryService))]
    [InlineData(typeof(IAgentPhaseExecutor))]
    [InlineData(typeof(IQualityGateExecutor))]
    [InlineData(typeof(IssueDescriptionParser))]
    public void Key_Service_Resolves_Without_Error(Type serviceType)
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetService(serviceType);

        Assert.NotNull(service);
    }

    [Theory]
    // PipelineLoopService was moved to CodingAgent.Scheduler in Spec 047 — no longer
    // registered in the WebUI DI container. Removed from this smoke test.
    [InlineData(typeof(IConfigurationStore))]
    [InlineData(typeof(IProviderFactory))]
    public void Singleton_Services_Return_Same_Instance(Type serviceType)
    {
        var first = _factory.Services.GetRequiredService(serviceType);
        var second = _factory.Services.GetRequiredService(serviceType);

        Assert.Same(first, second);
    }

    [Fact]
    public void Transient_Service_Returns_Different_Instances()
    {
        using var scope = _factory.Services.CreateScope();

        var first = scope.ServiceProvider.GetRequiredService<IssueDescriptionParser>();
        var second = scope.ServiceProvider.GetRequiredService<IssueDescriptionParser>();

        Assert.NotSame(first, second);
    }

    /// <summary>
    /// The monolith must read agent presence from the Pipeline API, not from a local registry.
    ///
    /// <para>
    /// Spec 044 left <c>MapHub&lt;AgentHub&gt;</c> — and with it <c>RegisterAgent</c>, the only
    /// writer of any agent registry — in the API process, while this host kept binding
    /// <c>IAgentRegistryService</c> to its own <c>AgentRegistryService</c>. Nothing ever wrote to
    /// that instance, so <c>Fleet</c>, <c>SidebarHealthIndicators</c> and the drawer
    /// services reported an empty cluster in every deployment. Rebinding it to the local type again
    /// would restore that defect silently, which is what this test is here to prevent.
    /// </para>
    /// </summary>
    [Fact]
    public void AgentRegistryInterface_IsBoundToTheApiBackedImplementation()
    {
        var registry = _factory.Services.GetRequiredService<IAgentRegistryService>();

        Assert.IsType<ApiAgentRegistryService>(registry);
    }

    /// <summary>
    /// The poller and the readers must share one instance. <c>AgentRegistrySyncService</c> resolves
    /// <c>ApiAgentRegistryService</c> by its concrete type while every consumer injects
    /// <c>IAgentRegistryService</c>; if those two registrations produced separate objects the poller
    /// would faithfully refresh a snapshot nobody reads, and the UI would stay empty exactly as
    /// before.
    /// </summary>
    [Fact]
    public void ApiBackedRegistry_IsASingleton_SharedWithTheInterfaceRegistration()
    {
        var byInterface = _factory.Services.GetRequiredService<IAgentRegistryService>();
        var byConcreteType = _factory.Services.GetRequiredService<ApiAgentRegistryService>();

        Assert.Same(byConcreteType, byInterface);
        Assert.Same(byInterface, _factory.Services.GetRequiredService<IAgentRegistryService>());
    }

    /// <summary>
    /// The local registry stays registered under its concrete type — the E2E factories resolve it directly.
    /// </summary>
    [Fact]
    public void LocalAgentRegistry_RemainsResolvableByConcreteType()
    {
        Assert.NotNull(_factory.Services.GetRequiredService<AgentRegistryService>());
    }

    /// <summary>
    /// Every service the web host registers under one of its own types, and every service a Razor
    /// component injects, must be constructible. Factory registrations and <c>@inject</c> are not
    /// covered by <c>ValidateOnBuild</c>, so a registration removed while a dependency still needs
    /// it would otherwise only fail when a user opens the page.
    /// </summary>
    [Fact]
    public async Task Every_Application_Service_And_Component_Injection_Resolves()
    {
        _ = _factory.Services; // builds the host, which fills RegisteredServices
        var applicationServices = _factory.RegisteredServices
            .Where(d => !d.IsKeyedService
                        && !d.ServiceType.IsGenericTypeDefinition
                        && d.ServiceType.Namespace?.StartsWith("CodingAgent", StringComparison.Ordinal) == true)
            .Select(d => d.ServiceType);
        var componentInjections = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(Microsoft.AspNetCore.Components.ComponentBase).IsAssignableFrom(t))
            .SelectMany(t => t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(p => p.GetCustomAttribute<Microsoft.AspNetCore.Components.InjectAttribute>() is not null)
            .Select(p => p.PropertyType);

        // Async scope: the hub connection (scoped, one per circuit) only implements IAsyncDisposable.
        await using var scope = _factory.Services.CreateAsyncScope();
        var failures = new List<string>();
        foreach (var type in applicationServices.Concat(componentInjections).Distinct())
        {
            try
            {
                if (scope.ServiceProvider.GetService(type) is null)
                    failures.Add($"{type.FullName}: not registered");
            }
            catch (Exception ex)
            {
                failures.Add($"{type.FullName}: {ex.GetType().Name}: {ex.Message}");
            }
        }

        Assert.Empty(failures);
    }

    /// <summary>
    /// The circuit hub accepts messages up to 128 KB. It used to inherit that limit from the global
    /// SignalR options of the agent-hub setup this host registered; without the explicit setting it
    /// would fall back to SignalR's 32 KB default and drop larger inputs such as pasted config.
    /// </summary>
    [Fact]
    public void Circuit_Hub_Keeps_The_128KB_Message_Limit()
    {
        var componentHub = Type.GetType("Microsoft.AspNetCore.Components.Server.ComponentHub, Microsoft.AspNetCore.Components.Server", throwOnError: true)!;
        var optionsType = typeof(Microsoft.Extensions.Options.IOptions<>)
            .MakeGenericType(typeof(Microsoft.AspNetCore.SignalR.HubOptions<>).MakeGenericType(componentHub));

        var hubOptions = (Microsoft.AspNetCore.SignalR.HubOptions)optionsType.GetProperty("Value")!
            .GetValue(_factory.Services.GetRequiredService(optionsType))!;

        Assert.Equal(128 * 1024, hubOptions.MaximumReceiveMessageSize);
    }

    /// <summary>
    /// The web host makes no Kubernetes API calls: leader election runs in the Scheduler and the
    /// Job Controller, and job operations run in the Pipeline API. A registration here would need
    /// the service account token the chart no longer mounts into the web pod.
    /// </summary>
    [Fact]
    public void Web_Host_Registers_No_Kubernetes_Client_Or_Leader_Election()
    {
        _ = _factory.Services;

        Assert.DoesNotContain(_factory.RegisteredServices, d => d.ServiceType == typeof(k8s.IKubernetes));
        Assert.DoesNotContain(_factory.RegisteredServices,
            d => d.ServiceType == typeof(CodingAgent.Pipeline.LeaderElection.ILeaderElectionService)
                 || d.ServiceType == typeof(CodingAgent.Pipeline.LeaderElection.LeaderElectionService));
    }
}
