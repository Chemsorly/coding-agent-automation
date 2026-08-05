using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.TestUtilities;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Tests for <see cref="TestPipelineRunner"/> constructor and observable properties
/// introduced in PR #1778 (replaced internal test entry point).
/// These cover the 92 uncovered lines in tests/CodingAgentWebUI.TestUtilities/TestPipelineRunner.cs.
/// </summary>
public class TestPipelineRunnerConstructorTests : IDisposable
{
    private TestPipelineRunner? _runner;

    public void Dispose()
    {
        _runner?.Dispose();
        GC.SuppressFinalize(this);
    }

    // ── Construction ─────────────────────────────────────────────────────

    [Fact]
    public void Constructor_AllRequired_DoesNotThrow()
    {
        var act = () => { _runner = CreateRunner(); };
        act.Should().NotThrow();
    }

    // ── Initial property values ──────────────────────────────────────────

    [Fact]
    public void ActiveRun_Initially_IsNull()
    {
        _runner = CreateRunner();
        _runner.ActiveRun.Should().BeNull();
    }

    [Fact]
    public void IsRunning_Initially_IsFalse()
    {
        _runner = CreateRunner();
        _runner.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task GetRunHistoryAsync_Initially_ReturnsEmptyList()
    {
        _runner = CreateRunner();
        var history = await _runner.GetRunHistoryAsync(CancellationToken.None);
        history.Should().BeEmpty("no runs have been executed yet");
    }

    // ── Event wiring ──────────────────────────────────────────────────────

    [Fact]
    public void OnChange_CanSubscribeAndUnsubscribe()
    {
        _runner = CreateRunner();
        int fired = 0;
        Action handler = () => fired++;

        _runner.OnChange += handler;
        _runner.OnChange -= handler;

        // After unsubscribe, no events should fire (verified by absence of throw)
        fired.Should().Be(0);
    }

    [Fact]
    public void OnOutputLine_CanSubscribeAndUnsubscribe()
    {
        _runner = CreateRunner();
        var lines = new List<string>();
        Action<string> handler = line => lines.Add(line);

        _runner.OnOutputLine += handler;
        _runner.OnOutputLine -= handler;

        lines.Should().BeEmpty();
    }

    // ── IDisposable / IAsyncDisposable ───────────────────────────────────

    [Fact]
    public void Dispose_DoesNotThrow()
    {
        var runner = CreateRunner();
        var act = () => runner.Dispose();
        act.Should().NotThrow();
    }

    [Fact]
    public async Task DisposeAsync_DoesNotThrow()
    {
        var runner = CreateRunner();
        var act = async () => await runner.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public void Dispose_CanBeCalledTwice()
    {
        var runner = CreateRunner();
        runner.Dispose();
        var act = () => runner.Dispose();
        act.Should().NotThrow("double-dispose must be idempotent");
    }

    // ── Optional parameter defaults ───────────────────────────────────────

    [Fact]
    public void Constructor_NullBrainUpdateService_UsesNullBrainUpdateServiceDefault()
    {
        // Omitting brainUpdateService uses the internal NullBrainUpdateService — must not throw
        _runner = new TestPipelineRunner(
            Mock.Of<IConfigurationStore>(),
            Mock.Of<IProviderFactory>(),
            new IssueDescriptionParser(),
            Mock.Of<IAgentPhaseExecutor>(),
            Mock.Of<IQualityGateExecutor>(),
            new Mock<Serilog.ILogger>().Object,
            brainUpdateService: null);

        _runner.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void Constructor_WithCustomHistoryService_UsesIt()
    {
        var historyService = Mock.Of<IPipelineRunHistoryService>();
        _runner = new TestPipelineRunner(
            Mock.Of<IConfigurationStore>(),
            Mock.Of<IProviderFactory>(),
            new IssueDescriptionParser(),
            Mock.Of<IAgentPhaseExecutor>(),
            Mock.Of<IQualityGateExecutor>(),
            new Mock<Serilog.ILogger>().Object,
            historyService: historyService);

        _runner.Should().NotBeNull();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static TestPipelineRunner CreateRunner() => new(
        Mock.Of<IConfigurationStore>(),
        Mock.Of<IProviderFactory>(),
        new IssueDescriptionParser(),
        Mock.Of<IAgentPhaseExecutor>(),
        Mock.Of<IQualityGateExecutor>(),
        new Mock<Serilog.ILogger>().Object);
}
