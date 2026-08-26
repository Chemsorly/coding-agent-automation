using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Pipeline.UnitTests.Steps;

/// <summary>
/// Unit tests for <see cref="WriteOpenIssueContextStep"/>.
/// Covers null guard, success paths (implementation + decomposition), transition call,
/// and count propagation.
/// </summary>
public class WriteOpenIssueContextStepTests
{
    private static readonly ILogger Logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Mock<IOpenIssueContextWriter> _writer = new();

    public WriteOpenIssueContextStepTests()
    {
        _callbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()));
        _callbacks.Setup(c => c.EmitOutputLine(It.IsAny<string>()));
    }

    private PipelineStepContext BuildContext(PipelineRunType runType = PipelineRunType.Implementation) =>
        new()
        {
            Run = new PipelineRun
            {
                RunId = Guid.NewGuid().ToString(),
                IssueIdentifier = "42",
                IssueTitle = "Test",
                IssueProviderConfigId = "ip",
                RepoProviderConfigId = "rp",
                StartedAt = DateTime.UtcNow,
                RunType = runType,
                WorkspacePath = "/tmp/ws"
            },
            Config = new PipelineConfiguration
            {
                WorkspaceBaseDirectory = "/tmp",
                MaxOpenIssuesForContext = 25
            },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = null,
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = _callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(Logger),
            Logger = Logger
        };

    // ── Constructor ───────────────────────────────────────────────────────

    [Fact]
    public void Ctor_NullWriter_Throws()
    {
        var act = () => new WriteOpenIssueContextStep(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── ExecuteAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ImplementationRun_CallsWriterWithoutClosedSiblings()
    {
        _writer
            .Setup(w => w.WriteOpenIssueContextAsync(
                It.IsAny<IAgentIssueOperations>(), "/tmp/ws", 25, false, It.IsAny<CancellationToken>()))
            .ReturnsAsync(7);

        var step = new WriteOpenIssueContextStep(_writer.Object);
        var context = BuildContext(PipelineRunType.Implementation);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        context.Run.OpenIssuesDownloaded.Should().Be(7, "result of WriteOpenIssueContextAsync must be stored on the run");
        _callbacks.Verify(c => c.TransitionTo(PipelineStep.DownloadingOpenIssues), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_DecompositionAnalysisRun_IncludesClosedSiblings()
    {
        _writer
            .Setup(w => w.WriteOpenIssueContextAsync(
                It.IsAny<IAgentIssueOperations>(), It.IsAny<string>(), It.IsAny<int>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(3);

        var step = new WriteOpenIssueContextStep(_writer.Object);
        var context = BuildContext(PipelineRunType.DecompositionAnalysis);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        _writer.Verify(w => w.WriteOpenIssueContextAsync(
            It.IsAny<IAgentIssueOperations>(), It.IsAny<string>(), It.IsAny<int>(),
            true, // includeClosedSiblings = true for DecompositionAnalysis
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_DecompositionRun_IncludesClosedSiblings()
    {
        _writer
            .Setup(w => w.WriteOpenIssueContextAsync(
                It.IsAny<IAgentIssueOperations>(), It.IsAny<string>(), It.IsAny<int>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);

        var step = new WriteOpenIssueContextStep(_writer.Object);
        var context = BuildContext(PipelineRunType.Decomposition);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        _writer.Verify(w => w.WriteOpenIssueContextAsync(
            It.IsAny<IAgentIssueOperations>(), It.IsAny<string>(), It.IsAny<int>(),
            true, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_PassesMaxIssuesFromConfig()
    {
        _writer
            .Setup(w => w.WriteOpenIssueContextAsync(
                It.IsAny<IAgentIssueOperations>(), It.IsAny<string>(), 25, It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var step = new WriteOpenIssueContextStep(_writer.Object);
        var context = BuildContext(); // Config.MaxOpenIssuesForContext = 25

        await step.ExecuteAsync(context, CancellationToken.None);

        _writer.Verify(w => w.WriteOpenIssueContextAsync(
            It.IsAny<IAgentIssueOperations>(), It.IsAny<string>(), 25, It.IsAny<bool>(), It.IsAny<CancellationToken>()),
            Times.Once, "MaxOpenIssuesForContext from Config must be passed to the writer");
    }

    [Fact]
    public async Task ExecuteAsync_ReviewRun_ExcludesClosedSiblings()
    {
        _writer
            .Setup(w => w.WriteOpenIssueContextAsync(
                It.IsAny<IAgentIssueOperations>(), It.IsAny<string>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(0);

        var step = new WriteOpenIssueContextStep(_writer.Object);
        var context = BuildContext(PipelineRunType.Review);

        await step.ExecuteAsync(context, CancellationToken.None);

        _writer.Verify(w => w.WriteOpenIssueContextAsync(
            It.IsAny<IAgentIssueOperations>(), It.IsAny<string>(), It.IsAny<int>(),
            false, // Review is not epic-scoped → no closed siblings
            It.IsAny<CancellationToken>()), Times.Once);
    }

    // ── IsEpicScopedRun static ──────────────────────────────────────────

    [Theory]
    [InlineData(PipelineRunType.DecompositionAnalysis, true)]
    [InlineData(PipelineRunType.Decomposition, true)]
    [InlineData(PipelineRunType.Implementation, false)]
    [InlineData(PipelineRunType.Review, false)]
    public void IsEpicScopedRun_ReturnsExpected(PipelineRunType runType, bool expected)
    {
        WriteOpenIssueContextStep.IsEpicScopedRun(runType).Should().Be(expected);
    }
}
