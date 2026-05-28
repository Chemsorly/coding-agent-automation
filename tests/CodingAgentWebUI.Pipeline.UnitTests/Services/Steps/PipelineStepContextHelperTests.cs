using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services.Steps;

public class PipelineStepContextHelperTests
{
    private readonly Mock<IRepositoryProvider> _repoProvider = new();
    private readonly Mock<IAgentProvider> _agentProvider = new();
    private readonly Mock<IConfigurationStore> _configStore = new();
    private readonly Mock<IAgentIssueOperations> _issueOps = new();
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly PipelineRun _run;

    public PipelineStepContextHelperTests()
    {
        _run = new PipelineRun
        {
            RunId = "test-run-id",
            IssueIdentifier = "42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "issue-config",
            RepoProviderConfigId = "repo-config",
            StartedAt = DateTime.UtcNow,
            CurrentStep = PipelineStep.Created,
            RepositoryName = "owner/repo"
        };

        _callbacks.Setup(c => c.SwapAgentLabel(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private PipelineStepContext BuildContext()
    {
        var prOrchestrator = new PullRequestOrchestrator(_logger);
        return new PipelineStepContext
        {
            Run = _run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = Path.GetTempPath() },
            RepoProvider = _repoProvider.Object,
            AgentProvider = _agentProvider.Object,
            BrainProvider = null,
            PipelineProvider = null,
            Cts = new CancellationTokenSource(),
            ConfigStore = _configStore.Object,
            IssueProvider = null,
            Callbacks = _callbacks.Object,
            IssueOps = _issueOps.Object,
            AgentExecution = new AgentPhaseExecutor(_logger),
            QualityGates = new QualityGateExecutor(
                Mock.Of<IQualityGateValidator>(), prOrchestrator, _logger),
            BrainSync = null,
            PrOrchestrator = prOrchestrator,
            Logger = _logger
        };
    }

    // ── TryCriticalAsync ──

    [Fact]
    public async Task TryCriticalAsync_Success_ReturnsContinue()
    {
        var context = BuildContext();
        var result = await context.TryCriticalAsync(() => Task.CompletedTask, "test action");
        result.Should().Be(StepResult.Continue);
    }

    [Fact]
    public async Task TryCriticalAsync_Exception_ReturnsStop()
    {
        var context = BuildContext();
        var result = await context.TryCriticalAsync(
            () => throw new InvalidOperationException("boom"), "test action");
        result.Should().Be(StepResult.Stop);
    }

    [Fact]
    public async Task TryCriticalAsync_Exception_SetsFailureReason()
    {
        var context = BuildContext();
        await context.TryCriticalAsync(
            () => throw new InvalidOperationException("boom"), "test action");
        _run.FailureReason.Should().Be("test action failed: boom");
    }

    [Fact]
    public async Task TryCriticalAsync_OperationCanceledException_Propagates()
    {
        var context = BuildContext();
        var act = () => context.TryCriticalAsync(
            () => throw new OperationCanceledException(), "test action");
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ── TryNonCriticalAsync ──

    [Fact]
    public async Task TryNonCriticalAsync_Success_ReturnsContinue()
    {
        var context = BuildContext();
        var result = await context.TryNonCriticalAsync(() => Task.CompletedTask, "test action");
        result.Should().Be(StepResult.Continue);
    }

    [Fact]
    public async Task TryNonCriticalAsync_Exception_ReturnsContinue()
    {
        var context = BuildContext();
        var result = await context.TryNonCriticalAsync(
            () => throw new InvalidOperationException("boom"), "test action");
        result.Should().Be(StepResult.Continue);
    }

    [Fact]
    public async Task TryNonCriticalAsync_Exception_InvokesOnFailure()
    {
        var context = BuildContext();
        var called = false;
        await context.TryNonCriticalAsync(
            () => throw new InvalidOperationException("boom"), "test action",
            onFailure: () => called = true);
        called.Should().BeTrue();
    }

    [Fact]
    public async Task TryNonCriticalAsync_Success_DoesNotInvokeOnFailure()
    {
        var context = BuildContext();
        var called = false;
        await context.TryNonCriticalAsync(
            () => Task.CompletedTask, "test action",
            onFailure: () => called = true);
        called.Should().BeFalse();
    }

    [Fact]
    public async Task TryNonCriticalAsync_OperationCanceledException_Propagates()
    {
        var context = BuildContext();
        var act = () => context.TryNonCriticalAsync(
            () => throw new OperationCanceledException(), "test action");
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
