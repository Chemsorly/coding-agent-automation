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

    // Spec 051 4.1: pages read and act through the API's /api/ui surface and UI hub as the signed-in user.
    [Theory]
    [InlineData(typeof(CodingAgent.Web.Ui.IUiRunClient))]
    [InlineData(typeof(CodingAgent.Web.Ui.IUiSettingsClient))]
    [InlineData(typeof(CodingAgent.Web.Ui.IUiHubConnection))]
    [InlineData(typeof(CodingAgent.Web.Ui.IUiAccessTokenSource))]
    [InlineData(typeof(AgentCodingPageService))]
    public async Task Key_Service_Resolves_Without_Error(Type serviceType)
    {
        // Async: the UI hub connection is only IAsyncDisposable.
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetService(serviceType);

        Assert.NotNull(service);
    }

    /// <summary>
    /// Spec 051 4.1: the web host holds no agent API key and none of the services that need one: no work
    /// distributor, no run history or configuration store, no pipeline executors.
    /// </summary>
    [Fact]
    public void WebHost_RegistersNoServerService()
    {
        _ = _factory.Services;

        string[] serverNamespaces = ["CodingAgent.Orchestration", "CodingAgent.AgentGateway", "CodingAgent.Api.Client"];
        var offenders = _factory.ProductionServices
            .Select(d => d.ImplementationType ?? d.ServiceType)
            .Where(t => serverNamespaces.Any(ns => t.Namespace?.StartsWith(ns, StringComparison.Ordinal) == true)
                        || t.Assembly.GetName().Name is "CodingAgent.Pipeline" or "CodingAgent.Orchestration" or "CodingAgent.AgentGateway" or "CodingAgent.Api.Client")
            .Select(t => t.FullName)
            .ToList();
        Assert.Empty(offenders);
    }

    /// <summary>
    /// Spec 051 D8: pages read the agents with the user's token (<c>/api/ui/agents</c>), so the web host
    /// keeps no agent registry and polls nothing in the background with the master key.
    /// </summary>
    [Fact]
    public void WebHost_KeepsNoAgentRegistry()
    {
        Assert.Null(_factory.Services.GetService<IAgentRegistryService>());
        Assert.Null(_factory.Services.GetService<AgentRegistryService>());
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
