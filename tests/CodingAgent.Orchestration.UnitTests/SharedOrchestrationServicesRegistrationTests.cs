using AwesomeAssertions;
using CodingAgent.Infrastructure;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgent.Orchestration.UnitTests;

/// <summary>
/// Verifies that <see cref="SharedOrchestrationServiceCollectionExtensions.AddSharedOrchestrationServices"/>
/// registers exactly the five services it is documented to register, using the correct concrete
/// implementations, as singletons.
///
/// A dedicated prerequisite helper (<see cref="BuildMinimalPrerequisites"/>) registers only the
/// dependencies the extension requires — notably without registering <see cref="IConfigurationStore"/>
/// in DI, which pins the requirement that <see cref="IDispatchOrchestrationService"/> is wired
/// via <see cref="IProviderConfigStore"/> (not <see cref="IConfigurationStore"/>).
/// </summary>
public sealed class SharedOrchestrationServicesRegistrationTests
{
    /// <summary>
    /// Registers the minimum prerequisites required by
    /// <see cref="SharedOrchestrationServiceCollectionExtensions.AddSharedOrchestrationServices"/>:
    /// <list type="bullet">
    ///   <item><see cref="IPipelineConfigStore"/> (mock)</item>
    ///   <item><see cref="IProjectStore"/> (mock)</item>
    ///   <item><see cref="IWorkDistributor"/> (mock)</item>
    ///   <item><see cref="IProviderConfigStore"/> (mock)</item>
    ///   <item><see cref="IPipelineRunHistoryService"/> (mock)</item>
    ///   <item><see cref="DispatchInfrastructure"/> (constructed directly, not via AddDispatchResolutionServices)</item>
    /// </list>
    /// <para>
    /// <see cref="IConfigurationStore"/> is intentionally NOT registered in DI.
    /// <see cref="DispatchResolutionService"/> (a collaborator of <see cref="DispatchInfrastructure"/>)
    /// receives a <see cref="Mock{IConfigurationStore}"/> directly via its constructor — not through DI.
    /// This allows test 6 to verify that <see cref="IDispatchOrchestrationService"/> resolves when
    /// <see cref="IProviderConfigStore"/> is present but <see cref="IConfigurationStore"/> is absent from DI.
    /// </para>
    /// </summary>
    private static ServiceCollection BuildMinimalPrerequisites()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Prerequisites for IProviderFactory and ConsolidationServiceDependencies
        services.AddSingleton(new Mock<IPipelineConfigStore>().Object);

        // Prerequisites for ConsolidationServiceDependencies
        services.AddSingleton(new Mock<IProjectStore>().Object);
        services.AddSingleton(new Mock<IWorkDistributor>().Object);

        // Prerequisites for DispatchOrchestrationServiceDependencies (third arg) and ConsolidationStalenessService
        services.AddSingleton(new Mock<IProviderConfigStore>().Object);

        // Prerequisite for ConsolidationStalenessService
        services.AddSingleton(new Mock<IPipelineRunHistoryService>().Object);

        // DispatchInfrastructure registered as a pre-built object — NOT via AddDispatchResolutionServices.
        // QualityGateResolver and ReviewerResolver have no-argument constructors.
        // IConfigurationStore is passed directly to DispatchResolutionService's constructor here,
        // NOT registered in the DI container. This is intentional: it allows the
        // ResolvesWithProviderConfigStoreButNoConfigurationStore test to verify that
        // IDispatchOrchestrationService resolves when IProviderConfigStore is present but
        // IConfigurationStore is absent from DI.
        services.AddSingleton(new DispatchInfrastructure(
            new Mock<ITokenVendingService>().Object,
            new Mock<IProviderFactory>().Object,
            new Mock<ILabelService>().Object,
            new DispatchResolutionService(
                new QualityGateResolver(),
                new ReviewerResolver(),
                new Mock<IConfigurationStore>().Object))); // NOT registered in DI — constructor-injected directly

