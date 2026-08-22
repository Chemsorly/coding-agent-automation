using System.Reflection;
using CodingAgentWebUI.Infrastructure.GitHub;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.Extensions.DependencyInjection;

namespace CodingAgentWebUI.IntegrationTests.Smoke;

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
    [InlineData(typeof(PipelineOrchestrationService))]
    [InlineData(typeof(PipelineLoopService))]
    [InlineData(typeof(IBrainUpdateService))]
    [InlineData(typeof(IPipelineRunHistoryService))]
    [InlineData(typeof(IAgentPhaseExecutor))]
    [InlineData(typeof(IQualityGateExecutor))]
    [InlineData(typeof(IssueDescriptionParser))]
    [InlineData(typeof(GitHubValidationService))]
    [InlineData(typeof(IChatNotifier))]
    public void Key_Service_Resolves_Without_Error(Type serviceType)
    {
        using var scope = _factory.Services.CreateScope();
        var service = scope.ServiceProvider.GetService(serviceType);

        Assert.NotNull(service);
    }

    [Theory]
    [InlineData(typeof(PipelineOrchestrationService))]
    [InlineData(typeof(PipelineLoopService))]
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
    /// that instance, so <c>AgentMonitoring</c>, <c>SidebarHealthIndicators</c> and the drawer
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
    /// The local registry stays registered under its concrete type — <c>ConsolidationDispatchService</c>,
    /// <c>ModelFetchService</c>, <c>AgentChat.razor</c> and the E2E factories all resolve it directly.
    /// </summary>
    [Fact]
    public void LocalAgentRegistry_RemainsResolvableByConcreteType()
    {
        Assert.NotNull(_factory.Services.GetRequiredService<AgentRegistryService>());
    }

    /// <summary>
    /// The dedup guard stays on the in-process registry. <c>SelectAgent</c> reserves an agent by
    /// flipping it to Busy under the same lock that chose it; against a read-only replica that
    /// reservation evaporates and two callers can be handed the same agent.
    /// </summary>
    [Fact]
    public void JobDeduplicationGuard_ReadsTheLocalRegistry_NotTheApiBackedOne()
    {
        var guard = _factory.Services.GetRequiredService<JobDeduplicationGuardService>();

        var registryField = typeof(JobDeduplicationGuardService)
            .GetField("_registry", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(registryField);

        Assert.IsType<AgentRegistryService>(registryField!.GetValue(guard));
    }
}
