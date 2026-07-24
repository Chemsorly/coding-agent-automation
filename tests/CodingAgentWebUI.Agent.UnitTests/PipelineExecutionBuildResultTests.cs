using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Unit tests for <see cref="PipelineExecutionBuildResult.DisposeAsync"/>.
/// Validates that DisposeAsync correctly disposes both LocalCts and Reporter,
/// and that subsequent calls are idempotent (no-op).
/// </summary>
public class PipelineExecutionBuildResultTests : IAsyncDisposable
{
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly HubConnection _connection;
    private readonly OutputBatcher _batcher = new();

    public PipelineExecutionBuildResultTests()
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

    private static PipelineRun CreateRun()
    {
        return PipelineRun.Create(
            runId: "test-run",
            issueIdentifier: "test/repo#1",
            issueTitle: "Test",
            issueProviderConfigId: "",
            repoProviderConfigId: "repo-1",
            runType: PipelineRunType.Implementation,
            initiatedBy: "test",
            agentId: "test-agent");
    }

    private PipelineExecutionBuildResult CreateBuildResult(PipelineRun run, CancellationTokenSource cts, PipelineSignalRReporter reporter)
    {
        var job = new JobAssignmentMessage
        {
            JobId = "test-run",
            IssueIdentifier = "test/repo#1",
            RunType = PipelineRunType.Implementation,
            InitiatedBy = "test",
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            IssueDetail = new IssueDetail { Identifier = "test/repo#1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            ReviewPrTargetBranch = "main"
        };

        return new PipelineExecutionBuildResult
        {
            Run = run,
            ExecutionContext = new PipelineExecutionContext
            {
                Job = job,
                Run = run,
                Config = new PipelineConfiguration(),
                RepoProvider = Mock.Of<IRepositoryProvider>(),
                AgentProvider = Mock.Of<IAgentProvider>(),
                IssueOps = new OrchestratorProxy(_connection, "test-run"),
                PrOrchestrator = new PullRequestOrchestrator(_mockLogger.Object),
                AgentExecution = new AgentPhaseExecutor(_mockLogger.Object),
                QualityGates = new QualityGateExecutor(
                    Mock.Of<IQualityGateValidator>(),
                    new PullRequestOrchestrator(_mockLogger.Object),
                    new CiLogWriter(_mockLogger.Object),
                    new FeedbackService(_mockLogger.Object),
                    _mockLogger.Object,
                    null),
                LocalCts = cts,
                PrContext = new PullRequestCreationContext
                {
                    RepoProvider = Mock.Of<IRepositoryProvider>(),
                    AgentProvider = Mock.Of<IAgentProvider>(),
                    Config = new PipelineConfiguration(),
                    IssueOps = new OrchestratorProxy(_connection, "test-run"),
                    Job = job,
                    PrOrchestrator = new PullRequestOrchestrator(_mockLogger.Object),
                    EmitOutputLine = _ => { },
                    ReportStepTransition = (_, _) => Task.CompletedTask
                },
                TransitionTo = _ => { },
                EmitOutputLine = _ => { },
                ReportQualityGateResult = _ => { }
            },
            Reporter = reporter,
            LocalCts = cts,
            EmitOutputLine = _ => { }
        };
    }

    [Fact]
    public async Task DisposeAsync_DisposesLocalCts()
    {
        var run = CreateRun();
        var reporter = CreateReporter(run);
        var cts = new CancellationTokenSource();

        var buildResult = CreateBuildResult(run, cts, reporter);

        await buildResult.DisposeAsync();

        // After disposal, accessing the Token should throw ObjectDisposedException
        var act = () => cts.Token;
        act.Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task DisposeAsync_DisposesReporter()
    {
        var run = CreateRun();
        var reporter = CreateReporter(run);
        var cts = new CancellationTokenSource();

        var buildResult = CreateBuildResult(run, cts, reporter);

        await buildResult.DisposeAsync();

        // TODO: [WARNING] This test verifies implementation details via reflection — accessing
        // the private _signalrLock field by name couples the test to PipelineSignalRReporter's
        // internal structure. If the field is renamed or the disposal mechanism changes, this
        // test breaks without any behavior change. Consider verifying disposal through observable
        // behavior instead, though this may not be feasible given the current API surface.
        // After reporter disposal, the internal SemaphoreSlim should be disposed.
        // Access it via reflection and verify that WaitAsync throws ObjectDisposedException,
        // proving that buildResult.DisposeAsync() actually called reporter.DisposeAsync().
        var lockField = typeof(PipelineSignalRReporter)
            .GetField("_signalrLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var semaphore = (SemaphoreSlim)lockField.GetValue(reporter)!;
        var act = () => semaphore.WaitAsync(CancellationToken.None);
        await act.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task DisposeAsync_CalledTwice_DoesNotThrow()
    {
        var run = CreateRun();
        var reporter = CreateReporter(run);
        var cts = new CancellationTokenSource();

        var buildResult = CreateBuildResult(run, cts, reporter);

        // First call disposes resources
        await buildResult.DisposeAsync();

        // Second call should be a no-op (idempotent)
        var act = async () => await buildResult.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Reporter_DisposeAsync_CalledTwice_DoesNotThrow()
    {
        var run = CreateRun();
        var reporter = CreateReporter(run);

        // First call disposes the semaphore
        await reporter.DisposeAsync();

        // Second call must not throw ObjectDisposedException
        var act = async () => await reporter.DisposeAsync();
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Reporter_DisposeAsync_ConcurrentCalls_DoNotThrow()
    {
        var run = CreateRun();
        var reporter = CreateReporter(run);

        // Two concurrent disposal calls must both complete without throwing
        var act = async () => await Task.WhenAll(
            reporter.DisposeAsync().AsTask(),
            reporter.DisposeAsync().AsTask());
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