        return services;
    }

    // TODO [WARNING]: The five tests below share identical structure (BuildMinimalPrerequisites,
    // AddSharedOrchestrationServices, BuildServiceProvider, two GetRequiredService calls, BeOfType,
    // BeSameAs). A future change to BuildMinimalPrerequisites or the common assertion pattern
    // requires the same edit in all five. Consider parameterising as a [Theory] with a typed
    // test-data source that resolves each service and checks its concrete type and singleton identity.
    // See TestQualityReviewer finding on SharedOrchestrationServicesRegistrationTests.cs:79.

    [Fact]
    public async Task AddSharedOrchestrationServices_IProviderFactory_ResolvesAsProviderFactorySingleton()
    {
        var services = BuildMinimalPrerequisites();
        services.AddSharedOrchestrationServices();

        await using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IProviderFactory>();
        var second = provider.GetRequiredService<IProviderFactory>();

        first.Should().BeOfType<ProviderFactory>(
            "IProviderFactory must be backed by the real ProviderFactory implementation");
        first.Should().BeSameAs(second,
            "IProviderFactory must be registered as a singleton");
    }

    [Fact]
    public async Task AddSharedOrchestrationServices_IConsolidationService_ResolvesAsConsolidationServiceSingleton()
    {
        var services = BuildMinimalPrerequisites();
        services.AddSharedOrchestrationServices();

        await using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IConsolidationService>();
        var second = provider.GetRequiredService<IConsolidationService>();

        first.Should().BeOfType<ConsolidationService>(
            "IConsolidationService must be backed by ConsolidationService");
        first.Should().BeSameAs(second,
            "IConsolidationService must be registered as a singleton");
    }

    [Fact]
    public async Task AddSharedOrchestrationServices_IDependencyChecker_ResolvesAsDependencyCheckerSingleton()
    {
        var services = BuildMinimalPrerequisites();
        services.AddSharedOrchestrationServices();

        await using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IDependencyChecker>();
        var second = provider.GetRequiredService<IDependencyChecker>();

        first.Should().BeOfType<DependencyChecker>(
            "IDependencyChecker must be backed by DependencyChecker");
        first.Should().BeSameAs(second,
            "IDependencyChecker must be registered as a singleton");
    }

    [Fact]
    public async Task AddSharedOrchestrationServices_IDispatchOrchestrationService_ResolvesAsDispatchOrchestrationServiceSingleton()
    {
        var services = BuildMinimalPrerequisites();
        services.AddSharedOrchestrationServices();

        await using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<IDispatchOrchestrationService>();
        var second = provider.GetRequiredService<IDispatchOrchestrationService>();

        first.Should().BeOfType<DispatchOrchestrationService>(
            "IDispatchOrchestrationService must be backed by DispatchOrchestrationService");
        first.Should().BeSameAs(second,
            "IDispatchOrchestrationService must be registered as a singleton");
    }

    [Fact]
    public async Task AddSharedOrchestrationServices_ConsolidationStalenessService_ResolvesAsItselfSingleton()
    {
        var services = BuildMinimalPrerequisites();
        services.AddSharedOrchestrationServices();

        await using var provider = services.BuildServiceProvider();

        var first = provider.GetRequiredService<ConsolidationStalenessService>();
        var second = provider.GetRequiredService<ConsolidationStalenessService>();

        // BeOfType is not tautological for non-sealed types; kept per issue #3857 requirements.
        first.Should().BeOfType<ConsolidationStalenessService>(
            "ConsolidationStalenessService must resolve to its own concrete type");
        first.Should().BeSameAs(second,
            "ConsolidationStalenessService must be registered as a singleton");
    }

    /// <summary>
    /// Pins the third-argument requirement: <see cref="IDispatchOrchestrationService"/> must
    /// resolve when <see cref="IProviderConfigStore"/> is registered but
    /// <see cref="IConfigurationStore"/> is NOT. If the implementation accidentally resolves
    /// <see cref="IConfigurationStore"/> instead, this test will fail with an
    /// <see cref="InvalidOperationException"/> during service resolution.
    /// </summary>
    [Fact]
    public async Task AddSharedOrchestrationServices_IDispatchOrchestrationService_ResolvesWithProviderConfigStoreButNoConfigurationStore()
    {
        // BuildMinimalPrerequisites intentionally does NOT register IConfigurationStore in DI.
        var services = BuildMinimalPrerequisites();
        services.AddSharedOrchestrationServices();

        await using var provider = services.BuildServiceProvider();

        // Should NOT throw — IDispatchOrchestrationService wires via IProviderConfigStore, not IConfigurationStore.
        var service = provider.GetRequiredService<IDispatchOrchestrationService>();

        service.Should().BeOfType<DispatchOrchestrationService>(
            "IDispatchOrchestrationService must resolve to DispatchOrchestrationService when IProviderConfigStore is registered but IConfigurationStore is not");
    }

    [Fact]
    public void AddSharedOrchestrationServices_ThrowsArgumentNullException_WhenServicesIsNull()
    {
        IServiceCollection? nullServices = null;

        var act = () => nullServices!.AddSharedOrchestrationServices();

        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("services");
    }
}
