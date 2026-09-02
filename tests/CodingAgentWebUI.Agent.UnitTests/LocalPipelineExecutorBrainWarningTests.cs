using AwesomeAssertions;
using CodingAgentWebUI.Agent;
using CodingAgentWebUI.Infrastructure;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using KiroCliLib.Core;
using Microsoft.AspNetCore.SignalR.Client;
using Moq;

namespace CodingAgentWebUI.Agent.UnitTests;

/// <summary>
/// Tests for the "no brain provider" diagnostic warning in <see cref="LocalPipelineExecutor"/>.
/// These tests verify that the warning fires for implementation-type runs when
/// <see cref="JobAssignmentMessage.BrainProviderConfigId"/> is null or empty, and that it
/// does NOT fire for Review/DecompositionAnalysis/Decomposition run types.
/// </summary>
public class LocalPipelineExecutorBrainWarningTests : IDisposable
{
    private readonly Mock<Serilog.ILogger> _mockLogger = new();
    private readonly Mock<IAgentProviderResolver> _mockResolver = new();
    private readonly Mock<IKiroCliOrchestrator> _mockOrchestrator = new();
    private readonly Mock<IHttpClientFactory> _mockHttpClientFactory = new();
    private readonly Mock<IQualityGateValidator> _mockQualityGateValidator = new();

    private readonly ProviderConfig _repoConfig = new()
    {
        Id = "repo-1",
        Kind = ProviderKind.Repository,
        ProviderType = "GitHub",
        DisplayName = "Test Repo",
        Settings = new Dictionary<string, string>()
    };
    private readonly ProviderConfig _agentConfig = new()
    {
        Id = "agent-1",
        Kind = ProviderKind.Agent,
        ProviderType = "KiroCli",
        DisplayName = "Test Agent",
        Settings = new Dictionary<string, string>()
    };

    public LocalPipelineExecutorBrainWarningTests()
    {
        // Make resolver return mock providers so execution reaches the warning check at line ~145.
        var mockRepoProvider = new Mock<IRepositoryProvider>();
        var mockAgentProvider = new Mock<IAgentProvider>();
        mockAgentProvider.Setup(p => p.PipelineInjectedPaths).Returns([]);
        mockAgentProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        mockRepoProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _mockResolver
            .Setup(r => r.ResolveAsync(
                It.IsAny<JobAssignmentMessage>(),
                It.IsAny<IProviderFactory>(),
                It.IsAny<ProviderConfig>(),
                It.IsAny<ProviderConfig>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ResolvedProviders(
                mockRepoProvider.Object,
                mockAgentProvider.Object,
                BrainProvider: null,
                PipelineProvider: null,
                AdditionalRepoProviders: null));
    }

    public void Dispose() { }

    // ── Warning fires for implementation run with no brain provider ──────

    [Fact]
    public async Task ExecuteAsync_ImplementationRunWithNoBrainProvider_LogsWarning()
    {
        var executor = CreateExecutorWithMockResolver();
        var job = CreateJob(PipelineRunType.Implementation, brainProviderConfigId: "");

        await using var connection = CreateDisconnectedHubConnection();
        await using var batcher = new OutputBatcher();

        // Act — will throw once it reaches ExecutePipelineStepsAsync (mock step runner isn't set up)
        // but the warning at line ~145 fires before that point.
        try { await executor.ExecuteAsync(job, connection, batcher, null, CancellationToken.None); }
        catch { /* expected — pipeline steps are not mocked */ }

        _mockLogger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("no BrainProviderConfigId")),
                It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_ImplementationRunWithNullBrainProvider_LogsWarning()
    {
        var executor = CreateExecutorWithMockResolver();
        var job = CreateJob(PipelineRunType.Implementation, brainProviderConfigId: null);

        await using var connection = CreateDisconnectedHubConnection();
        await using var batcher = new OutputBatcher();

        try { await executor.ExecuteAsync(job, connection, batcher, null, CancellationToken.None); }
        catch { /* expected */ }

        _mockLogger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("no BrainProviderConfigId")),
                It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    // ── Warning does NOT fire when brain provider is configured ──────────

    [Fact]
    public async Task ExecuteAsync_ImplementationRunWithBrainProvider_DoesNotLogWarning()
    {
        var executor = CreateExecutorWithMockResolver();
        var brainConfig = new ProviderConfig
        {
            Id = "brain-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Brain Repo",
            RepositoryRole = RepositoryRole.Brain,
            Settings = new Dictionary<string, string>()
        };
        var job = CreateJob(PipelineRunType.Implementation, brainProviderConfigId: "brain-1", extraConfigs: [brainConfig]);

        await using var connection = CreateDisconnectedHubConnection();
        await using var batcher = new OutputBatcher();

        try { await executor.ExecuteAsync(job, connection, batcher, null, CancellationToken.None); }
        catch { /* expected */ }

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
    public async Task ExecuteAsync_ReviewOrDecompositionRunWithNoBrainProvider_DoesNotLogWarning(PipelineRunType runType)
    {
        var executor = CreateExecutorWithMockResolver();
        var job = CreateJob(runType, brainProviderConfigId: "");

        await using var connection = CreateDisconnectedHubConnection();
        await using var batcher = new OutputBatcher();

        try { await executor.ExecuteAsync(job, connection, batcher, null, CancellationToken.None); }
        catch { /* expected */ }

        _mockLogger.Verify(
            l => l.Warning(
                It.Is<string>(s => s.Contains("no BrainProviderConfigId")),
                It.IsAny<string>(), It.IsAny<string>()),
            Times.Never);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private LocalPipelineExecutor CreateExecutorWithMockResolver()
    {
        var deps = new LocalPipelineExecutorDependencies(
            _mockOrchestrator.Object,
            _mockHttpClientFactory.Object,
            new PipelineConfiguration(),
            _mockQualityGateValidator.Object,
            _mockLogger.Object,
            AgentIdentity: new AgentId("test-agent"));
        return new LocalPipelineExecutor(deps, _mockResolver.Object);
    }

    private JobAssignmentMessage CreateJob(
        PipelineRunType runType,
        string? brainProviderConfigId,
        ProviderConfig[]? extraConfigs = null)
    {
        var configs = new List<ProviderConfig> { _repoConfig, _agentConfig };
        if (extraConfigs is not null)
            configs.AddRange(extraConfigs);

        return new JobAssignmentMessage
        {
            JobId = "test-job-brain-warning",
            IssueIdentifier = "owner/repo#1",
            RunType = runType,
            IssueDetail = new IssueDetail { Identifier = "owner/repo#1", Title = "Test", Description = "", Labels = [] },
            ParsedIssue = new ParsedIssue { RequirementsSection = "", AcceptanceCriteria = [] },
            RepoProviderConfigId = "repo-1",
            AgentProviderConfigId = "agent-1",
            BrainProviderConfigId = brainProviderConfigId,
            PipelineConfiguration = new PipelineConfiguration(),
            ProviderConfigs = configs,
            ReviewerConfigs = [],
            QualityGateConfigs = [],
            IssueComments = [],
            InitiatedBy = "test-user"
        };
    }

    private static HubConnection CreateDisconnectedHubConnection() =>
        new HubConnectionBuilder()
            .WithUrl("http://localhost/agent-hub", options =>
            {
                options.HttpMessageHandlerFactory = _ => new NoOpHandler();
            })
            .Build();

    private sealed class NoOpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
    }
}
