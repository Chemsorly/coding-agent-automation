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
/// Unit tests for <see cref="PipelineExecutionContextBuilder"/>.
/// Validates that the builder correctly constructs all orchestrators, reporter, and context objects.
/// </summary>
public class PipelineExecutionContextBuilderTests : IAsyncDisposable
{
    private readonly Mock<IQualityGateValidator> _mockQualityGateValidator = new();
    private readonly Mock<IPipelineReporterFactory> _mockReporterFactory = new();
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly FeedbackService _feedbackService;
    private readonly AgentId _agentId = new("test-agent");
    private readonly HubConnection _connection;
    private readonly OutputBatcher _batcher = new();

    public PipelineExecutionContextBuilderTests()
    {
        _feedbackService = new FeedbackService(_mockLogger.Object);
        _connection = CreateDisconnectedHubConnection();
    }

    public async ValueTask DisposeAsync()
    {
        await _batcher.DisposeAsync();
        await _connection.DisposeAsync();
    }

    // TODO(#1776): Add a test that invokes the PrContext.CreatePullRequest callback and verifies it delegates
    // to PullRequestFinalizationService.RunFullPrCreationAsync to cover the fixed null-dereference path.
    private PipelineExecutionContextBuilder CreateBuilder(
        IBrainUpdateService? brainUpdateService = null,
        IPipelineRunHistoryService? historyService = null)
    {
        return new PipelineExecutionContextBuilder(
            _mockQualityGateValidator.Object,
            _mockReporterFactory.Object,
            _feedbackService,
            _agentId,
            _mockLogger.Object,
            brainUpdateService,
            historyService,
            new PullRequestFinalizationService(_mockLogger.Object));
    }

    private static JobAssignmentMessage CreateTestJob(PipelineRunType runType = PipelineRunType.Implementation)
    {
        return new JobAssignmentMessage
        {
            JobId = "job-123",
            IssueIdentifier = "test/repo#42",
            RunType = runType,
            InitiatedBy = "test-user",
            RepoProviderConfigId = "repo-config-1",
            AgentProviderConfigId = "agent-config-1",
            PipelineProviderConfigId = "pipeline-config-1",
            BrainProviderConfigId = "brain-config-1",
            IssueDetail = new IssueDetail { Identifier = "test/repo#42", Title = "Test Issue", Description = "", Labels = new List<string> { "bug" } },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = new List<ProviderConfig>(),
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            ReviewPrTargetBranch = "main",
            ReviewPrDescription = null,
            ReviewPrAuthor = null,
            LinkedIssueContexts = null
        };
    }

    private PipelineSignalRReporter CreateReporter(PipelineRun run)
    {
        return new PipelineReporterFactory(_mockLogger.Object)
            .Create(_connection, _batcher, run.RunId, run, null);
    }

    private void SetupReporterFactory()
    {
        _mockReporterFactory
            .Setup(f => f.Create(
                It.IsAny<HubConnection>(),
                It.IsAny<OutputBatcher>(),
                It.IsAny<string>(),
                It.IsAny<PipelineRun>(),
                It.IsAny<Action<PipelineStep?>?>()))
            .Returns((HubConnection conn, OutputBatcher batcher, string jobId, PipelineRun run, Action<PipelineStep?>? onStep) =>
                new PipelineReporterFactory(_mockLogger.Object).Create(conn, batcher, jobId, run, onStep));
    }

    [Fact]
    public async Task Build_CreatesRunWithCorrectParameters()
    {
        SetupReporterFactory();

        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.RepositoryFullName).Returns("test/my-repo");
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(Array.Empty<string>());

        var builder = CreateBuilder();
        var job = CreateTestJob();
        var config = new PipelineConfiguration();
        var proxy = new OrchestratorProxy(_connection, "job-123");

