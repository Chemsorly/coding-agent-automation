using System.Collections.Concurrent;
using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using CodingAgent.Pipeline.Services.Steps;
using Moq;

namespace CodingAgent.Pipeline.UnitTests.Services.Steps;

/// <summary>
/// Tests are in a single collection so they run sequentially. Each test creates a background Task.Run
/// via BackgroundBaseline.Start; on a 1.5-CPU pod, many concurrent Task.Run calls cause the 100 ms
/// Regex timeout in BrainLineFormat.TryParseSkillLine to fire under thread-pool pressure, breaking
/// BrainSkillsReaderTests that run in parallel. Sequential execution keeps the peak concurrent
/// background-task count at 1.
/// </summary>
[Collection("VerifyBaselineStepTests")]
public class VerifyBaselineStepTests
{
    private readonly Mock<IQualityGateValidator> _validator = new();
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Mock<IConfigurationStore> _configStore = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly ConcurrentQueue<string> _outputLines = new();
    private readonly List<PipelineStep> _transitions = [];

    public VerifyBaselineStepTests()
    {
        _callbacks.Setup(c => c.EmitOutputLine(It.IsAny<string>()))
            .Callback<string>(line => _outputLines.Enqueue(line));
        _callbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()))
            .Callback<PipelineStep>(step => _transitions.Add(step));
        _callbacks.Setup(c => c.SwapAgentLabel(It.IsAny<IssueIdentifier>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    /// <summary>Runs the step and then the wait that follows it in the pipeline, so the baseline's result is in.</summary>
    private static async Task<StepResult> ExecuteAndAwaitBaselineAsync(VerifyBaselineStep step, PipelineStepContext context)
    {
        var result = await step.ExecuteAsync(context, CancellationToken.None);
        await new AwaitBaselineStep().ExecuteAsync(context, CancellationToken.None);
        return result;
    }

    private PipelineStepContext BuildContext(
        bool baselineEnabled = true,
        Mock<IAgentProvider>? agentProviderMock = null,
        IReadOnlyList<QualityGateConfiguration>? preResolvedQgcs = null)
    {
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "42",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            CurrentStep = PipelineStep.CreatingBranch,
            WorkspacePath = "/tmp/workspace",
            RepositoryName = "owner/repo"
        };

        var config = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = "/tmp",
            BaselineHealthCheckEnabled = baselineEnabled
        };

        var agentProvider = agentProviderMock?.Object ?? Mock.Of<IAgentProvider>();

        return new PipelineStepContext
        {
            Run = run,
            Config = config,
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = agentProvider,
            BrainProvider = null,
            PipelineProvider = null,
            Cts = new CancellationTokenSource(),
            ConfigStore = _configStore.Object,
            Callbacks = _callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger,
            QualityGateValidator = _validator.Object,
            PreResolvedQualityGateConfigs = preResolvedQgcs
        };
    }

    [Fact]
    public async Task ExecuteAsync_WhenDisabled_SkipsAndReturnsContinue()
    {
        var context = BuildContext(baselineEnabled: false);
        var step = new VerifyBaselineStep();

        var result = await ExecuteAndAwaitBaselineAsync(step, context);

        Assert.Equal(StepResult.Continue, result);
        _transitions.Should().BeEmpty();
        _outputLines.Should().Contain(l => l.Contains("disabled"));
    }

    [Fact]
    public async Task ExecuteAsync_NoDoctorNoQgcs_ReturnsContinue()
    {
        var context = BuildContext(preResolvedQgcs: []);
        var step = new VerifyBaselineStep();

        var result = await ExecuteAndAwaitBaselineAsync(step, context);

        Assert.Equal(StepResult.Continue, result);
        _transitions.Should().Contain(PipelineStep.VerifyingBaseline);
        Assert.Null(context.Run.BaselineHealthPassed);
    }

    [Fact]
    public async Task ExecuteAsync_BaselineSucceeds_SetsBaselineHealthPassedTrue()
    {
        var qgc = CreateQgc();
        var context = BuildContext(preResolvedQgcs: [qgc]);

        _validator.Setup(v => v.ValidateAsync(It.IsAny<WorkspacePath>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>(), It.IsAny<Action<PipelineRunEventReport>?>()))
            .ReturnsAsync(CreatePassingReport());

        var step = new VerifyBaselineStep();
        var result = await ExecuteAndAwaitBaselineAsync(step, context);

        Assert.Equal(StepResult.Continue, result);
        Assert.True(context.Run.BaselineHealthPassed);
        _outputLines.Should().Contain(l => l.Contains("✅ Workspace baseline healthy"));
    }

    [Fact]
    public async Task ExecuteAsync_BaselineFails_ReturnsContinueWithBaselineHealthFalse()
    {
        var qgc = CreateQgc();
        var context = BuildContext(preResolvedQgcs: [qgc]);

        _validator.Setup(v => v.ValidateAsync(It.IsAny<WorkspacePath>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>(), It.IsAny<Action<PipelineRunEventReport>?>()))
            .ReturnsAsync(CreateFailingReport());

        var step = new VerifyBaselineStep();
        var result = await ExecuteAndAwaitBaselineAsync(step, context);

        Assert.Equal(StepResult.Continue, result);
        Assert.False(context.Run.BaselineHealthPassed);
        _outputLines.Should().Contain(l => l.Contains("⚠️") && l.Contains("non-fatal"));
    }

    [Fact]
    public async Task ExecuteAsync_BaselineThrows_ReturnsContinueWithBaselineHealthFalse()
    {
        var qgc = CreateQgc();
        var context = BuildContext(preResolvedQgcs: [qgc]);

        _validator.Setup(v => v.ValidateAsync(It.IsAny<WorkspacePath>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>(), It.IsAny<Action<PipelineRunEventReport>?>()))
            .ThrowsAsync(new InvalidOperationException("build tool not found"));

        var step = new VerifyBaselineStep();
        var result = await ExecuteAndAwaitBaselineAsync(step, context);

        Assert.Equal(StepResult.Continue, result);
        Assert.False(context.Run.BaselineHealthPassed);
    }

    [Fact]
    public async Task ExecuteAsync_UsesPreResolvedQgcs_WhenAvailable()
    {
        var qgc = CreateQgc();
        var context = BuildContext(preResolvedQgcs: [qgc]);

        _validator.Setup(v => v.ValidateAsync((WorkspacePath)"/tmp/workspace", It.Is<IReadOnlyList<QualityGateConfiguration>>(l => l.Count == 1 && l[0] == qgc), It.IsAny<CancellationToken>(), It.IsAny<Action<PipelineRunEventReport>?>()))
            .ReturnsAsync(CreatePassingReport());

        var step = new VerifyBaselineStep();
        await ExecuteAndAwaitBaselineAsync(step, context);

        _validator.Verify(v => v.ValidateAsync((WorkspacePath)"/tmp/workspace", It.Is<IReadOnlyList<QualityGateConfiguration>>(l => l[0] == qgc), It.IsAny<CancellationToken>(), It.IsAny<Action<PipelineRunEventReport>?>()), Times.Once);
        _configStore.Verify(c => c.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_LoadsQgcsFromStore_WhenNotPreResolved()
    {
        var qgc = CreateQgc();
        var context = BuildContext(preResolvedQgcs: null);

        _configStore.Setup(c => c.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QualityGateConfiguration> { qgc });
        _validator.Setup(v => v.ValidateAsync(It.IsAny<WorkspacePath>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>(), It.IsAny<Action<PipelineRunEventReport>?>()))
            .ReturnsAsync(CreatePassingReport());

        var step = new VerifyBaselineStep();
        await ExecuteAndAwaitBaselineAsync(step, context);

        _configStore.Verify(c => c.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_NeverInvokesExternalCi()
    {
        var qgc = CreateQgc();
        var context = BuildContext(preResolvedQgcs: [qgc]);

        _validator.Setup(v => v.ValidateAsync(It.IsAny<WorkspacePath>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>(), It.IsAny<Action<PipelineRunEventReport>?>()))
            .ReturnsAsync(CreatePassingReport());

        var step = new VerifyBaselineStep();
        await ExecuteAndAwaitBaselineAsync(step, context);

        // ValidateAsync is called (local gates only) — external CI is never invoked
        // because VerifyBaselineStep calls IQualityGateValidator directly, not IQualityGateExecutor
        _validator.Verify(v => v.ValidateAsync(It.IsAny<WorkspacePath>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>(), It.IsAny<Action<PipelineRunEventReport>?>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_TransitionsToVerifyingBaseline()
    {
        var context = BuildContext(preResolvedQgcs: [CreateQgc()]);

        _validator.Setup(v => v.ValidateAsync(It.IsAny<WorkspacePath>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>(), It.IsAny<Action<PipelineRunEventReport>?>()))
            .ReturnsAsync(CreatePassingReport());

        var step = new VerifyBaselineStep();
        await ExecuteAndAwaitBaselineAsync(step, context);

        _transitions.Should().Contain(PipelineStep.VerifyingBaseline);
    }

    [Fact]
    public async Task ExecuteAsync_NoValidator_SetsBaselineHealthPassedNull()
    {
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "42",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            CurrentStep = PipelineStep.CreatingBranch,
            WorkspacePath = "/tmp/workspace",
            RepositoryName = "owner/repo"
        };

        var context = new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = new CancellationTokenSource(),
            ConfigStore = _configStore.Object,
            Callbacks = _callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger,
            QualityGateValidator = null
        };

        var step = new VerifyBaselineStep();
        var result = await ExecuteAndAwaitBaselineAsync(step, context);

        Assert.Equal(StepResult.Continue, result);
        Assert.Null(context.Run.BaselineHealthPassed);
        _outputLines.Should().Contain(l => l.Contains("⏭️") && l.Contains("skipped"));
    }

    [Fact]
    public async Task ExecuteAsync_EmptyWorkspacePath_EmitsSkipReasonAndSetsBaselineNull()
    {
        var run = new PipelineRun
        {
            RunId = "test-run",
            IssueIdentifier = "42",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow,
            CurrentStep = PipelineStep.CreatingBranch,
            WorkspacePath = "",
            RepositoryName = "owner/repo"
        };

        var context = new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = new CancellationTokenSource(),
            ConfigStore = _configStore.Object,
            Callbacks = _callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger,
            QualityGateValidator = _validator.Object
        };

        var step = new VerifyBaselineStep();
        var result = await ExecuteAndAwaitBaselineAsync(step, context);

        Assert.Equal(StepResult.Continue, result);
        Assert.Null(context.Run.BaselineHealthPassed);
        _outputLines.Should().Contain(l => l.Contains("⏭️") && l.Contains("skipped"));
    }

    [Fact]
    public async Task ExecuteAsync_WarmUpRefusesConfiguration_StopsBeforeWorkspaceBaseline()
    {
        var agentProviderMock = new Mock<IAgentProvider>();
        agentProviderMock.Setup(a => a.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        agentProviderMock.Setup(a => a.EnsureSessionAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ProviderUnavailableException(AgentErrorCategory.ProviderMisconfigured, "kiro-cli refused its configuration: x"));

        var context = BuildContext(agentProviderMock: agentProviderMock, preResolvedQgcs: [CreateQgc()]);
        var step = new VerifyBaselineStep();

        var result = await ExecuteAndAwaitBaselineAsync(step, context);

        Assert.Equal(StepResult.Stop, result);
        Assert.Equal(FailureReason.InfrastructureFailure, context.Run.FailureCategory);
        context.Run.FailureReason.Should().Contain("refused its configuration");
        _validator.Verify(v => v.ValidateAsync(
            It.IsAny<WorkspacePath>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>(), It.IsAny<Action<PipelineRunEventReport>?>()),
            Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_HealthyAgent_WarmsUpSessionBeforeWorkspaceBaseline()
    {
        var callOrder = new List<string>();
        var agentProviderMock = new Mock<IAgentProvider>();
        agentProviderMock.Setup(a => a.EnsureSessionAsync(It.IsAny<WorkspacePath>(), It.IsAny<CancellationToken>()))
            .Callback(() => callOrder.Add("EnsureSession"))
            .Returns(Task.CompletedTask);
        _validator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>(), It.IsAny<Action<PipelineRunEventReport>?>()))
            .Callback(() => callOrder.Add("ValidateQgc"))
            .ReturnsAsync(CreatePassingReport());

        var context = BuildContext(agentProviderMock: agentProviderMock, preResolvedQgcs: [CreateQgc()]);
        var step = new VerifyBaselineStep();

        await ExecuteAndAwaitBaselineAsync(step, context);

        agentProviderMock.Verify(a => a.EnsureSessionAsync(
            (WorkspacePath)"/tmp/workspace", It.IsAny<CancellationToken>()), Times.Once);
        callOrder.Should().HaveCount(2);
        callOrder[0].Should().Be("EnsureSession");
        callOrder[1].Should().Be("ValidateQgc");
    }

    [Fact]
    public async Task ExecuteAsync_AgentValidateFails_ReturnsStopWithClearError()
    {
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.ValidateAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("An error occurred trying to start process '/home/ubuntu/.local/bin/kiro-cli' with working directory '/app'. No such file or directory"));

        var context = BuildContext(agentProviderMock: mockAgent, preResolvedQgcs: [CreateQgc()]);

        var step = new VerifyBaselineStep();
        var result = await ExecuteAndAwaitBaselineAsync(step, context);

        Assert.Equal(StepResult.Stop, result);
        context.Run.FailureReason.Should().Contain("Agent environment unhealthy");
    }

    // ── New tests ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_WhileWorkspaceBaselineRuns_ReturnsAndLeavesTheResultOpen()
    {
        var tcs = new TaskCompletionSource<QualityGateReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var qgc = CreateQgc();
        var context = BuildContext(preResolvedQgcs: [qgc]);

        _validator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<PipelineRunEventReport>?>()))
            .Returns<WorkspacePath, IReadOnlyList<QualityGateConfiguration>, CancellationToken, Action<PipelineRunEventReport>?>(
                async (_, _, ct, _) => await tcs.Task.WaitAsync(ct));

        var step = new VerifyBaselineStep();

        // Run only VerifyBaselineStep — baseline should be running in the background
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        Assert.NotNull(context.RunningBaseline);
        Assert.Null(context.Run.BaselineHealthPassed);

        // Now finish the baseline and run AwaitBaselineStep
        tcs.SetResult(CreatePassingReport());
        await new AwaitBaselineStep().ExecuteAsync(context, CancellationToken.None);

        Assert.True(context.Run.BaselineHealthPassed);
        Assert.Null(context.RunningBaseline);
    }

    [Fact]
    public async Task AwaitBaselineStep_WhenBaselineStillRuns_WaitsWithoutChangingTheStep()
    {
        var tcs = new TaskCompletionSource<QualityGateReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var qgc = CreateQgc();
        var context = BuildContext(preResolvedQgcs: [qgc]);

        _validator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<PipelineRunEventReport>?>()))
            .Returns<WorkspacePath, IReadOnlyList<QualityGateConfiguration>, CancellationToken, Action<PipelineRunEventReport>?>(
                async (_, _, ct, _) => await tcs.Task.WaitAsync(ct));

        var step = new VerifyBaselineStep();
        await step.ExecuteAsync(context, CancellationToken.None);

        // Clear transitions so we only track what AwaitBaselineStep does
        _transitions.Clear();

        var awaitTask = new AwaitBaselineStep().ExecuteAsync(context, CancellationToken.None);

        // Task should not be completed yet — baseline is still pending
        Assert.False(awaitTask.IsCompleted);

        // No additional transitions should have occurred
        _transitions.Should().BeEmpty();

        // Complete baseline with failure, then await
        tcs.SetResult(CreateFailingReport());
        await awaitTask;

        _outputLines.Should().Contain(l => l.Contains("waiting for the workspace baseline"));
        Assert.False(context.Run.BaselineHealthPassed);
    }

    [Fact]
    public async Task AwaitBaselineStep_WhenNoBaselineRuns_DoesNothing()
    {
        var context = BuildContext(baselineEnabled: false);
        _transitions.Clear();

        var result = await new AwaitBaselineStep().ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        _transitions.Should().BeEmpty();
    }

    [Fact]
    public async Task Runner_RunsAnalysisWhileBaselineRuns_AndCodeGenerationOnlyAfterIt()
    {
        var tcs = new TaskCompletionSource<QualityGateReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        var qgc = CreateQgc();
        var context = BuildContext(preResolvedQgcs: [qgc]);

        _validator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<PipelineRunEventReport>?>()))
            .Returns<WorkspacePath, IReadOnlyList<QualityGateConfiguration>, CancellationToken, Action<PipelineRunEventReport>?>(
                async (_, _, ct, _) => await tcs.Task.WaitAsync(ct));

        bool? analysisSeesBaselineRunning = null;
        bool? codeGenSeesBaselineDone = null;

        var steps = new IPipelineStep[]
        {
            new VerifyBaselineStep(),
            new DelegateStep("AnalyzeCode", ctx =>
            {
                analysisSeesBaselineRunning = ctx.RunningBaseline?.IsCompleted == false;
                tcs.SetResult(CreatePassingReport());
                return StepResult.Continue;
            }),
            new AwaitBaselineStep(),
            new DelegateStep("GenerateCode", ctx =>
            {
                codeGenSeesBaselineDone = ctx.RunningBaseline is null && ctx.Run.BaselineHealthPassed == true;
                return StepResult.Continue;
            })
        };

        await PipelineStepRunner.ExecuteAsync(steps, context, CancellationToken.None);

        Assert.True(analysisSeesBaselineRunning, "During analysis the baseline should still be running");
        Assert.True(codeGenSeesBaselineDone, "During code gen the baseline should be done and passed");
    }

    [Fact]
    public async Task Runner_WhenAnalysisStopsTheRun_CancelsTheBaselineAndWaitsForItsEnd()
    {
        var qgc = CreateQgc();
        var context = BuildContext(preResolvedQgcs: [qgc]);

        var ended = false;
        _validator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<PipelineRunEventReport>?>()))
            .Returns<WorkspacePath, IReadOnlyList<QualityGateConfiguration>, CancellationToken, Action<PipelineRunEventReport>?>(
                async (_, _, ct, _) =>
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, ct);
                        return CreatePassingReport();
                    }
                    finally
                    {
                        ended = true;
                    }
                });

        var steps = new IPipelineStep[]
        {
            new VerifyBaselineStep(),
            new DelegateStep("AnalyzeCode", _ => StepResult.Stop)
        };

        await PipelineStepRunner.ExecuteAsync(steps, context, CancellationToken.None);

        Assert.True(ended, "Baseline process must end when the run stops");
        Assert.Null(context.RunningBaseline);
        Assert.Null(context.Run.BaselineHealthPassed);
    }

    [Fact]
    public async Task Runner_WhenStepThrows_StillStopsTheBaseline()
    {
        var qgc = CreateQgc();
        var context = BuildContext(preResolvedQgcs: [qgc]);

        var ended = false;
        _validator.Setup(v => v.ValidateAsync(
                It.IsAny<WorkspacePath>(),
                It.IsAny<IReadOnlyList<QualityGateConfiguration>>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<Action<PipelineRunEventReport>?>()))
            .Returns<WorkspacePath, IReadOnlyList<QualityGateConfiguration>, CancellationToken, Action<PipelineRunEventReport>?>(
                async (_, _, ct, _) =>
                {
                    try
                    {
                        await Task.Delay(Timeout.Infinite, ct);
                        return CreatePassingReport();
                    }
                    finally
                    {
                        ended = true;
                    }
                });

        var steps = new IPipelineStep[]
        {
            new VerifyBaselineStep(),
            new DelegateStep("AnalyzeCode", _ => throw new InvalidOperationException("analysis broke"))
        };

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => PipelineStepRunner.ExecuteAsync(steps, context, CancellationToken.None));

        Assert.True(ended, "Baseline process must end even when a step throws");
        Assert.Null(context.RunningBaseline);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static QualityGateConfiguration CreateQgc() => new()
    {
        Id = "qgc-1",
        DisplayName = "Test QGC",
        MatchLabels = ["dotnet"],
        CompilationCommand = "dotnet",
        CompilationArguments = ["build"],
        TestCommand = "dotnet",
        TestArguments = ["test"]
    };

    private static QualityGateReport CreatePassingReport() => new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = true },
        Tests = new GateResult { GateName = "Tests", Passed = true }
    };

    private static QualityGateReport CreateFailingReport() => new()
    {
        Compilation = new GateResult { GateName = "Compilation", Passed = false },
        Tests = new GateResult { GateName = "Tests", Passed = true }
    };

    private sealed class DelegateStep(string name, Func<PipelineStepContext, StepResult> run) : IPipelineStep
    {
        public string StepName => name;
        public Task<StepResult> ExecuteAsync(PipelineStepContext context, CancellationToken ct)
            => Task.FromResult(run(context));
    }
}
