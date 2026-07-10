using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// TDD tests for <see cref="IConsolidationExecutor"/> interface extraction (R2).
/// Defines the behavioral contract:
/// - LocalConsolidationExecutor implements IConsolidationExecutor
/// - WorkItemExecutorRouter depends on IConsolidationExecutor (not concrete)
/// - AgentWorkerService depends on IConsolidationExecutor (not concrete)
/// - DI resolves IConsolidationExecutor
/// </summary>
public class IConsolidationExecutorTests
{
    // ── Interface compliance ─────────────────────────────────────────────

    [Fact]
    public void LocalConsolidationExecutor_Implements_IConsolidationExecutor()
    {
        var executor = CreateConsolidationExecutor();
        executor.Should().BeAssignableTo<IConsolidationExecutor>();
    }

    // ── WorkItemExecutorRouter depends on interface ──────────────────────

    [Fact]
    public void WorkItemExecutorRouter_Constructor_AcceptsIConsolidationExecutor()
    {
        // The router must accept IConsolidationExecutor (not concrete LocalConsolidationExecutor)
        var mockConsolidation = new Mock<IConsolidationExecutor>();
        var mockPipeline = CreateMockPipelineExecutor();

        var act = () => new WorkItemExecutorRouter(
            mockPipeline,
            mockConsolidation.Object,
            Mock.Of<Serilog.ILogger>());

        act.Should().NotThrow();
    }

    [Fact]
    public void WorkItemExecutorRouter_Constructor_NullIConsolidationExecutor_Throws()
    {
        var mockPipeline = CreateMockPipelineExecutor();

        var act = () => new WorkItemExecutorRouter(
            mockPipeline,
            (IConsolidationExecutor)null!,
            Mock.Of<Serilog.ILogger>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("consolidationExecutor");
    }

    // ── AgentWorkerService depends on interface ──────────────────────────

    [Fact]
    public void SourceCode_AgentWorkerService_DependsOnIConsolidationExecutor()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "AgentWorkerService.cs"));

        sourceCode.Should().Contain("IConsolidationExecutor",
            "AgentWorkerService must depend on IConsolidationExecutor interface, not concrete LocalConsolidationExecutor");
    }

    // ── Interface definition ─────────────────────────────────────────────

    [Fact]
    public void IConsolidationExecutor_HasExecuteAsyncMethod()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "IConsolidationExecutor.cs"));

        sourceCode.Should().Contain("Task<ConsolidationJobResult> ExecuteAsync",
            "IConsolidationExecutor must define ExecuteAsync returning Task<ConsolidationJobResult>");
    }

    [Fact]
    public void IConsolidationExecutor_AcceptsConsolidationJobMessage()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "IConsolidationExecutor.cs"));

        sourceCode.Should().Contain("ConsolidationJobMessage",
            "IConsolidationExecutor.ExecuteAsync must accept ConsolidationJobMessage");
    }

    // ── Behavioral test: mock consolidation executor ─────────────────────

    [Fact]
    public async Task WorkItemExecutorRouter_ConsolidationTask_DelegatesToIConsolidationExecutor()
    {
        // When we can mock IConsolidationExecutor, we can fully control its behavior
        var mockConsolidation = new Mock<IConsolidationExecutor>();
        mockConsolidation
            .Setup(x => x.ExecuteAsync(
                It.IsAny<ConsolidationJobMessage>(),
                It.IsAny<Microsoft.AspNetCore.SignalR.Client.HubConnection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationJobResult
            {
                JobId = "test-job",
                Success = true
            });

        var mockPipeline = CreateMockPipelineExecutor();
        var router = new WorkItemExecutorRouter(
            mockPipeline,
            mockConsolidation.Object,
            Mock.Of<Serilog.ILogger>());

        var assignment = CreateMinimalAssignment(WorkItemTaskType.Consolidation);
        var result = await router.ExecuteAsync(
            assignment,
            CreateDisconnectedConnection(),
            new OutputBatcher(),
            _ => { },
            CancellationToken.None);

        // Verify the mock was called (proves routing went to IConsolidationExecutor)
        mockConsolidation.Verify(x => x.ExecuteAsync(
            It.IsAny<ConsolidationJobMessage>(),
            It.IsAny<Microsoft.AspNetCore.SignalR.Client.HubConnection>(),
            It.IsAny<CancellationToken>()), Times.Once);

        // Verify the result was adapted correctly
        result.FinalStep.Should().Be(PipelineStep.Completed);
    }

    [Fact]
    public async Task WorkItemExecutorRouter_ConsolidationTask_FailedResult_AdaptsToFailedPayload()
    {
        var mockConsolidation = new Mock<IConsolidationExecutor>();
        mockConsolidation
            .Setup(x => x.ExecuteAsync(
                It.IsAny<ConsolidationJobMessage>(),
                It.IsAny<Microsoft.AspNetCore.SignalR.Client.HubConnection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationJobResult
            {
                JobId = "test-job",
                Success = false,
                ErrorMessage = "Provider resolution failed"
            });

        var mockPipeline = CreateMockPipelineExecutor();
        var router = new WorkItemExecutorRouter(
            mockPipeline,
            mockConsolidation.Object,
            Mock.Of<Serilog.ILogger>());

        var assignment = CreateMinimalAssignment(WorkItemTaskType.Consolidation);
        var result = await router.ExecuteAsync(
            assignment,
            CreateDisconnectedConnection(),
            new OutputBatcher(),
            _ => { },
            CancellationToken.None);

        result.FinalStep.Should().Be(PipelineStep.Failed);
        result.FailureReason.Should().Be("Provider resolution failed");
    }

    // ── DI Resolution ────────────────────────────────────────────────────

    [Fact]
    public void DI_CanResolve_IConsolidationExecutor()
    {
        // IConsolidationExecutor should be resolvable from the DI container
        var services = new Microsoft.Extensions.DependencyInjection.ServiceCollection();

        // Minimal registrations
        Serilog.Log.Logger = new Serilog.LoggerConfiguration().CreateLogger();
        services.AddSingleton(Serilog.Log.Logger);
        services.AddSingleton<KiroCliLib.Configuration.Configuration>(new KiroCliLib.Configuration.Configuration
        {
            KiroCliPath = "/usr/local/bin/kiro-cli",
            UseWsl = false,
            WorkspaceDirectory = "/tmp"
        });
        services.AddSingleton<KiroCliLib.Core.IKiroCliOrchestrator>(sp =>
            new KiroCliLib.Core.KiroCliOrchestrator(
                sp.GetRequiredService<KiroCliLib.Configuration.Configuration>(),
                Serilog.Log.Logger));
        services.AddHttpClient();
        services.AddSingleton<IConsolidationExecutor>(sp => new LocalConsolidationExecutor(
            sp.GetRequiredService<KiroCliLib.Core.IKiroCliOrchestrator>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            Serilog.Log.Logger));

        using var sp = services.BuildServiceProvider();
        var executor = sp.GetRequiredService<IConsolidationExecutor>();

        executor.Should().NotBeNull();
        executor.Should().BeOfType<LocalConsolidationExecutor>();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static LocalConsolidationExecutor CreateConsolidationExecutor()
    {
        var mockOrchestrator = new Mock<KiroCliLib.Core.IKiroCliOrchestrator>();
        var mockHttpFactory = new Mock<IHttpClientFactory>();
        return new LocalConsolidationExecutor(
            mockOrchestrator.Object, mockHttpFactory.Object,
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
