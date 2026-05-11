using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services.Steps;

public class VerifyBaselineStepTests
{
    private readonly Mock<IQualityGateValidator> _validator = new();
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Mock<IConfigurationStore> _configStore = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly List<string> _outputLines = [];
    private readonly List<PipelineStep> _transitions = [];

    public VerifyBaselineStepTests()
    {
        _callbacks.Setup(c => c.EmitOutputLine(It.IsAny<string>()))
            .Callback<string>(line => _outputLines.Add(line));
        _callbacks.Setup(c => c.TransitionTo(It.IsAny<PipelineStep>()))
            .Callback<PipelineStep>(step => _transitions.Add(step));
        _callbacks.Setup(c => c.SwapAgentLabel(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    private PipelineStepContext BuildContext(
        bool baselineEnabled = true,
        string? kiroCliPath = null,
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

        return new PipelineStepContext
        {
            Run = run,
            Config = config,
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
            QualityGateValidator = _validator.Object,
            KiroCliPath = kiroCliPath,
            PreResolvedQualityGateConfigs = preResolvedQgcs
        };
    }

    [Fact]
    public async Task ExecuteAsync_WhenDisabled_SkipsAndReturnsContinue()
    {
        var context = BuildContext(baselineEnabled: false);
        var step = new VerifyBaselineStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        _transitions.Should().BeEmpty();
        _outputLines.Should().Contain(l => l.Contains("disabled"));
    }

    [Fact]
    public async Task ExecuteAsync_NoDoctorNoQgcs_ReturnsContinue()
    {
        var context = BuildContext(preResolvedQgcs: []);
        var step = new VerifyBaselineStep();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        _transitions.Should().Contain(PipelineStep.VerifyingBaseline);
        Assert.Null(context.Run.BaselineHealthPassed);
    }

    [Fact]
    public async Task ExecuteAsync_BaselineSucceeds_SetsBaselineHealthPassedTrue()
    {
        var qgc = CreateQgc();
        var context = BuildContext(preResolvedQgcs: [qgc]);

        _validator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreatePassingReport());

        var step = new VerifyBaselineStep();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        Assert.True(context.Run.BaselineHealthPassed);
        _outputLines.Should().Contain(l => l.Contains("✅ Workspace baseline healthy"));
    }

    [Fact]
    public async Task ExecuteAsync_BaselineFails_ReturnsContinueWithBaselineHealthFalse()
    {
        var qgc = CreateQgc();
        var context = BuildContext(preResolvedQgcs: [qgc]);

        _validator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateFailingReport());

        var step = new VerifyBaselineStep();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        Assert.False(context.Run.BaselineHealthPassed);
        _outputLines.Should().Contain(l => l.Contains("⚠️") && l.Contains("non-fatal"));
    }

    [Fact]
    public async Task ExecuteAsync_BaselineThrows_ReturnsContinueWithBaselineHealthFalse()
    {
        var qgc = CreateQgc();
        var context = BuildContext(preResolvedQgcs: [qgc]);

        _validator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("build tool not found"));

        var step = new VerifyBaselineStep();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        Assert.False(context.Run.BaselineHealthPassed);
    }

    [Fact]
    public async Task ExecuteAsync_UsesPreResolvedQgcs_WhenAvailable()
    {
        var qgc = CreateQgc();
        var context = BuildContext(preResolvedQgcs: [qgc]);

        _validator.Setup(v => v.ValidateAsync("/tmp/workspace", It.Is<IReadOnlyList<QualityGateConfiguration>>(l => l.Count == 1 && l[0] == qgc), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreatePassingReport());

        var step = new VerifyBaselineStep();
        await step.ExecuteAsync(context, CancellationToken.None);

        _validator.Verify(v => v.ValidateAsync("/tmp/workspace", It.Is<IReadOnlyList<QualityGateConfiguration>>(l => l[0] == qgc), It.IsAny<CancellationToken>()), Times.Once);
        _configStore.Verify(c => c.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task ExecuteAsync_LoadsQgcsFromStore_WhenNotPreResolved()
    {
        var qgc = CreateQgc();
        var context = BuildContext(preResolvedQgcs: null);

        _configStore.Setup(c => c.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<QualityGateConfiguration> { qgc });
        _validator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreatePassingReport());

        var step = new VerifyBaselineStep();
        await step.ExecuteAsync(context, CancellationToken.None);

        _configStore.Verify(c => c.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_NeverInvokesExternalCi()
    {
        var qgc = CreateQgc();
        var context = BuildContext(preResolvedQgcs: [qgc]);

        _validator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreatePassingReport());

        var step = new VerifyBaselineStep();
        await step.ExecuteAsync(context, CancellationToken.None);

        // ValidateAsync is called (local gates only) — external CI is never invoked
        // because VerifyBaselineStep calls IQualityGateValidator directly, not IQualityGateExecutor
        _validator.Verify(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_TransitionsToVerifyingBaseline()
    {
        var context = BuildContext(preResolvedQgcs: [CreateQgc()]);

        _validator.Setup(v => v.ValidateAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<QualityGateConfiguration>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreatePassingReport());

        var step = new VerifyBaselineStep();
        await step.ExecuteAsync(context, CancellationToken.None);

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
            QualityGateValidator = null,
            KiroCliPath = null
        };

        var step = new VerifyBaselineStep();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Continue, result);
        Assert.Null(context.Run.BaselineHealthPassed);
    }

    [Fact]
    public async Task ExecuteAsync_DoctorFails_ReturnsStopWithClearError()
    {
        // Use a non-existent path to simulate doctor failure
        var context = BuildContext(kiroCliPath: "/nonexistent/kiro-cli", preResolvedQgcs: [CreateQgc()]);

        var step = new VerifyBaselineStep();
        var result = await step.ExecuteAsync(context, CancellationToken.None);

        Assert.Equal(StepResult.Stop, result);
        context.Run.FailureReason.Should().Contain("Agent environment unhealthy");
    }

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
}