        var result = await builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        result.Run.RunId.Should().Be("job-123");
        result.Run.IssueIdentifier.Value.Should().Be("test/repo#42");
        result.Run.RunType.Should().Be(PipelineRunType.Implementation);
        result.Run.RepositoryName.Should().Be("test/my-repo");
        result.Run.InitiatedBy.Should().Be("test-user");
    }

    [Fact]
    public async Task Build_CreatesReporterViaFactory()
    {
        SetupReporterFactory();

        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.RepositoryFullName).Returns("test/repo");
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(Array.Empty<string>());

        var builder = CreateBuilder();
        var job = CreateTestJob();
        var config = new PipelineConfiguration();
        var proxy = new OrchestratorProxy(_connection, "job-123");

        var result = await builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        result.Reporter.Should().NotBeNull();
        _mockReporterFactory.Verify(f => f.Create(
            _connection, _batcher, "job-123",
            It.IsAny<PipelineRun>(), null), Times.Once);
    }

    [Fact]
    public async Task Build_WithBrainProvider_SetsBrainProviderConfigId()
    {
        SetupReporterFactory();

        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.RepositoryFullName).Returns("test/repo");
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(Array.Empty<string>());
        var mockBrain = new Mock<IRepositoryProvider>();

        var builder = CreateBuilder();
        var job = CreateTestJob();
        var config = new PipelineConfiguration();
        var proxy = new OrchestratorProxy(_connection, "job-123");

        var result = await builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, mockBrain.Object, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        result.Run.BrainProviderConfigId.Should().Be("brain-config-1");
    }

    [Fact]
    public async Task Build_WithoutBrainProvider_BrainProviderConfigIdIsNull()
    {
        SetupReporterFactory();

        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.RepositoryFullName).Returns("test/repo");
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(Array.Empty<string>());

        var builder = CreateBuilder();
        var job = CreateTestJob();
        var config = new PipelineConfiguration();
        var proxy = new OrchestratorProxy(_connection, "job-123");

        var result = await builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        result.Run.BrainProviderConfigId.Should().BeNull();
    }

    [Fact]
    public async Task Build_WithBrainUpdateService_ContextHasBrainSync()
    {
        SetupReporterFactory();

        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.RepositoryFullName).Returns("test/repo");
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(Array.Empty<string>());
        var mockBrainService = new Mock<IBrainUpdateService>();

        var builder = CreateBuilder(brainUpdateService: mockBrainService.Object);
        var job = CreateTestJob();
        var config = new PipelineConfiguration();
        var proxy = new OrchestratorProxy(_connection, "job-123");

        var result = await builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        result.ExecutionContext.BrainSync.Should().NotBeNull();
    }

    [Fact]
    public async Task Build_WithoutBrainUpdateService_ContextHasNullBrainSync()
    {
        SetupReporterFactory();

        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.RepositoryFullName).Returns("test/repo");
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(Array.Empty<string>());

        var builder = CreateBuilder(brainUpdateService: null);
        var job = CreateTestJob();
        var config = new PipelineConfiguration();
        var proxy = new OrchestratorProxy(_connection, "job-123");

        var result = await builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        result.ExecutionContext.BrainSync.Should().BeNull();
    }

    [Fact]
    public async Task Build_PopulatesExecutionContextWithAllFields()
    {
        SetupReporterFactory();

        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.RepositoryFullName).Returns("test/repo");
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(Array.Empty<string>());
        var mockPipeline = new Mock<IPipelineProvider>();

        var builder = CreateBuilder();
        var job = CreateTestJob();
        var config = new PipelineConfiguration();
        var proxy = new OrchestratorProxy(_connection, "job-123");

        var result = await builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, mockPipeline.Object,
            proxy, _connection, _batcher, null, CancellationToken.None);

        var ctx = result.ExecutionContext;
        ctx.Job.Should().BeSameAs(job);
        ctx.Run.Should().BeSameAs(result.Run);
        ctx.Config.Should().BeSameAs(config);
        ctx.RepoProvider.Should().BeSameAs(mockRepo.Object);
        ctx.AgentProvider.Should().BeSameAs(mockAgent.Object);
        ctx.PipelineProvider.Should().BeSameAs(mockPipeline.Object);
        ctx.IssueOps.Should().BeSameAs(proxy);
        ctx.AgentExecution.Should().NotBeNull();
        ctx.QualityGates.Should().NotBeNull();
        ctx.PrOrchestrator.Should().NotBeNull();
        ctx.LocalCts.Should().NotBeNull();
        ctx.PrContext.Should().NotBeNull();
        ctx.TransitionTo.Should().NotBeNull();
        ctx.EmitOutputLine.Should().NotBeNull();
        ctx.ReportQualityGateResult.Should().NotBeNull();
    }

    [Fact]
    public async Task Build_LocalCtsIsLinkedToProvidedToken()
    {
        SetupReporterFactory();

        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.RepositoryFullName).Returns("test/repo");
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(Array.Empty<string>());

        var builder = CreateBuilder();
        var job = CreateTestJob();
        var config = new PipelineConfiguration();
        var proxy = new OrchestratorProxy(_connection, "job-123");

        using var cts = new CancellationTokenSource();
        var result = await builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, cts.Token);

        // Cancelling the parent should cancel the linked token
        cts.Cancel();
        result.LocalCts.Token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task Build_SetsIssueLabelsOnRun()
    {
        SetupReporterFactory();

        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.RepositoryFullName).Returns("test/repo");
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(Array.Empty<string>());

        var builder = CreateBuilder();
        var job = CreateTestJob();
        var config = new PipelineConfiguration();
        var proxy = new OrchestratorProxy(_connection, "job-123");

        var result = await builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        result.Run.IssueLabels.Should().BeEquivalentTo(new[] { "bug" });
    }

    [Fact]
    public async Task Build_EmitOutputLine_DelegatesToReporter()
    {
        // TODO(#1776): This test only asserts no-throw. Verify the reporter actually receives the output
        // line (e.g., via mock verification). Also add a test for the mutable StepContext
        // late-binding pattern: verify that setting StepContext post-Build causes EmitOutputLine
        // to use it for secret masking.
        SetupReporterFactory();

        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.RepositoryFullName).Returns("test/repo");
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(Array.Empty<string>());

        var builder = CreateBuilder();
        var job = CreateTestJob();
        var config = new PipelineConfiguration();
        var proxy = new OrchestratorProxy(_connection, "job-123");

        var result = await builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        // EmitOutputLine should not throw — it delegates to the reporter's fire-and-forget path
        var act = () => result.EmitOutputLine("test output");
        act.Should().NotThrow();
    }

    [Fact]
    public async Task Build_SetsProjectIdAndProjectNameOnRun()
    {
        SetupReporterFactory();

        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.RepositoryFullName).Returns("test/repo");
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(Array.Empty<string>());

        var builder = CreateBuilder();
        var job = CreateTestJobWithProject("proj-1", "My Project");
        var config = new PipelineConfiguration();
        var proxy = new OrchestratorProxy(_connection, "job-123");

        var result = await builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        result.Run.ProjectId.Should().Be("proj-1");
        result.Run.ProjectName.Should().Be("My Project");
    }

    [Fact]
    public async Task Build_SetsModelNameFromAgentProvider()
    {
        SetupReporterFactory();

        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.RepositoryFullName).Returns("test/repo");
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.Model).Returns("claude-sonnet-4-5");
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(Array.Empty<string>());

        var builder = CreateBuilder();
        var job = CreateTestJob();
        var config = new PipelineConfiguration();
        var proxy = new OrchestratorProxy(_connection, "job-123");

        var result = await builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        result.Run.ModelName.Should().Be("claude-sonnet-4-5");
        await result.DisposeAsync();
    }

    [Fact]
    public async Task Build_SetsModelNameToNull_WhenAgentProviderModelIsNull()
    {
        SetupReporterFactory();

        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.RepositoryFullName).Returns("test/repo");
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(Array.Empty<string>());

        var builder = CreateBuilder();
        var job = CreateTestJob();
        var config = new PipelineConfiguration();
        var proxy = new OrchestratorProxy(_connection, "job-123");

        var result = await builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        result.Run.ModelName.Should().BeNull();
        await result.DisposeAsync();
    }

    [Fact]
    public async Task Build_SuccessPath_DoesNotDisposeResources()
    {
        // Verify that on the success path, reporter and CTS remain undisposed
        // (disposal is handled by PipelineCleanup.RunAsync in the caller's finally block).
        SetupReporterFactory();

        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.RepositoryFullName).Returns("test/repo");
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(Array.Empty<string>());

        var builder = CreateBuilder();
        var job = CreateTestJob();
        var config = new PipelineConfiguration();
        var proxy = new OrchestratorProxy(_connection, "job-123");

        var result = await builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        // CTS should NOT be disposed after successful Build — caller owns cleanup
        var act = () => result.LocalCts.Token;
        act.Should().NotThrow();

        // Reporter should still be usable (not disposed) — verify by checking the internal
        // semaphore can be acquired. Mirrors the reflection pattern in Build_FailurePath_DisposesLocalCtsAndReporter.
        // TODO(#1776): [WARNING] The null-forgiving operator (!) on GetField() and GetValue() yields an opaque
        // NullReferenceException if '_signalrLock' is ever renamed or removed. Replace with:
        //   Assert.NotNull(lockField, "Expected private field '_signalrLock' on PipelineSignalRReporter — update this test if the field was renamed.");
        // to produce a diagnosable failure message.
        // TODO(#1776): [WARNING] The semaphore WaitAsync(0) returns false if SerializedSendAsync holds the lock
        // at the instant this runs (e.g., a fire-and-forget in flight), causing a spurious test failure.
        // The window is narrow with a disconnected connection and no pending work, but the assumption is
        // undocumented. If this test becomes flaky, add a comment confirming no async work is in flight
        // or switch to asserting ObjectDisposedException is NOT thrown on semaphore.WaitAsync.
        var lockField = typeof(PipelineSignalRReporter)
            .GetField("_signalrLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var semaphore = (SemaphoreSlim)lockField.GetValue(result.Reporter)!;
        var acquired = await semaphore.WaitAsync(0);
        if (acquired) semaphore.Release();
        acquired.Should().BeTrue("reporter semaphore should be acquirable when reporter is not disposed");
        // TODO(#1776): [WARNING] result.DisposeAsync() is intentionally skipped to verify the undisposed state,
        // but this leaks the PipelineSignalRReporter and its internal SemaphoreSlim (and the CancellationTokenSource)
        // for the lifetime of the test process. The assertions are already captured above, so calling
        // 'await result.DisposeAsync()' here would not invalidate them. Consider adding cleanup after
        // the assertions to avoid the resource leak on every test run.
        // Note: result.DisposeAsync() is intentionally NOT called here — this test verifies
        // that Build() leaves resources live on the success path.
    }

    private static JobAssignmentMessage CreateTestJobWithProject(string projectId, string projectName)
    {
        return new JobAssignmentMessage
        {
            JobId = "job-123",
            IssueIdentifier = "test/repo#42",
            RunType = PipelineRunType.Implementation,
            InitiatedBy = "test-user",
            RepoProviderConfigId = "repo-config-1",
            AgentProviderConfigId = "agent-config-1",
            PipelineProviderConfigId = "pipeline-config-1",
            BrainProviderConfigId = "brain-config-1",
            IssueDetail = new IssueDetail { Identifier = "test/repo#42", Title = "Test Issue", Description = "", Labels = new List<string> { "bug" } },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = new List<ProviderConfig>(),
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            ReviewPrTargetBranch = "main",
            ReviewPrDescription = null,
            ReviewPrAuthor = null,
            LinkedIssueContexts = null,
            ProjectId = projectId,
            ProjectName = projectName
        };
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

    private static JobAssignmentMessage CreateTestJobWithStaleness(bool forceRefresh, string? signal, int refreshCount)
    {
        return new JobAssignmentMessage
        {
            JobId = "job-123",
            IssueIdentifier = "test/repo#42",
            RunType = PipelineRunType.Implementation,
            InitiatedBy = "test-user",
            RepoProviderConfigId = "repo-config-1",
            AgentProviderConfigId = "agent-config-1",
            PipelineProviderConfigId = "pipeline-config-1",
            BrainProviderConfigId = "brain-config-1",
            IssueDetail = new IssueDetail { Identifier = "test/repo#42", Title = "Test Issue", Description = "", Labels = new List<string> { "bug" } },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = new List<ProviderConfig>(),
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            ReviewPrTargetBranch = "main",
            ReviewPrDescription = null,
            ReviewPrAuthor = null,
            LinkedIssueContexts = null,
            ForceRefreshAnalysis = forceRefresh,
            StalenessSignal = signal,
            AnalysisRefreshCount = refreshCount
        };
    }

    [Fact]
    public async Task Build_FailurePath_DisposesLocalCtsAndReporter()
    {
        // Exercises the catch block in Build() — verifies that both localCts and reporter
        // are disposed when an exception occurs after CTS creation but before return.
        SetupReporterFactory();

        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.RepositoryFullName).Returns("test/repo");
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(Array.Empty<string>());

        var builder = CreateBuilder();
        // Inject a failure inside the try block via the internal test seam.
        builder._testThrowAfterCtsCreation = () => throw new InvalidOperationException("Simulated construction failure");

        var job = CreateTestJob();
        var config = new PipelineConfiguration();
        var proxy = new OrchestratorProxy(_connection, "job-123");

        // Capture the reporter created by the factory so we can verify its disposal.
        PipelineSignalRReporter? capturedReporter = null;
        _mockReporterFactory
            .Setup(f => f.Create(
                It.IsAny<HubConnection>(),
                It.IsAny<OutputBatcher>(),
                It.IsAny<string>(),
                It.IsAny<PipelineRun>(),
                It.IsAny<Action<PipelineStep?>?>()))
            .Returns((HubConnection conn, OutputBatcher batcher, string jobId, PipelineRun run, Action<PipelineStep?>? onStep) =>
            {
                capturedReporter = new PipelineReporterFactory(_mockLogger.Object).Create(conn, batcher, jobId, run, onStep);
                return capturedReporter;
            });

        // Act — Build() should throw the injected exception
        var act = () => builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Simulated construction failure");

        // Assert — reporter should be disposed (internal semaphore is disposed)
        capturedReporter.Should().NotBeNull();
        var lockField = typeof(PipelineSignalRReporter)
            .GetField("_signalrLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var semaphore = (SemaphoreSlim)lockField.GetValue(capturedReporter!)!;
        var semaphoreAct = () => semaphore.WaitAsync(CancellationToken.None);
        await semaphoreAct.Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Build_FailurePath_PropagatesOriginalException()
    {
        // Verifies that the original exception is re-thrown (not swallowed or wrapped).
        SetupReporterFactory();

        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.RepositoryFullName).Returns("test/repo");
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(Array.Empty<string>());

        var expectedException = new ArgumentException("Specific failure message");
        var builder = CreateBuilder();
        builder._testThrowAfterCtsCreation = () => throw expectedException;

        var job = CreateTestJob();
        var config = new PipelineConfiguration();
        var proxy = new OrchestratorProxy(_connection, "job-123");

        // Act
        var act = () => builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        var thrown = await act.Should().ThrowAsync<ArgumentException>();
        thrown.Which.Should().BeSameAs(expectedException);
    }

    // ── CreateStepContext ─────────────────────────────────────────────────

    // TODO(#1776): CreateBuilder() does not pass a PullRequestFinalizationService, leaving _finalization null.
    // Add a test that constructs the builder with a mock finalization service and exercises the
    // CreatePullRequest callback path to cover the moved CreatePullRequestAsync logic.
    [Fact]
    public async Task CreateStepContext_SetsRunAndConfigCorrectly()
    {
        SetupReporterFactory();
        var builder = CreateBuilder();

        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.RepositoryFullName).Returns("test/repo");
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(Array.Empty<string>());

        var job = CreateTestJob();
        var config = new PipelineConfiguration { MaxRetries = 3 };
        var proxy = new OrchestratorProxy(_connection, "job-123");

        var buildResult = await builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        var reporter = buildResult.Reporter;

        // Act
        var ctx = builder.CreateStepContext(buildResult.ExecutionContext, reporter, CancellationToken.None);

        // Assert
        ctx.Run.Should().BeSameAs(buildResult.Run);
        ctx.Config.Should().BeSameAs(config);
        ctx.RepoProvider.Should().BeSameAs(mockRepo.Object);
        ctx.AgentProvider.Should().BeSameAs(mockAgent.Object);

        await buildResult.DisposeAsync();
    }

    [Fact]
    public async Task CreateStepContext_PropagatesStalenessFields()
    {
        SetupReporterFactory();
        var builder = CreateBuilder();

        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.RepositoryFullName).Returns("test/repo");
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(Array.Empty<string>());

        var job = CreateTestJobWithStaleness(forceRefresh: true, signal: "file-changed", refreshCount: 2);

        var config = new PipelineConfiguration();
        var proxy = new OrchestratorProxy(_connection, "job-123");

        var buildResult = await builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        var reporter = buildResult.Reporter;

        // Act
        var ctx = builder.CreateStepContext(buildResult.ExecutionContext, reporter, CancellationToken.None);

        // Assert
        ctx.ForceRefreshAnalysis.Should().BeTrue();
        ctx.StalenessSignal.Should().Be("file-changed");
        ctx.AnalysisRefreshCount.Should().Be(2);

        await buildResult.DisposeAsync();
    }

    [Fact]
    public async Task CreateStepContext_CallbacksFireTransitionTo()
    {
        SetupReporterFactory();
        var builder = CreateBuilder();

        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.RepositoryFullName).Returns("test/repo");
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(Array.Empty<string>());

        var job = CreateTestJob();
        var config = new PipelineConfiguration();
        var proxy = new OrchestratorProxy(_connection, "job-123");

        // TCS must be declared before Build() so the closure can capture it.
        // RunContinuationsAsynchronously ensures TrySetResult does not run test
        // continuations synchronously on the callback's thread-pool thread.
        var tcs = new TaskCompletionSource<PipelineStep>(TaskCreationOptions.RunContinuationsAsynchronously);
        PipelineStep? transitionedStep = null;
        var buildResult = await builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher,
            step =>
            {
                transitionedStep = step;
                if (step.HasValue) tcs.TrySetResult(step.Value);
            },
            CancellationToken.None);

        var reporter = buildResult.Reporter;

        // Act
        var ctx = builder.CreateStepContext(buildResult.ExecutionContext, reporter, CancellationToken.None);
        ctx.Callbacks.TransitionTo(PipelineStep.AnalyzingCode);

        // Assert — await TCS with timeout instead of Task.Delay:
        // TransitionTo is fire-and-forget; _onStepChanged fires on the thread-pool thread
        // inside SerializedSendAsync. The TCS provides deterministic synchronization.
        var receivedStep = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        receivedStep.Should().Be(PipelineStep.AnalyzingCode);
        transitionedStep.Should().Be(PipelineStep.AnalyzingCode);

        await buildResult.DisposeAsync();
    }

    // ── Review run type ───────────────────────────────────────────────────

    [Fact]
    public async Task Build_ReviewRunType_CreatesReviewRun()
    {
        SetupReporterFactory();

        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.RepositoryFullName).Returns("test/repo");
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(Array.Empty<string>());

        var builder = CreateBuilder();
        var job = CreateTestJob(PipelineRunType.Review);
        var config = new PipelineConfiguration();
        var proxy = new OrchestratorProxy(_connection, "job-123");

        var result = await builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        result.Run.RunType.Should().Be(PipelineRunType.Review);
        result.Run.RunId.Should().Be("job-123");

        await result.DisposeAsync();
    }

    // ── Decomposition run type ────────────────────────────────────────────

    [Fact]
    public async Task Build_DecompositionRunType_CreatesDecompositionRun()
    {
        SetupReporterFactory();

        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.RepositoryFullName).Returns("test/repo");
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(Array.Empty<string>());

        var builder = CreateBuilder();
        var job = CreateTestJob(PipelineRunType.Decomposition);
        var config = new PipelineConfiguration();
        var proxy = new OrchestratorProxy(_connection, "job-123");

        var result = await builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        result.Run.RunType.Should().Be(PipelineRunType.Decomposition);
        result.Run.RunId.Should().Be("job-123");

        await result.DisposeAsync();
    }

    [Fact]
    public async Task Build_DecompositionAnalysisRunType_CreatesDecompositionRun()
    {
        SetupReporterFactory();

        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.RepositoryFullName).Returns("test/repo");
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(Array.Empty<string>());

        var builder = CreateBuilder();
        var job = CreateTestJob(PipelineRunType.DecompositionAnalysis);
        var config = new PipelineConfiguration();
        var proxy = new OrchestratorProxy(_connection, "job-123");

        var result = await builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        result.Run.RunType.Should().Be(PipelineRunType.DecompositionAnalysis);

        await result.DisposeAsync();
    }

    // ── CreatePullRequest callback / null finalization ────────────────────

    [Fact]
    public async Task Build_CreatePullRequestCallback_WhenFinalizationNull_ThrowsInvalidOperationException()
    {
        // Build without a PullRequestFinalizationService — _finalization is null.
        // Invoking the CreatePullRequest callback through the step context's Callbacks
        // must throw InvalidOperationException.
        var builderWithoutFinalization = new PipelineExecutionContextBuilder(
            _mockQualityGateValidator.Object,
            _mockReporterFactory.Object,
            _feedbackService,
            _agentId,
            _mockLogger.Object,
            brainUpdateService: null,
            historyService: null,
            finalization: null);

        SetupReporterFactory();
        var mockRepo = new Mock<IRepositoryProvider>();
        mockRepo.Setup(r => r.RepositoryFullName).Returns("test/repo");
        var mockAgent = new Mock<IAgentProvider>();
        mockAgent.Setup(a => a.PipelineInjectedPaths).Returns(Array.Empty<string>());

        var job = CreateTestJob();
        var config = new PipelineConfiguration();
        var proxy = new OrchestratorProxy(_connection, "job-123");

        var result = await builderWithoutFinalization.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        // CreateStepContext wires up AgentCallbacks which delegates CreatePullRequest
        // to the private CreatePullRequestAsync (which throws when _finalization is null).
        var stepCtx = builderWithoutFinalization.CreateStepContext(
            result.ExecutionContext, (PipelineSignalRReporter)result.Reporter, CancellationToken.None);

        var report = new QualityGateReport
        {
            Compilation = new GateResult { GateName = "Compilation", Passed = true },
            Tests = new GateResult { GateName = "Tests", Passed = true }
        };
        var act = () => stepCtx.Callbacks.CreatePullRequest(
            result.Run, report, isDraft: false, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*PullRequestFinalizationService*");

        await result.DisposeAsync();
    }

    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
