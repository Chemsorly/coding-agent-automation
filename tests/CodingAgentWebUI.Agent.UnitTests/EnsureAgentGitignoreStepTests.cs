using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="EnsureAgentGitignoreStep"/>.
/// Verifies that .agent/ is added to .gitignore at the start of any pipeline run.
/// </summary>
public class EnsureAgentGitignoreStepTests : IDisposable
{
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();
    private readonly string _workspacePath;

    public EnsureAgentGitignoreStepTests()
    {
        _workspacePath = Path.Combine(Path.GetTempPath(), $"gitignore-step-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_workspacePath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspacePath))
            Directory.Delete(_workspacePath, recursive: true);
    }

    [Fact]
    public async Task ExecuteAsync_NoGitignoreExists_CreatesWithAgentEntry()
    {
        var step = new EnsureAgentGitignoreStep();
        var context = BuildContext();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        var gitignorePath = Path.Combine(_workspacePath, ".gitignore");
        File.Exists(gitignorePath).Should().BeTrue();
        var content = await File.ReadAllTextAsync(gitignorePath);
        content.Should().Contain(".agent/");
    }

    [Fact]
    public async Task ExecuteAsync_GitignoreExistsWithoutAgent_AddsAgentEntry()
    {
        var gitignorePath = Path.Combine(_workspacePath, ".gitignore");
        await File.WriteAllTextAsync(gitignorePath, "node_modules/\nbin/\n");

        var step = new EnsureAgentGitignoreStep();
        var context = BuildContext();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        var content = await File.ReadAllTextAsync(gitignorePath);
        content.Should().Contain(".agent/");
        content.Should().Contain("node_modules/");
    }

    [Fact]
    public async Task ExecuteAsync_GitignoreAlreadyContainsAgent_DoesNotDuplicate()
    {
        var gitignorePath = Path.Combine(_workspacePath, ".gitignore");
        var original = "node_modules/\n.agent/\nbin/\n";
        await File.WriteAllTextAsync(gitignorePath, original);

        var step = new EnsureAgentGitignoreStep();
        var context = BuildContext();

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
        var content = await File.ReadAllTextAsync(gitignorePath);
        content.Should().Be(original);
    }

    [Fact]
    public async Task ExecuteAsync_WorkspacePathNull_ReturnsContinue()
    {
        var step = new EnsureAgentGitignoreStep();
        var run = new PipelineRun
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            WorkspacePath = null
        };
        var context = BuildContext(run);

        var result = await step.ExecuteAsync(context, CancellationToken.None);

        result.Should().Be(StepResult.Continue);
    }

    private PipelineStepContext BuildContext(PipelineRun? run = null)
    {
        run ??= new PipelineRun
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = "1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            WorkspacePath = _workspacePath
        };

        return new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" },
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
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger
        };
    }
}
