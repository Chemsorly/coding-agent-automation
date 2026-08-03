using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.TestUtilities;
using Moq;

namespace CodingAgentWebUI.UnitTests.TestUtilitiesTests;

/// <summary>
/// Tests for <see cref="TestPipelineRunner"/>.
/// Exercises construction and basic property/event access without running a real pipeline.
/// </summary>
public class TestPipelineRunnerTests : IDisposable
{
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();
    private readonly Mock<IProviderFactory> _mockProviderFactory = new();
    private readonly Mock<IAgentPhaseExecutor> _mockAgentExecution = new();
    private readonly Mock<IQualityGateExecutor> _mockQualityGates = new();
    private readonly Mock<Serilog.ILogger> _mockLogger = new();

    private TestPipelineRunner CreateSut() => new(
        configStore: _mockConfigStore.Object,
        providerFactory: _mockProviderFactory.Object,
        issueParser: new IssueDescriptionParser(),
        agentExecution: _mockAgentExecution.Object,
        qualityGates: _mockQualityGates.Object,
        logger: _mockLogger.Object);

    // ── Construction ─────────────────────────────────────────────────────

    [Fact]
    public void Constructor_WithRequiredDeps_DoesNotThrow()
    {
        using var runner = CreateSut();
        runner.Should().NotBeNull();
    }

    [Fact]
    public void Constructor_WithOptionalDeps_DoesNotThrow()
    {
        var historyService = new TestOrchestrationFactory.NullHistoryService();

        using var runner = new TestPipelineRunner(
            configStore: _mockConfigStore.Object,
            providerFactory: _mockProviderFactory.Object,
            issueParser: new IssueDescriptionParser(),
            agentExecution: _mockAgentExecution.Object,
            qualityGates: _mockQualityGates.Object,
            logger: _mockLogger.Object,
            historyService: historyService);

        runner.Should().NotBeNull();
    }

    // ── Initial state ─────────────────────────────────────────────────────

    [Fact]
    public void ActiveRun_InitiallyNull()
    {
        using var runner = CreateSut();
        runner.ActiveRun.Should().BeNull();
    }

    [Fact]
    public void IsRunning_InitiallyFalse()
    {
        using var runner = CreateSut();
        runner.IsRunning.Should().BeFalse();
    }

    // ── Events ────────────────────────────────────────────────────────────

    [Fact]
    public void OnChange_CanSubscribeAndUnsubscribe()
    {
        using var runner = CreateSut();
        Action handler = () => { };

        // Should not throw
        runner.OnChange += handler;
        runner.OnChange -= handler;
    }

    [Fact]
    public void OnOutputLine_CanSubscribeAndUnsubscribe()
    {
        using var runner = CreateSut();
        Action<string> handler = _ => { };

        runner.OnOutputLine += handler;
        runner.OnOutputLine -= handler;
    }

    // ── GetRunHistoryAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task GetRunHistoryAsync_InitiallyEmpty()
    {
        using var runner = CreateSut();
        var history = await runner.GetRunHistoryAsync();
        history.Should().BeEmpty();
    }

    // ── Dispose ───────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var runner = CreateSut();
        var act = () => runner.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var runner = CreateSut();
        await runner.DisposeAsync();
    }

    public void Dispose() { }
}
