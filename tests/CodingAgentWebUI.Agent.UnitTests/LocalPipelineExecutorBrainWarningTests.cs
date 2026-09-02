using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests for the "no brain provider" diagnostic warning in <see cref="LocalPipelineExecutor"/>.
/// These tests call <see cref="LocalPipelineExecutor.WarnIfNoBrainProvider"/> directly to
/// avoid triggering <see cref="LocalPipelineExecutor.ExecuteAsync"/>'s full pipeline execution,
/// which emits <c>pipeline.jobs.*</c> telemetry counters that pollute cross-assembly
/// <see cref="System.Diagnostics.Metrics.MeterListener"/> instances in parallel test runs.
/// </summary>
public class LocalPipelineExecutorBrainWarningTests
{
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly Mock<IKiroCliOrchestrator> _mockOrchestrator = new();
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory = new();
    private readonly Mock<IQualityGateValidator> _mockQualityGateValidator = new();

    // ── Warning fires for implementation run with no brain provider ──────

    [Fact]
    public void WarnIfNoBrainProvider_ImplementationRunWithEmptyBrainProviderConfigId_LogsWarning()
    {
        var executor = CreateExecutor();
        var job = CreateJob(PipelineRunType.Implementation, brainProviderConfigId: "");

        executor.WarnIfNoBrainProvider(job);

        _mockLogger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("no BrainProviderConfigId")),
                It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public void WarnIfNoBrainProvider_ImplementationRunWithNullBrainProviderConfigId_LogsWarning()
    {
        var executor = CreateExecutor();
        var job = CreateJob(PipelineRunType.Implementation, brainProviderConfigId: null);

        executor.WarnIfNoBrainProvider(job);

        _mockLogger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("no BrainProviderConfigId")),
                It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    // ── Warning does NOT fire when brain provider is configured ──────────

    [Fact]
    public void WarnIfNoBrainProvider_ImplementationRunWithBrainProviderConfigId_DoesNotLogWarning()
    {
        var executor = CreateExecutor();
        var job = CreateJob(PipelineRunType.Implementation, brainProviderConfigId: "brain-1");

        executor.WarnIfNoBrainProvider(job);

        _mockLogger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("no BrainProviderConfigId")),
                It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    // ── Warning does NOT fire for review/decomposition run types ─────────

    [Theory]
    [InlineData(PipelineRunType.Review)]
    [InlineData(PipelineRunType.DecompositionAnalysis)]
    [InlineData(PipelineRunType.Decomposition)]
    public void WarnIfNoBrainProvider_ReviewOrDecompositionRunWithNoBrainProvider_DoesNotLogWarning(PipelineRunType runType)
    {
        var executor = CreateExecutor();
        var job = CreateJob(runType, brainProviderConfigId: "");

        executor.WarnIfNoBrainProvider(job);

        _mockLogger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("no BrainProviderConfigId")),
                It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    // ── Warning includes job context in structured log properties ─────────

    [Fact]
    public void WarnIfNoBrainProvider_LogsJobIdAndIssueIdentifier()
    {
        var executor = CreateExecutor();
        var job = CreateJob(PipelineRunType.Implementation, brainProviderConfigId: null,
            jobId: "job-123", issueIdentifier: "owner/repo#42");

        executor.WarnIfNoBrainProvider(job);

        _mockLogger.Verify(
            l => l.Warning(
                It.IsAny<string>(),
                It.Is<string>(s => s == "job-123"),
                It.Is<string>(s => s == "owner/repo#42")),
            Times.Once);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private LocalPipelineExecutor CreateExecutor() =>
        new(new LocalPipelineExecutorDependencies(
            _mockOrchestrator.Object,
            _mockHttpClientFactory.Object,
            new PipelineConfiguration(),
            _mockQualityGateValidator.Object,
            _mockLogger.Object,
            AgentIdentity: new AgentId("test-agent")));

    private static JobAssignmentMessage CreateJob(
        PipelineRunType runType,
        string? brainProviderConfigId,
        string jobId = "test-job",
        string issueIdentifier = "owner/repo#1") =>
        new()
        {
            JobId = jobId,
            IssueIdentifier = issueIdentifier,
            RunType = runType,
            IssueDetail = new IssueDetail { Identifier = issueIdentifier, Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            BrainProviderConfigId = brainProviderConfigId,
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = [],
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            InitiatedBy = "test-user"
        };
}
