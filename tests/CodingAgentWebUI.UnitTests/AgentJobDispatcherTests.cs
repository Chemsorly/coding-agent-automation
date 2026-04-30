using AwesomeAssertions;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests;

/// <summary>
/// Unit tests for <see cref="AgentJobDispatcher"/>.
/// </summary>
public class AgentJobDispatcherTests
{
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly AgentRegistryService _registry;
    private readonly JobDispatcherService _dispatcher;
    private readonly OrchestratorRunService _runService;
    private readonly Mock<IConfigurationStore> _mockConfigStore;
    private readonly Mock<IProviderFactory> _mockProviderFactory;
    private readonly Mock<IHubContext<AgentHub, IAgentHubClient>> _mockHubContext;
    private readonly TokenVendingService _tokenVending;
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService;

    public AgentJobDispatcherTests()
    {
        _registry = new AgentRegistryService(_mockLogger.Object);
        _dispatcher = new JobDispatcherService(_registry, _mockLogger.Object);
        _runService = new OrchestratorRunService(_mockLogger.Object);
        _mockConfigStore = new Mock<IConfigurationStore>();
        _mockProviderFactory = new Mock<IProviderFactory>();
        _mockHubContext = new Mock<IHubContext<AgentHub, IAgentHubClient>>();
        _tokenVending = new TokenVendingService(_mockLogger.Object);
        _mockHistoryService = new Mock<IPipelineRunHistoryService>();
    }

    private AgentJobDispatcher CreateDispatcher()
    {
        var mockQualityGateValidator = new Mock<IQualityGateValidator>();
        var mockBrainUpdateService = new Mock<IBrainUpdateService>();
        var ciLogWriter = new CiLogWriter(_mockLogger.Object);
        var issueParser = new IssueDescriptionParser();

        var orchestration = new PipelineOrchestrationService(
            _mockConfigStore.Object,
            _mockProviderFactory.Object,
            issueParser,
            mockQualityGateValidator.Object,
            ciLogWriter,
            _mockLogger.Object,
            mockBrainUpdateService.Object,
            _mockHistoryService.Object,
            _runService);

        return new AgentJobDispatcher(
            _dispatcher,
            _registry,
            _runService,
            orchestration,
            _tokenVending,
            _mockConfigStore.Object,
            _mockProviderFactory.Object,
            new ProfileResolver(),
            new QualityGateResolver(),
            new ReviewerResolver(),
            _mockHubContext.Object,
            _mockLogger.Object);
    }

    [Fact]
    public void HasRegisteredAgents_NoAgents_ReturnsFalse()
    {
        var dispatcher = CreateDispatcher();
        dispatcher.HasRegisteredAgents.Should().BeFalse();
    }

    [Fact]
    public void HasRegisteredAgents_WithAgent_ReturnsTrue()
    {
        _registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "host",
            AgentType = "kiro",
            Labels = new[] { "dotnet" }
        }, "conn-1");

        var dispatcher = CreateDispatcher();
        dispatcher.HasRegisteredAgents.Should().BeTrue();
    }

    [Fact]
    public void IsIssueBeingProcessedOrQueued_NotQueued_ReturnsFalse()
    {
        var dispatcher = CreateDispatcher();
        dispatcher.IsIssueBeingProcessedOrQueued("issue-1").Should().BeFalse();
    }

    [Fact]
    public void IsIssueBeingProcessedOrQueued_Queued_ReturnsTrue()
    {
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "issue-1",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "test"
        });

        var dispatcher = CreateDispatcher();
        dispatcher.IsIssueBeingProcessedOrQueued("issue-1").Should().BeTrue();
    }

    [Fact]
    public void IsIssueBeingProcessedOrQueued_BeingProcessed_ReturnsTrue()
    {
        _runService.AddRun(new PipelineRun
        {
            RunId = "run-1",
            IssueIdentifier = "issue-1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow
        });

        var dispatcher = CreateDispatcher();
        dispatcher.IsIssueBeingProcessedOrQueued("issue-1").Should().BeTrue();
    }

    [Fact]
    public void IsIssueBeingProcessedOrQueued_NullIdentifier_Throws()
    {
        var dispatcher = CreateDispatcher();
        var act = () => dispatcher.IsIssueBeingProcessedOrQueued(null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public async Task TryDispatchAsync_AlreadyQueued_ReturnsFalse()
    {
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "issue-1",
            IssueProviderId = "ip",
            RepoProviderId = "rp",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "test"
        });

        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.TryDispatchAsync(
            "issue-1", "ip", "rp", null, null, "test", CancellationToken.None);

        result.Should().BeFalse();
    }

    [Fact]
    public async Task TryDispatchAsync_NoIdleAgent_EnqueuesJob()
    {
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        var dispatcher = CreateDispatcher();
        var result = await dispatcher.TryDispatchAsync(
            "issue-new", "ip", "rp", null, null, "user", CancellationToken.None);

        result.Should().BeTrue(); // Enqueued successfully
        _dispatcher.IsIssueQueued("issue-new").Should().BeTrue();
    }

    [Fact]
    public async Task TryDispatchAsync_NullIssueIdentifier_Throws()
    {
        var dispatcher = CreateDispatcher();
        var act = () => dispatcher.TryDispatchAsync(
            null!, "ip", "rp", null, null, "test", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task TryDispatchAsync_NullInitiatedBy_Throws()
    {
        var dispatcher = CreateDispatcher();
        var act = () => dispatcher.TryDispatchAsync(
            "issue-1", "ip", "rp", null, null, null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}
