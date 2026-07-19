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
    private readonly AgentIdentity _agentIdentity = new("test-agent");
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

    private PipelineExecutionContextBuilder CreateBuilder(
        IBrainUpdateService? brainUpdateService = null,
        IPipelineRunHistoryService? historyService = null)
    {
        return new PipelineExecutionContextBuilder(
            _mockQualityGateValidator.Object,
            _mockReporterFactory.Object,
            _feedbackService,
            _agentIdentity,
            _mockLogger.Object,
            brainUpdateService,
            historyService);
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
    public void Build_CreatesRunWithCorrectParameters()
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

        var result = builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        result.Run.RunId.Should().Be("job-123");
        result.Run.IssueIdentifier.Should().Be("test/repo#42");
        result.Run.RunType.Should().Be(PipelineRunType.Implementation);
        result.Run.RepositoryName.Should().Be("test/my-repo");
        result.Run.InitiatedBy.Should().Be("test-user");
    }

    [Fact]
    public void Build_CreatesReporterViaFactory()
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

        var result = builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        result.Reporter.Should().NotBeNull();
        _mockReporterFactory.Verify(f => f.Create(
            _connection, _batcher, "job-123",
            It.IsAny<PipelineRun>(), null), Times.Once);
    }

    [Fact]
    public void Build_WithBrainProvider_SetsBrainProviderConfigId()
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

        var result = builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, mockBrain.Object, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        result.Run.BrainProviderConfigId.Should().Be("brain-config-1");
    }

    [Fact]
    public void Build_WithoutBrainProvider_BrainProviderConfigIdIsNull()
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

        var result = builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        result.Run.BrainProviderConfigId.Should().BeNull();
    }

    [Fact]
    public void Build_WithBrainUpdateService_ContextHasBrainSync()
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

        var result = builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        result.ExecutionContext.BrainSync.Should().NotBeNull();
    }

    [Fact]
    public void Build_WithoutBrainUpdateService_ContextHasNullBrainSync()
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

        var result = builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        result.ExecutionContext.BrainSync.Should().BeNull();
    }

    [Fact]
    public void Build_PopulatesExecutionContextWithAllFields()
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

        var result = builder.Build(
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
    public void Build_LocalCtsIsLinkedToProvidedToken()
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
        var result = builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, cts.Token);

        // Cancelling the parent should cancel the linked token
        cts.Cancel();
        result.LocalCts.Token.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public void Build_SetsIssueLabelsOnRun()
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

        var result = builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        result.Run.IssueLabels.Should().BeEquivalentTo(new[] { "bug" });
    }

    [Fact]
    public void Build_EmitOutputLine_DelegatesToReporter()
    {
        // TODO: This test only asserts no-throw. Verify the reporter actually receives the output
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

        var result = builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        // EmitOutputLine should not throw — it delegates to the reporter's fire-and-forget path
        var act = () => result.EmitOutputLine("test output");
        act.Should().NotThrow();
    }

    [Fact]
    public void Build_SetsProjectIdAndProjectNameOnRun()
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

        var result = builder.Build(
            job, config, mockRepo.Object, mockAgent.Object, null, null,
            proxy, _connection, _batcher, null, CancellationToken.None);

        result.Run.ProjectId.Should().Be("proj-1");
        result.Run.ProjectName.Should().Be("My Project");
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

    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
