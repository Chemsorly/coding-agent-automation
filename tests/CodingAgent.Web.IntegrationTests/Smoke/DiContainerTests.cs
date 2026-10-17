using System.Reflection;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Web.Services;
using CodingAgent.Web.Ui;
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

    // Spec 052: the BFF's own services (sign-in, permissions, forwarding).
    [Theory]
    [InlineData(typeof(CodingAgent.Web.Auth.UserTokenMinter))]
    [InlineData(typeof(CodingAgent.Web.Auth.IRbacEvaluator))]
    [InlineData(typeof(CodingAgent.Web.Auth.IAuthUiInfo))]
    [InlineData(typeof(Yarp.ReverseProxy.Forwarder.IHttpForwarder))]
    public async Task Key_Service_Resolves_Without_Error(Type serviceType)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var service = scope.ServiceProvider.GetService(serviceType);

        Assert.NotNull(service);
    }

    /// <summary>
    /// Spec 052 Req 0.2, 8.1: pages run in the browser, so the web host registers none of their
    /// services: no UI clients, no UI hub connection, no page services, no token source.
    /// </summary>
    [Theory]
    [InlineData(typeof(IUiRunClient))]
    [InlineData(typeof(IUiSettingsClient))]
    [InlineData(typeof(IUiHubConnection))]
    [InlineData(typeof(IUiAccessTokenSource))]
    [InlineData(typeof(AgentCodingPageService))]
    [InlineData(typeof(CodingAgent.Web.Auth.CurrentAccess))]
    public void WebHost_RegistersNoPageService(Type serviceType)
    {
        _ = _factory.Services;

        Assert.DoesNotContain(_factory.ProductionServices, d => d.ServiceType == serviceType);
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
    /// Every service the web host registers under one of its own types, and every service a component
    /// injects that the web host still renders, must be constructible. Since spec 052 that is the static
    /// login render: the components of this assembly (App, Login), the client's Routes and AuthLayout.
    /// Factory registrations and <c>@inject</c> are not covered by <c>ValidateOnBuild</c>. The browser's
    /// container has its own test (<c>WebClientHostDiTests</c>).
    /// </summary>
    [Fact]
    public async Task Every_Application_Service_And_Server_Rendered_Injection_Resolves()
    {
        _ = _factory.Services; // builds the host, which fills RegisteredServices
        var applicationServices = _factory.RegisteredServices
            .Where(d => !d.IsKeyedService
                        && !d.ServiceType.IsGenericTypeDefinition
                        && d.ServiceType.Namespace?.StartsWith("CodingAgent", StringComparison.Ordinal) == true)
            .Select(d => d.ServiceType);
        var serverRendered = typeof(Program).Assembly.GetTypes()
            .Where(t => typeof(Microsoft.AspNetCore.Components.ComponentBase).IsAssignableFrom(t))
            .Concat([typeof(CodingAgent.Web.Components.Routes), typeof(CodingAgent.Web.Components.Auth.AuthLayout)]);
        var componentInjections = serverRendered
            .SelectMany(t => t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(p => p.GetCustomAttribute<Microsoft.AspNetCore.Components.InjectAttribute>() is not null)
            .Select(p => p.PropertyType);

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
