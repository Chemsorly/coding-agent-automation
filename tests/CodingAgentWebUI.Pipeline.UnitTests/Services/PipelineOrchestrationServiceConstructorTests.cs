using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.TestUtilities;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Constructor guard-clause tests for <see cref="PipelineOrchestrationService"/>.
/// The label-swap path in CancelPipelineAsync is tested via PipelineOrchestrationServiceTests
/// (which uses TestPipelineRunner for a fully-wired lifecycle).
/// </summary>
public sealed class PipelineOrchestrationServiceConstructorTests
{
    private readonly Mock<IProviderConfigStore> _configStore = new();
    private readonly Mock<IProviderFactory> _providerFactory = new();
    private readonly Mock<IPipelineCancellationFacade> _cancellationFacade = new();
    private readonly Mock<ILabelService> _labelService = new();
    private readonly Mock<IPipelineRunHistoryService> _history = new();

    // ── Constructor guard clauses ─────────────────────────────────────────

    [Fact]
    public void Constructor_NullConfigurationStore_Throws()
    {
        var ex = Record.Exception(() => new PipelineOrchestrationService(
            null!, _providerFactory.Object, _cancellationFacade.Object,
            BuildLifecycle(), _labelService.Object, Serilog.Log.Logger));
        Assert.IsType<ArgumentNullException>(ex);
    }

    [Fact]
    public void Constructor_NullProviderFactory_Throws()
    {
        var ex = Record.Exception(() => new PipelineOrchestrationService(
            _configStore.Object, null!, _cancellationFacade.Object,
            BuildLifecycle(), _labelService.Object, Serilog.Log.Logger));
        Assert.IsType<ArgumentNullException>(ex);
    }

    [Fact]
    public void Constructor_NullCancellationFacade_Throws()
    {
        var ex = Record.Exception(() => new PipelineOrchestrationService(
            _configStore.Object, _providerFactory.Object, null!,
            BuildLifecycle(), _labelService.Object, Serilog.Log.Logger));
        Assert.IsType<ArgumentNullException>(ex);
    }

    [Fact]
    public void Constructor_NullLifecycle_Throws()
    {
        var ex = Record.Exception(() => new PipelineOrchestrationService(
            _configStore.Object, _providerFactory.Object, _cancellationFacade.Object,
            null!, _labelService.Object, Serilog.Log.Logger));
        Assert.IsType<ArgumentNullException>(ex);
    }

    [Fact]
    public void Constructor_NullLabelService_Throws()
    {
        var ex = Record.Exception(() => new PipelineOrchestrationService(
            _configStore.Object, _providerFactory.Object, _cancellationFacade.Object,
            BuildLifecycle(), null!, Serilog.Log.Logger));
        Assert.IsType<ArgumentNullException>(ex);
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var ex = Record.Exception(() => new PipelineOrchestrationService(
            _configStore.Object, _providerFactory.Object, _cancellationFacade.Object,
            BuildLifecycle(), _labelService.Object, null!));
        Assert.IsType<ArgumentNullException>(ex);
    }

    [Fact]
    public void Constructor_AllValid_DoesNotThrow()
    {
        var ex = Record.Exception(() => new PipelineOrchestrationService(
            _configStore.Object, _providerFactory.Object, _cancellationFacade.Object,
            BuildLifecycle(), _labelService.Object, Serilog.Log.Logger));
        Assert.Null(ex);
    }

    // ── CancelPipelineAsync — no active run is a safe no-op ──────────────

    [Fact]
    public async Task CancelPipelineAsync_WithNoActiveRun_IsNoOp()
    {
        var service = TestOrchestrationFactory.CreateMinimal(
            configStore: new Mock<IConfigurationStore>().Object,
            providerFactory: _providerFactory.Object);

        var ex = await Record.ExceptionAsync(() => service.CancelPipelineAsync());
        Assert.Null(ex);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private PipelineRunLifecycleService BuildLifecycle() =>
        new(_history.Object, null, Serilog.Log.Logger);
}
