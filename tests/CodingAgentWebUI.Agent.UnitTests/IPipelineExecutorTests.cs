using AwesomeAssertions;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// TDD tests for <see cref="IPipelineExecutor"/> interface extraction (R1).
/// Defines the behavioral contract:
/// - LocalPipelineExecutor implements IPipelineExecutor
/// - WorkItemExecutorRouter depends on IPipelineExecutor (not concrete)
/// - AgentWorkerService depends on IPipelineExecutor (not concrete)
/// - DI resolves IPipelineExecutor
/// </summary>
public class IPipelineExecutorTests
{
    // ── Interface compliance ─────────────────────────────────────────────

    [Fact]
    public void LocalPipelineExecutor_Implements_IPipelineExecutor()
    {
        var executor = CreatePipelineExecutor();
        executor.Should().BeAssignableTo<IPipelineExecutor>();
    }

    // ── WorkItemExecutorRouter depends on interface ──────────────────────

    [Fact]
    public void WorkItemExecutorRouter_Constructor_AcceptsIPipelineExecutor()
    {
        // The router must accept IPipelineExecutor (not concrete LocalPipelineExecutor)
        var mockPipeline = new Mock<IPipelineExecutor>();
        var mockConsolidation = new Mock<IConsolidationExecutor>();

        var act = () => new WorkItemExecutorRouter(
            mockPipeline.Object,
            mockConsolidation.Object,
            Mock.Of<Serilog.ILogger>());

        act.Should().NotThrow();
    }

    [Fact]
    public void WorkItemExecutorRouter_Constructor_NullIPipelineExecutor_Throws()
    {
        var mockConsolidation = new Mock<IConsolidationExecutor>();

        var act = () => new WorkItemExecutorRouter(
            (IPipelineExecutor)null!,
            mockConsolidation.Object,
            Mock.Of<Serilog.ILogger>());

        act.Should().Throw<ArgumentNullException>().WithParameterName("pipelineExecutor");
    }

    // ── AgentWorkerService depends on interface ──────────────────────────

