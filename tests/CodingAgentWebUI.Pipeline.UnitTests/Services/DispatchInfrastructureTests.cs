using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Verifies that <see cref="DispatchInfrastructure"/> correctly aggregates shared
/// dispatch dependencies, reducing constructor bloat in AgentJobDispatcher (11→8 deps)
/// and DispatchOrchestrationService (7→4 deps).
/// </summary>
public class DispatchInfrastructureTests
{
    private readonly Mock<ITokenVendingService> _mockTokenVending = new();
    private readonly Mock<IProviderFactory> _mockProviderFactory = new();
    private readonly Mock<ILabelSwapper> _mockLabelSwapper = new();
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();

    private DispatchInfrastructure CreateInfrastructure()
    {
        var resolution = new DispatchResolutionService(
            new ProfileResolver(),
            new QualityGateResolver(),
            new ReviewerResolver(),
            _mockConfigStore.Object,
            _mockConfigStore.Object,
            _mockConfigStore.Object,
            _mockConfigStore.Object,
            _mockConfigStore.Object,
            _mockConfigStore.Object,
            new Mock<ILogger>().Object);

        return new DispatchInfrastructure(
            _mockTokenVending.Object,
            _mockProviderFactory.Object,
            _mockLabelSwapper.Object,
            resolution);
    }

    // ── Construction ──

    [Fact]
    public void Constructor_NullTokenVending_Throws()
    {
        var resolution = new DispatchResolutionService(
            new ProfileResolver(), new QualityGateResolver(), new ReviewerResolver(),
            _mockConfigStore.Object, _mockConfigStore.Object, _mockConfigStore.Object,
            _mockConfigStore.Object, _mockConfigStore.Object, _mockConfigStore.Object,
            new Mock<ILogger>().Object);

        var act = () => new DispatchInfrastructure(
            null!, _mockProviderFactory.Object, _mockLabelSwapper.Object, resolution);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullProviderFactory_Throws()
    {
        var resolution = new DispatchResolutionService(
            new ProfileResolver(), new QualityGateResolver(), new ReviewerResolver(),
            _mockConfigStore.Object, _mockConfigStore.Object, _mockConfigStore.Object,
            _mockConfigStore.Object, _mockConfigStore.Object, _mockConfigStore.Object,
            new Mock<ILogger>().Object);

        var act = () => new DispatchInfrastructure(
            _mockTokenVending.Object, null!, _mockLabelSwapper.Object, resolution);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLabelSwapper_Throws()
    {
        var resolution = new DispatchResolutionService(
            new ProfileResolver(), new QualityGateResolver(), new ReviewerResolver(),
            _mockConfigStore.Object, _mockConfigStore.Object, _mockConfigStore.Object,
            _mockConfigStore.Object, _mockConfigStore.Object, _mockConfigStore.Object,
            new Mock<ILogger>().Object);

        var act = () => new DispatchInfrastructure(
            _mockTokenVending.Object, _mockProviderFactory.Object, null!, resolution);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullResolution_Throws()
    {
        var act = () => new DispatchInfrastructure(
            _mockTokenVending.Object, _mockProviderFactory.Object, _mockLabelSwapper.Object, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── Property Access ──

    [Fact]
    public void Properties_ExposeInjectedDependencies()
    {
        var infra = CreateInfrastructure();

        infra.TokenVending.Should().BeSameAs(_mockTokenVending.Object);
        infra.ProviderFactory.Should().BeSameAs(_mockProviderFactory.Object);
        infra.LabelSwapper.Should().BeSameAs(_mockLabelSwapper.Object);
        infra.Resolution.Should().NotBeNull();
    }

    // ── ConfigStore convenience accessor ──

    [Fact]
    public void ProviderConfigStore_DelegatesToResolution()
    {
        // TODO: Only 1 of 6 store properties is asserted. Add assertions for AgentProfileStore, QualityGateConfigStore, ReviewerConfigStore, PipelineConfigStore, and ProjectStore.
        var infra = CreateInfrastructure();

        // DispatchResolutionService.ProviderConfigStore is the same store passed in
        infra.Resolution.ProviderConfigStore.Should().BeSameAs(_mockConfigStore.Object);
    }
}
