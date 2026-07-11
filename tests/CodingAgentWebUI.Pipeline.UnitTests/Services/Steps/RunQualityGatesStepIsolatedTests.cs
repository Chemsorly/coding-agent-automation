using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services.Steps;

/// <summary>
/// Isolated unit tests for <see cref="RunQualityGatesStep"/> using mocked <see cref="IQualityGateExecutor"/>.
/// Decision (Issue #297): Approach 1 — interfaces already exist and are mockable.
/// </summary>
public class RunQualityGatesStepIsolatedTests
{
    private readonly Mock<IQualityGateExecutor> _qualityGates = new();
    private readonly Mock<IConfigurationStore> _configStore = new();
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly PipelineRun _run;

    public RunQualityGatesStepIsolatedTests()
    {
        _run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "42",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            CurrentStep = PipelineStep.RunningQualityGates,
            RepositoryName = "owner/repo",
            WorkspacePath = "/tmp/test"
        };
    }

    private PipelineStepContext BuildContext()
    {
        return new PipelineStepContext
        {
            Run = _run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = new CancellationTokenSource(),
            ProviderConfigStore = _configStore.Object,
            QualityGateConfigStore = _configStore.Object,
            ReviewerConfigStore = _configStore.Object,
            Callbacks = _callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = _qualityGates.Object,
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger
        };
    }

    [Fact]
    public async Task ExecuteAsync_UsesPreResolvedQualityGateConfigs_WhenSet()
    {
        var qgcs = new List<QualityGateConfiguration>
        {
            new() { DisplayName = "TestQGC", CompilationCommand = "dotnet build" }
        };

        var context = BuildContext();
        context.PreResolvedQualityGateConfigs = qgcs;

        await new RunQualityGatesStep().ExecuteAsync(context, CancellationToken.None);

        _qualityGates.Verify(x => x.ProceedToQualityGatesAsync(
            It.Is<QualityGateContext>(qgc => qgc.QualityGateConfigs.Count == 1),
            It.IsAny<CancellationToken>()), Times.Once);
        _configStore.Verify(x => x.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_ResolvesFromConfigStore_WhenPreResolvedIsNull()
    {
        var qgcs = new List<QualityGateConfiguration>
        {
            new() { DisplayName = "FromStore", CompilationCommand = "mvn compile" }
        };
        _configStore.Setup(x => x.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(qgcs);

        var context = BuildContext();
        context.PreResolvedQualityGateConfigs = null;

        await new RunQualityGatesStep().ExecuteAsync(context, CancellationToken.None);

        _configStore.Verify(x => x.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()), Times.Once);
        _qualityGates.Verify(x => x.ProceedToQualityGatesAsync(
            It.Is<QualityGateContext>(qgc => qgc.QualityGateConfigs.Count == 1 && qgc.QualityGateConfigs[0].DisplayName == "FromStore"),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsContinue_WhenRunNotInTerminalState()
    {
        var context = BuildContext();
        context.PreResolvedQualityGateConfigs = [];

        var result = await new RunQualityGatesStep().ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsStop_WhenRunTransitionsToFailed()
    {
        _qualityGates
            .Setup(x => x.ProceedToQualityGatesAsync(It.IsAny<QualityGateContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => _run.CurrentStep = PipelineStep.Failed);

        var context = BuildContext();
        context.PreResolvedQualityGateConfigs = [];

        var result = await new RunQualityGatesStep().ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsStop_WhenRunTransitionsToCompleted()
    {
        _qualityGates
            .Setup(x => x.ProceedToQualityGatesAsync(It.IsAny<QualityGateContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => _run.CurrentStep = PipelineStep.Completed);

        var context = BuildContext();
        context.PreResolvedQualityGateConfigs = [];

        var result = await new RunQualityGatesStep().ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsStop_WhenRunTransitionsToCancelled()
    {
        _qualityGates
            .Setup(x => x.ProceedToQualityGatesAsync(It.IsAny<QualityGateContext>(), It.IsAny<CancellationToken>()))
            .Callback(() => _run.CurrentStep = PipelineStep.Cancelled);

        var context = BuildContext();
        context.PreResolvedQualityGateConfigs = [];

        var result = await new RunQualityGatesStep().ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Stop);
    }

    [Fact]
    public async Task ExecuteAsync_PassesCorrectContextProperties_ToQualityGateExecutor()
    {
        var context = BuildContext();
        context.PreResolvedQualityGateConfigs = [];

        await new RunQualityGatesStep().ExecuteAsync(context, CancellationToken.None);

        _qualityGates.Verify(x => x.ProceedToQualityGatesAsync(
            It.Is<QualityGateContext>(qgc =>
                qgc.Run == _run &&
                qgc.Config == context.Config &&
                qgc.Callbacks == _callbacks.Object),
            It.IsAny<CancellationToken>()), Times.Once);
    }
}