    [Fact]
    public void SourceCode_AgentWorkerService_DependsOnIPipelineExecutor()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "AgentWorkerService.cs"));

        sourceCode.Should().Contain("IPipelineExecutor",
            "AgentWorkerService must depend on IPipelineExecutor interface, not concrete LocalPipelineExecutor");
    }

    // ── Interface definition ─────────────────────────────────────────────

    [Fact]
    public void IPipelineExecutor_HasExecuteAsyncMethod()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "IPipelineExecutor.cs"));

        sourceCode.Should().Contain("Task<JobCompletionPayload> ExecuteAsync",
            "IPipelineExecutor must define ExecuteAsync returning Task<JobCompletionPayload>");
    }

    [Fact]
    public void IPipelineExecutor_AcceptsJobAssignmentMessage()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "IPipelineExecutor.cs"));

        sourceCode.Should().Contain("JobAssignmentMessage",
            "IPipelineExecutor.ExecuteAsync must accept JobAssignmentMessage");
    }

    [Fact]
    public void IPipelineExecutor_AcceptsOutputBatcher()
    {
        var sourceCode = File.ReadAllText(
            Path.Combine(GetSourceDirectory(), "src", "CodingAgentWebUI.Agent", "IPipelineExecutor.cs"));

        sourceCode.Should().Contain("OutputBatcher",
            "IPipelineExecutor.ExecuteAsync must accept OutputBatcher for streaming output");
    }

    // ── Behavioral test: mock pipeline executor ──────────────────────────

    [Fact]
    public async Task WorkItemExecutorRouter_ImplementationTask_DelegatesToIPipelineExecutor()
    {
        var mockPipeline = new Mock<IPipelineExecutor>();
        mockPipeline
            .Setup(x => x.ExecuteAsync(
                It.IsAny<JobAssignmentMessage>(),
                It.IsAny<Microsoft.AspNetCore.SignalR.Client.HubConnection>(),
                It.IsAny<OutputBatcher>(),
                It.IsAny<Action<PipelineStep?>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobCompletionPayload
            {
                FinalStep = PipelineStep.Completed,
                CompletedAt = DateTimeOffset.UtcNow
            });

        var mockConsolidation = new Mock<IConsolidationExecutor>();
        var router = new WorkItemExecutorRouter(
            mockPipeline.Object,
            mockConsolidation.Object,
            Mock.Of<Serilog.ILogger>());

        var assignment = CreateMinimalAssignment(WorkItemTaskType.Implementation);
        var result = await router.ExecuteAsync(
            assignment,
            CreateDisconnectedConnection(),
            new OutputBatcher(),
            _ => { },
            CancellationToken.None);

        // Verify the mock was called (proves routing went to IPipelineExecutor)
        mockPipeline.Verify(x => x.ExecuteAsync(
            It.IsAny<JobAssignmentMessage>(),
            It.IsAny<Microsoft.AspNetCore.SignalR.Client.HubConnection>(),
            It.IsAny<OutputBatcher>(),
            It.IsAny<Action<PipelineStep?>?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        result.FinalStep.Should().Be(PipelineStep.Completed);
    }

    [Fact]
    public async Task WorkItemExecutorRouter_ReviewTask_DelegatesToIPipelineExecutor()
    {
        var mockPipeline = new Mock<IPipelineExecutor>();
        mockPipeline
            .Setup(x => x.ExecuteAsync(
                It.IsAny<JobAssignmentMessage>(),
                It.IsAny<Microsoft.AspNetCore.SignalR.Client.HubConnection>(),
                It.IsAny<OutputBatcher>(),
                It.IsAny<Action<PipelineStep?>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobCompletionPayload
            {
                FinalStep = PipelineStep.Completed,
                CompletedAt = DateTimeOffset.UtcNow
            });

        var mockConsolidation = new Mock<IConsolidationExecutor>();
        var router = new WorkItemExecutorRouter(
            mockPipeline.Object,
            mockConsolidation.Object,
            Mock.Of<Serilog.ILogger>());

        var assignment = CreateMinimalAssignment(WorkItemTaskType.Review);
        var result = await router.ExecuteAsync(
            assignment,
            CreateDisconnectedConnection(),
            new OutputBatcher(),
            _ => { },
            CancellationToken.None);

        mockPipeline.Verify(x => x.ExecuteAsync(
            It.IsAny<JobAssignmentMessage>(),
            It.IsAny<Microsoft.AspNetCore.SignalR.Client.HubConnection>(),
            It.IsAny<OutputBatcher>(),
            It.IsAny<Action<PipelineStep?>?>(),
            It.IsAny<CancellationToken>()), Times.Once);

        result.FinalStep.Should().Be(PipelineStep.Completed);
    }

    [Fact]
    public async Task WorkItemExecutorRouter_ConsolidationTask_DoesNotCallIPipelineExecutor()
    {
        var mockPipeline = new Mock<IPipelineExecutor>();
        var mockConsolidation = new Mock<IConsolidationExecutor>();
        mockConsolidation
            .Setup(x => x.ExecuteAsync(
                It.IsAny<ConsolidationJobMessage>(),
                It.IsAny<Microsoft.AspNetCore.SignalR.Client.HubConnection>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ConsolidationJobResult { JobId = "test", Success = true });

        var router = new WorkItemExecutorRouter(
            mockPipeline.Object,
            mockConsolidation.Object,
            Mock.Of<Serilog.ILogger>());

        var assignment = CreateMinimalAssignment(WorkItemTaskType.Consolidation);
        await router.ExecuteAsync(
            assignment,
            CreateDisconnectedConnection(),
            new OutputBatcher(),
            _ => { },
            CancellationToken.None);

        // Pipeline executor should NOT be called for consolidation tasks
        mockPipeline.Verify(x => x.ExecuteAsync(
            It.IsAny<JobAssignmentMessage>(),
            It.IsAny<Microsoft.AspNetCore.SignalR.Client.HubConnection>(),
            It.IsAny<OutputBatcher>(),
            It.IsAny<Action<PipelineStep?>?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task WorkItemExecutorRouter_PipelineFailure_PropagatesPayload()
    {
        var mockPipeline = new Mock<IPipelineExecutor>();
        mockPipeline
            .Setup(x => x.ExecuteAsync(
                It.IsAny<JobAssignmentMessage>(),
                It.IsAny<Microsoft.AspNetCore.SignalR.Client.HubConnection>(),
                It.IsAny<OutputBatcher>(),
                It.IsAny<Action<PipelineStep?>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new JobCompletionPayload
            {
                FinalStep = PipelineStep.Failed,
                FailureReason = "Tests did not pass",
                CompletedAt = DateTimeOffset.UtcNow
            });

        var mockConsolidation = new Mock<IConsolidationExecutor>();
        var router = new WorkItemExecutorRouter(
            mockPipeline.Object,
            mockConsolidation.Object,
            Mock.Of<Serilog.ILogger>());

        var assignment = CreateMinimalAssignment(WorkItemTaskType.Implementation);
        var result = await router.ExecuteAsync(
            assignment,
            CreateDisconnectedConnection(),
            new OutputBatcher(),
            _ => { },
            CancellationToken.None);

        result.FinalStep.Should().Be(PipelineStep.Failed);
        result.FailureReason.Should().Be("Tests did not pass");
    }

    // ── DI Resolution ────────────────────────────────────────────────────

    [Fact]
    public void DI_CanResolve_IPipelineExecutor()
    {
        var services = new ServiceCollection();

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
                callbackHandler: null, Serilog.Log.Logger));
        services.AddHttpClient();
        services.AddSingleton(new PipelineConfiguration());
        services.AddSingleton(new AgentIdentity("test-agent"));
        services.AddSingleton<CodingAgentWebUI.Pipeline.Interfaces.IQualityGateValidator>(
            Mock.Of<CodingAgentWebUI.Pipeline.Interfaces.IQualityGateValidator>());
        services.AddSingleton<IPipelineExecutor>(sp => new LocalPipelineExecutor(
            sp.GetRequiredService<KiroCliLib.Core.IKiroCliOrchestrator>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<PipelineConfiguration>(),
            sp.GetRequiredService<CodingAgentWebUI.Pipeline.Interfaces.IQualityGateValidator>(),
            Serilog.Log.Logger,
            agentIdentity: sp.GetRequiredService<AgentIdentity>()));

        using var sp = services.BuildServiceProvider();
        var executor = sp.GetRequiredService<IPipelineExecutor>();

        executor.Should().NotBeNull();
        executor.Should().BeOfType<LocalPipelineExecutor>();
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static LocalPipelineExecutor CreatePipelineExecutor()
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
