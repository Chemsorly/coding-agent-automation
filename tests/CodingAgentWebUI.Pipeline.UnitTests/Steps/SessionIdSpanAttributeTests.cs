using System.Diagnostics;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Pipeline.Services.Steps;
using CodingAgentWebUI.Pipeline.Telemetry;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Steps;

/// <summary>
/// Tests verifying that pipeline phase steps emit pipeline.session_id as a span attribute.
/// This enables Grafana Tempo correlation to prove the primary session is reused across phases.
/// </summary>
public class SessionIdSpanAttributeTests : IDisposable
{
    private readonly ActivityListener _listener;
    private readonly List<Activity> _activities = [];
    private readonly Mock<IPipelineCallbacks> _callbacks = new();
    private readonly Mock<IAgentProvider> _agentProvider = new();
    private readonly Mock<IAgentPhaseExecutor> _agentExecution = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();

    public SessionIdSpanAttributeTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name == PipelineTelemetry.SourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _activities.Add(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public void Dispose() => _listener.Dispose();

    [Fact]
    public async Task GenerateCodeStep_SetsSessionIdTag_WhenCodegenSessionIdCaptured()
    {
        // Arrange
        const string expectedSessionId = "session-abc-123";
        var run = CreateRun();

        // Simulate: ExecuteCodeGenerationAsync sets CodegenSessionId on the run
        _agentExecution
            .Setup(e => e.ExecuteCodeGenerationAsync(It.IsAny<AgentPhaseContext>(), It.IsAny<CancellationToken>(), null))
            .Callback<AgentPhaseContext, CancellationToken, string?>((ctx, _, _) =>
            {
                ctx.Run.CodegenSessionId = expectedSessionId;
            })
            .ReturnsAsync(true);

        var context = BuildContext(run);
        var step = new GenerateCodeStep();

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        var activity = _activities.FirstOrDefault(a =>
            a.OperationName == "GenerateCode" &&
            a.GetTagItem("pipeline.run_id") as string == run.RunId);
        activity.Should().NotBeNull();
        activity!.GetTagItem("pipeline.session_id").Should().Be(expectedSessionId);
    }

    [Fact]
    public async Task GenerateCodeStep_DoesNotSetSessionIdTag_WhenCodegenSessionIdIsNull()
    {
        // Arrange
        var run = CreateRun();

        _agentExecution
            .Setup(e => e.ExecuteCodeGenerationAsync(It.IsAny<AgentPhaseContext>(), It.IsAny<CancellationToken>(), null))
            .ReturnsAsync(true);

        var context = BuildContext(run);
        var step = new GenerateCodeStep();

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        var activity = _activities.FirstOrDefault(a =>
            a.OperationName == "GenerateCode" &&
            a.GetTagItem("pipeline.run_id") as string == run.RunId);
        activity.Should().NotBeNull();
        activity!.GetTagItem("pipeline.session_id").Should().BeNull();
    }

    [Fact]
    public async Task ReviewCodeStep_SetsCodegenSessionIdTag_WhenAvailable()
    {
        // Arrange
        const string expectedSessionId = "session-xyz-789";
        var run = CreateRun();
        run.CodegenSessionId = expectedSessionId;

        _agentExecution
            .Setup(e => e.ExecuteCodeReviewAsync(
                It.IsAny<AgentPhaseContext>(), It.IsAny<CancellationToken>(),
                It.IsAny<IReadOnlyList<ReviewerConfiguration>?>()))
            .Returns(Task.CompletedTask);

        var context = BuildContext(run);
        // Pre-resolve reviewers to skip config store
        context.PreResolvedReviewerConfigs = Array.Empty<ReviewerConfiguration>();
        var step = new ReviewCodeStep();

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        var activity = _activities.FirstOrDefault(a =>
            a.OperationName == "ReviewCode" &&
            a.GetTagItem("pipeline.run_id") as string == run.RunId);
        activity.Should().NotBeNull();
        activity!.GetTagItem("pipeline.codegen_session_id").Should().Be(expectedSessionId);
    }

    [Fact]
    public async Task AnalyzeCodeStep_SetsSessionIdTag_WhenSessionAvailableAfterAnalysis()
    {
        // Arrange
        const string expectedSessionId = "session-analysis-456";
        var run = CreateRun();

        _agentExecution
            .Setup(e => e.ExecuteAnalysisPhaseAsync(
                It.IsAny<AgentPhaseContext>(), It.IsAny<IReadOnlyList<IssueComment>>(),
                It.IsAny<bool>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        _agentProvider
            .Setup(p => p.GetLatestSessionIdAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(expectedSessionId);

        var context = BuildContext(run);
        var step = new AnalyzeCodeStep();

        // Act
        await step.ExecuteAsync(context, CancellationToken.None);

        // Assert
        var activity = _activities.FirstOrDefault(a =>
            a.OperationName == "AnalyzeIssue" &&
            a.GetTagItem("pipeline.run_id") as string == run.RunId);
        activity.Should().NotBeNull();
        activity!.GetTagItem("pipeline.session_id").Should().Be(expectedSessionId);
    }

    private PipelineStepContext BuildContext(PipelineRun run)
    {
        var context = new PipelineStepContext
        {
            Run = run,
            Config = new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" },
            RepoProvider = Mock.Of<IRepositoryProvider>(),
            AgentProvider = _agentProvider.Object,
            BrainProvider = null,
            PipelineProvider = null,
            Cts = new CancellationTokenSource(),
            ConfigStore = Mock.Of<IConfigurationStore>(),
            Callbacks = _callbacks.Object,
            IssueOps = Mock.Of<IAgentIssueOperations>(),
            AgentExecution = _agentExecution.Object,
            QualityGates = Mock.Of<IQualityGateExecutor>(),
            BrainSync = null,
            PrOrchestrator = new PullRequestOrchestrator(_logger),
            Logger = _logger
        };

        // Set Issue/ParsedIssue so BuildAgentPhaseContext works
        context.Issue = new IssueDetail
        {
            Identifier = run.IssueIdentifier,
            Title = "Test Issue",
            Description = "Test description",
            Labels = []
        };
        context.ParsedIssue = new ParsedIssue
        {
            RequirementsSection = "requirements",
            AcceptanceCriteria = ["criterion 1"]
        };

        return context;
    }

    private static PipelineRun CreateRun() => new()
    {
        RunId = Guid.NewGuid().ToString(),
        IssueIdentifier = "42",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "ip",
        RepoProviderConfigId = "rp",
        StartedAt = DateTime.UtcNow,
        WorkspacePath = "/tmp/workspace"
    };
}
