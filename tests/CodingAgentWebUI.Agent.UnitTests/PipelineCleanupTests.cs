using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using Microsoft.AspNetCore.SignalR.Client;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="PipelineCleanup"/>.
/// Validates cleanup logic: CTS disposal, secret clearing, workspace deletion, reporter disposal.
/// </summary>
public class PipelineCleanupTests : IAsyncDisposable
{
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly HubConnection _connection;
    private readonly OutputBatcher _batcher = new();

    public PipelineCleanupTests()
    {
        _connection = CreateDisconnectedHubConnection();
    }

    public async ValueTask DisposeAsync()
    {
        await _batcher.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private PipelineSignalRReporter CreateReporter(PipelineRun run)
    {
        return new PipelineReporterFactory(_mockLogger.Object)
            .Create(_connection, _batcher, run.RunId, run, null);
    }

    private static PipelineRun CreateRun(PipelineStep currentStep = PipelineStep.Created)
    {
        var run = PipelineRun.Create(
            runId: "test-run",
            issueIdentifier: "test/repo#1",
            issueTitle: "Test",
            issueProviderConfigId: "",
            repoProviderConfigId: "repo-1",
            runType: PipelineRunType.Implementation,
            initiatedBy: "test",
            agentId: "test-agent");
        run.CurrentStep = currentStep;
        return run;
    }

    [Fact]
    public async Task RunAsync_DisposesLocalCts()
    {
        var run = CreateRun();
        var reporter = CreateReporter(run);
        using var cts = new CancellationTokenSource();

        await PipelineCleanup.RunAsync(cts, null, run, reporter, _mockLogger.Object);

        // After disposal, accessing the Token should throw ObjectDisposedException
        var act = () => cts.Token;
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task RunAsync_NullCts_DoesNotThrow()
    {
        var run = CreateRun();
        var reporter = CreateReporter(run);

        var act = () => PipelineCleanup.RunAsync(null, null, run, reporter, _mockLogger.Object);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunAsync_ClearsInjectedSecretKeys()
    {
        var run = CreateRun();
        var reporter = CreateReporter(run);
        var uniqueKey = $"TEST_SECRET_{Guid.NewGuid():N}";

        Environment.SetEnvironmentVariable(uniqueKey, "sensitive-value");

        var stepContext = new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration(),
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = Mock.Of<IAgentProvider>(),
            BrainProvider = null,
            PipelineProvider = null,
            Cts = null,
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = Mock.Of<IPipelineCallbacks>(),
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = Mock.Of<IAgentPhaseExecutor>(),
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_mockLogger.Object),
            Logger = _mockLogger.Object
        };
        stepContext.InjectedSecretKeys = new List<string> { uniqueKey };

        try
        {
            await PipelineCleanup.RunAsync(null, stepContext, run, reporter, _mockLogger.Object);

            Environment.GetEnvironmentVariable(uniqueKey).Should().BeNull();
        }
        finally
        {
            // Ensure cleanup even if test fails
            Environment.SetEnvironmentVariable(uniqueKey, null);
        }
    }

    [Fact]
    public async Task RunAsync_NullStepContext_DoesNotThrow()
    {
        var run = CreateRun();
        var reporter = CreateReporter(run);

        var act = () => PipelineCleanup.RunAsync(null, null, run, reporter, _mockLogger.Object);

        await act.Should().NotThrowAsync();
    }

    [Theory]
    [InlineData(PipelineStep.Completed)]
    [InlineData(PipelineStep.Failed)]
    [InlineData(PipelineStep.Cancelled)]
    public async Task RunAsync_TerminalStep_DeletesWorkspaceDirectory(PipelineStep step)
    {
        var run = CreateRun(step);
        var tempDir = Path.Combine(Path.GetTempPath(), $"cleanup-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        run.WorkspacePath = tempDir;

        var reporter = CreateReporter(run);

        await PipelineCleanup.RunAsync(null, null, run, reporter, _mockLogger.Object);

        Directory.Exists(tempDir).Should().BeFalse();
    }

    [Theory]
    [InlineData(PipelineStep.Created)]
    [InlineData(PipelineStep.AnalyzingCode)]
    [InlineData(PipelineStep.GeneratingCode)]
    public async Task RunAsync_NonTerminalStep_DoesNotDeleteWorkspace(PipelineStep step)
    {
        var run = CreateRun(step);
        var tempDir = Path.Combine(Path.GetTempPath(), $"cleanup-test-{Guid.NewGuid()}");
        Directory.CreateDirectory(tempDir);
        run.WorkspacePath = tempDir;

        var reporter = CreateReporter(run);

        try
        {
            await PipelineCleanup.RunAsync(null, null, run, reporter, _mockLogger.Object);

            Directory.Exists(tempDir).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task RunAsync_WorkspaceDeletionFails_LogsWarningAndContinues()
    {
        // TODO: This test does not exercise the deletion failure path. The workspace path
        // doesn't exist so Directory.Exists() returns false and deletion is never attempted.
        // Use a real temp directory with locked files or mock the filesystem to test the catch block.
        var run = CreateRun(PipelineStep.Completed);
        run.WorkspacePath = "/nonexistent/path/that/does/not/exist";

        var reporter = CreateReporter(run);

        // Should not throw — workspace path doesn't exist, so the condition short-circuits
        var act = () => PipelineCleanup.RunAsync(null, null, run, reporter, _mockLogger.Object);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunAsync_DisposesReporter()
    {
        // TODO: This test has zero assertions. Verify reporter.DisposeAsync() was actually called
        // (e.g., via a mock or by checking that subsequent operations on the reporter fail).
        var run = CreateRun();
        var reporter = CreateReporter(run);

        await PipelineCleanup.RunAsync(null, null, run, reporter, _mockLogger.Object);

        // After DisposeAsync, calling TransitionTo should still not throw
        // (fire-and-forget pattern swallows errors), but the semaphore is disposed.
        // We verify disposal happened by checking no exception during cleanup.
        // This is a smoke test — the real verification is that DisposeAsync was awaited.
    }

    [Fact]
    public async Task RunAsync_EmptyWorkspacePath_DoesNotAttemptDeletion()
    {
        var run = CreateRun(PipelineStep.Completed);
        run.WorkspacePath = "";

        var reporter = CreateReporter(run);

        var act = () => PipelineCleanup.RunAsync(null, null, run, reporter, _mockLogger.Object);

        await act.Should().NotThrowAsync();
    }

    private static HubConnection CreateDisconnectedHubConnection()
    {
        return new HubConnectionBuilder()
            .WithUrl($"http://localhost{HubRoutes.Agent}", options =>
            {
                options.HttpMessageHandlerFactory = _ => new NoOpHandler();
            })
            .Build();
    }

    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
