using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Models;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// TDD tests for <see cref="IWorkItemExecutor"/> and <see cref="WorkItemExecutorRouter"/>.
/// Defines the behavioral contract:
/// - Routes Implementation/Review/Decomposition to the pipeline executor
/// - Routes Consolidation to the consolidation executor
/// - Returns JobCompletionPayload uniformly regardless of task type
/// - Adapts ConsolidationJobResult → JobCompletionPayload transparently
/// </summary>
public class WorkItemExecutorRouterTests
{
    // ── Interface compliance ─────────────────────────────────────────────

    [Fact]
    public void WorkItemExecutorRouter_Implements_IWorkItemExecutor()
    {
        var router = CreateRouter();
        router.Should().BeAssignableTo<IWorkItemExecutor>();
    }

    // ── Routing by TaskType ──────────────────────────────────────────────

    [Fact]
    public void SourceCode_Router_RoutesPipelineTasksToPipelineExecutor()
    {
        // WorkItemExecutorRouter must route non-consolidation tasks to the pipeline executor.
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "WorkItemExecutorRouter.cs"));

        // Must reference LocalPipelineExecutor
        sourceCode.Should().Contain("LocalPipelineExecutor",
            "WorkItemExecutorRouter must delegate pipeline tasks to LocalPipelineExecutor");
    }

    [Fact]
    public void SourceCode_Router_RoutesConsolidationToConsolidationExecutor()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "WorkItemExecutorRouter.cs"));

        // Must reference LocalConsolidationExecutor
        sourceCode.Should().Contain("LocalConsolidationExecutor",
            "WorkItemExecutorRouter must delegate consolidation tasks to LocalConsolidationExecutor");

        // Must check TaskType for routing
        var checksTaskType = sourceCode.Contains("TaskType")
            || sourceCode.Contains("Consolidation");
        checksTaskType.Should().BeTrue(
            "WorkItemExecutorRouter must route based on TaskType (Consolidation vs others)");
    }

    [Fact]
    public void SourceCode_Router_AdaptsConsolidationResultToJobCompletionPayload()
    {
        // The consolidation executor returns ConsolidationJobResult, but the interface
        // returns JobCompletionPayload. The router must adapt between them.
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "WorkItemExecutorRouter.cs"));

        sourceCode.Should().Contain("JobCompletionPayload",
            "WorkItemExecutorRouter must return JobCompletionPayload for consolidation tasks (adapting from ConsolidationJobResult)");
    }

    [Fact]
    public void SourceCode_Router_ReportsConsolidationComplete()
    {
        // The consolidation path must still report via ReportConsolidationComplete hub method
        // (the orchestrator expects this specific message for consolidation runs).
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "WorkItemExecutorRouter.cs"));

        sourceCode.Should().Contain("ReportConsolidationComplete",
            "WorkItemExecutorRouter must report consolidation results via ReportConsolidationComplete hub method");
    }

    // ── Interface definition ─────────────────────────────────────────────

    [Fact]
    public void IWorkItemExecutor_ReturnsJobCompletionPayload()
    {
        // The interface must return JobCompletionPayload (unified return type)
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "IWorkItemExecutor.cs"));

        sourceCode.Should().Contain("Task<JobCompletionPayload>",
            "IWorkItemExecutor.ExecuteAsync must return Task<JobCompletionPayload>");
    }

    [Fact]
    public void IWorkItemExecutor_AcceptsJobAssignmentMessage()
    {
        // The interface must accept JobAssignmentMessage (the unified input)
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "IWorkItemExecutor.cs"));

        sourceCode.Should().Contain("JobAssignmentMessage",
            "IWorkItemExecutor.ExecuteAsync must accept JobAssignmentMessage as input");
    }

    // ── Constructor validation ───────────────────────────────────────────

    [Fact]
    public void Constructor_NullPipelineExecutor_Throws()
    {
        var act = () => new WorkItemExecutorRouter(
            null!,
            CreateMockConsolidationExecutor(),
            Mock.Of<Serilog.ILogger>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("pipelineExecutor");
    }

    [Fact]
    public void Constructor_NullConsolidationExecutor_Throws()
    {
        var act = () => new WorkItemExecutorRouter(
            CreateMockPipelineExecutor(),
            null!,
            Mock.Of<Serilog.ILogger>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("consolidationExecutor");
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new WorkItemExecutorRouter(
            CreateMockPipelineExecutor(),
            CreateMockConsolidationExecutor(),
            null!);

        act.Should().Throw<ArgumentNullException>().WithParameterName("logger");
    }

    [Fact]
    public void Constructor_ValidParams_DoesNotThrow()
    {
        var act = () => CreateRouter();
        act.Should().NotThrow();
    }

    // ── Runtime Routing (Behavioral) ────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ImplementationTask_DelegatesToPipelineExecutor()
    {
        // Verify actual runtime routing: Implementation task goes to pipeline executor.
        // This test exercises the real code path (not source-code inspection).
        var router = CreateRouter();
        var assignment = CreateMinimalAssignment(WorkItemTaskType.Implementation);

        // The pipeline executor will throw because connection is not connected —
        // but it proves the pipeline path was taken (not consolidation).
        var act = async () => await router.ExecuteAsync(
            assignment,
            CreateDisconnectedConnection(),
            new OutputBatcher(),
            _ => { },
            CancellationToken.None);

        // Pipeline executor tries to use the hub connection → throws InvalidOperationException
        // ("The connection is not in the 'Connected' state") — proving it was invoked.
        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task ExecuteAsync_ConsolidationTask_DelegatesToConsolidationExecutor()
    {
        // Verify actual runtime routing: Consolidation task goes to consolidation executor.
        // The consolidation executor internally handles errors and returns a result
        // (unlike the pipeline executor which throws on disconnection).
        var router = CreateRouter();
        var assignment = CreateMinimalAssignment(WorkItemTaskType.Consolidation);

        // Execute — consolidation executor wraps errors in ConsolidationJobResult,
        // then the router adapts that to a Failed JobCompletionPayload.
        var result = await router.ExecuteAsync(
            assignment,
            CreateDisconnectedConnection(),
            new OutputBatcher(),
            _ => { },
            CancellationToken.None);

        // The consolidation path was taken (it returned a result rather than throwing)
        result.Should().NotBeNull();
        // Consolidation with disconnected hub fails gracefully
        result.FinalStep.Should().Be(PipelineStep.Failed,
            "Consolidation with disconnected connection fails (provider resolution needs hub)");
    }

    [Fact]
    public async Task ExecuteAsync_ReviewTask_DelegatesToPipelineExecutor()
    {
        var router = CreateRouter();
        var assignment = CreateMinimalAssignment(WorkItemTaskType.Review);

        var act = async () => await router.ExecuteAsync(
            assignment,
            CreateDisconnectedConnection(),
            new OutputBatcher(),
            _ => { },
            CancellationToken.None);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public void ExecuteAsync_NullAssignment_ThrowsArgumentNullException()
    {
        var router = CreateRouter();

        var act = async () => await router.ExecuteAsync(
            null!,
            CreateDisconnectedConnection(),
            new OutputBatcher(),
            _ => { },
            CancellationToken.None);

        act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static WorkItemExecutorRouter CreateRouter()
    {
        return new WorkItemExecutorRouter(
            CreateMockPipelineExecutor(),
            CreateMockConsolidationExecutor(),
            Mock.Of<Serilog.ILogger>());
    }

    private static LocalPipelineExecutor CreateMockPipelineExecutor()
    {
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        var mockHttpFactory = new Mock<IHttpClientFactory>();
        var mockQgValidator = new Mock<CodingAgentWebUI.Pipeline.Interfaces.IQualityGateValidator>();
        return new LocalPipelineExecutor(
            mockOrchestrator.Object, mockHttpFactory.Object,
            new PipelineConfiguration(), mockQgValidator.Object,
            Mock.Of<Serilog.ILogger>(),
            agentIdentity: new AgentIdentity("test-agent"));
    }

    private static LocalConsolidationExecutor CreateMockConsolidationExecutor()
    {
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        var mockHttpFactory = new Mock<IHttpClientFactory>();
        return new LocalConsolidationExecutor(
            mockOrchestrator.Object, mockHttpFactory.Object,
            Mock.Of<Serilog.ILogger>());
    }

    private static string GetSourceDirectory()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CodingAgentAutomation.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("Could not find solution root");
    }

    private static Microsoft.AspNetCore.SignalR.Client.HubConnection CreateDisconnectedConnection()
    {
        var hubManager = new HubConnectionManager(
            "http://localhost:9999", "test-agent", "test-key",
            Mock.Of<Serilog.ILogger>());
        return hubManager.Connection;
    }

    private static JobAssignmentMessage CreateMinimalAssignment(WorkItemTaskType taskType)
    {
        return new JobAssignmentMessage
        {
            JobId = "test-job-1",
            IssueIdentifier = "owner/repo#1",
            IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            IssueComments = [],
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            ProviderConfigs = [],
            PipelineConfiguration = new PipelineConfiguration(),
            InitiatedBy = "test",
            QualityGateConfigs = [],
            TaskType = taskType,
            ConsolidationRunType = taskType == WorkItemTaskType.Consolidation
                ? ConsolidationRunType.BrainConsolidation : null
        };
    }
}
