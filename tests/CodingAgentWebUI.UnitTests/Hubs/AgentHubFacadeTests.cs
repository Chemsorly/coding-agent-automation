using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>Unit tests for <see cref="AgentHubFacade"/>.</summary>
public sealed class AgentHubFacadeTests
{
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly AgentRegistryService _registry;
    private readonly OrchestratorRunService _runService;
    private readonly JobDeduplicationGuardService _dispatcher;
    private readonly Mock<IPipelineRunHistoryService> _mockHistory = new();
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();
    private readonly Mock<IProviderFactory> _mockProviderFactory = new();
    private readonly AgentHubFacade _facade;
    private readonly ILogger<AgentHubFacadeDependencies> _facadeLogger = NullLogger<AgentHubFacadeDependencies>.Instance;

    public AgentHubFacadeTests()
    {
        _registry = new AgentRegistryService(_mockLogger.Object);
        _runService = new OrchestratorRunService(_mockLogger.Object);
        _dispatcher = new JobDeduplicationGuardService(_registry, _mockLogger.Object);

        _facade = new AgentHubFacade(new AgentHubFacadeDependencies(
            _registry,
            _runService,
            _dispatcher,
            _mockHistory.Object,
            _mockConfigStore.Object,
            _mockProviderFactory.Object,
            _facadeLogger));
    }

    #region Constructor null guards

    [Fact]
    public void Ctor_NullRegistry_Throws()
    {
        var act = () => new AgentHubFacade(new AgentHubFacadeDependencies(null!, _runService, _dispatcher, _mockHistory.Object, _mockConfigStore.Object, _mockProviderFactory.Object, _facadeLogger));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullRunService_Throws()
    {
        var act = () => new AgentHubFacade(new AgentHubFacadeDependencies(_registry, null!, _dispatcher, _mockHistory.Object, _mockConfigStore.Object, _mockProviderFactory.Object, _facadeLogger));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullDispatcher_Throws()
    {
        var act = () => new AgentHubFacade(new AgentHubFacadeDependencies(_registry, _runService, null!, _mockHistory.Object, _mockConfigStore.Object, _mockProviderFactory.Object, _facadeLogger));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullHistoryService_Throws()
    {
        var act = () => new AgentHubFacade(new AgentHubFacadeDependencies(_registry, _runService, _dispatcher, null!, _mockConfigStore.Object, _mockProviderFactory.Object, _facadeLogger));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullConfigStore_Throws()
    {
        var act = () => new AgentHubFacade(new AgentHubFacadeDependencies(_registry, _runService, _dispatcher, _mockHistory.Object, null!, _mockProviderFactory.Object, _facadeLogger));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullProviderFactory_Throws()
    {
        var act = () => new AgentHubFacade(new AgentHubFacadeDependencies(_registry, _runService, _dispatcher, _mockHistory.Object, _mockConfigStore.Object, null!, _facadeLogger));
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region Registry delegation

    [Fact]
    public void Register_DelegatesToRegistry()
    {
        var msg = new AgentRegistrationMessage { AgentId = "a1", Hostname = "h1", Labels = new[] { "dotnet" } };
        var result = _facade.Register(msg, "conn-1");
        result.Should().NotBeNull();
        result.AgentId.Value.Should().Be("a1");
    }

    [Fact]
    public void Deregister_DelegatesToRegistry()
    {
        var msg = new AgentRegistrationMessage { AgentId = "a1", Hostname = "h1", Labels = Array.Empty<string>() };
        _facade.Register(msg, "conn-1");
        _facade.Deregister("a1").Should().BeTrue();
    }

    [Fact]
    public void GetByAgentId_DelegatesToRegistry()
    {
        var msg = new AgentRegistrationMessage { AgentId = "a1", Hostname = "h1", Labels = Array.Empty<string>() };
        _facade.Register(msg, "conn-1");
        _facade.GetByAgentId("a1").Should().NotBeNull();
        _facade.GetByAgentId("unknown").Should().BeNull();
    }

    [Fact]
    public void GetByConnectionId_DelegatesToRegistry()
    {
        var msg = new AgentRegistrationMessage { AgentId = "a1", Hostname = "h1", Labels = Array.Empty<string>() };
        _facade.Register(msg, "conn-1");
        _facade.GetByConnectionId("conn-1").Should().NotBeNull();
        _facade.GetByConnectionId("unknown").Should().BeNull();
    }

    [Fact]
    public void TransitionStatus_DelegatesToRegistry()
    {
        var msg = new AgentRegistrationMessage { AgentId = "a1", Hostname = "h1", Labels = Array.Empty<string>() };
        _facade.Register(msg, "conn-1");
        _facade.TransitionStatus("a1", AgentStatus.Busy);
        _facade.GetByAgentId("a1")!.Status.Should().Be(AgentStatus.Busy);
    }

    [Fact]
    public void UpdateHeartbeat_DelegatesToRegistry()
    {
        var msg = new AgentRegistrationMessage { AgentId = "a1", Hostname = "h1", Labels = Array.Empty<string>() };
        _facade.Register(msg, "conn-1");
        var ts = DateTimeOffset.UtcNow;
        _facade.UpdateHeartbeat("a1", ts);
        _facade.GetByAgentId("a1")!.LastHeartbeatAt.Should().Be(ts);
    }

    #endregion

    #region Run state delegation

    [Fact]
    public void GetRun_DelegatesToRunService()
    {
        _facade.GetRun("nonexistent").Should().BeNull();
    }

    [Fact]
    public void GetOutputBuffer_DelegatesToRunService()
    {
        var buffer = _facade.GetOutputBuffer("job-1");
        buffer.Should().NotBeNull();
    }

    [Fact]
    public void RemoveRun_DelegatesToRunService()
    {
        // Should not throw even if run doesn't exist — and run lookup returns null afterwards
        _facade.RemoveRun("nonexistent");
        _facade.GetRun("nonexistent").Should().BeNull();
    }

    #endregion

    #region Dispatch delegation

    [Fact]
    public void MarkIssueComplete_DelegatesToDispatcher()
    {
        // First enqueue the issue so we can verify it gets cleared by MarkIssueComplete
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "org/repo#1",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "test"
        });
        _dispatcher.IsIssueQueued("org/repo#1").Should().BeTrue("issue should be queued before MarkIssueComplete");

        _facade.MarkIssueComplete("org/repo#1", "ip-1");

        _dispatcher.IsIssueQueued("org/repo#1").Should().BeFalse("MarkIssueComplete should clear the dedup entry");
    }

    [Fact]
    public void Signal_IsNoOp_AfterSpec044()
    {
        // Signal() is a no-op after Spec 044 — JobQueueDrainService was removed.
        // Verify it does not throw and does not mutate the queue.
        _dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "org/repo#99",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "test"
        });
        var queueLengthBefore = _dispatcher.QueueLength;

        _facade.Signal();

        _dispatcher.QueueLength.Should().Be(queueLengthBefore, "Signal() must not modify the queue itself");
    }

    #endregion

    #region History delegation

    [Fact]
    public async Task AddRunToHistoryAsync_DelegatesToHistoryService()
    {
        var run = new PipelineRun
        {
            RunId = "r1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1"
        };

        await _facade.AddRunToHistoryAsync(run);

        _mockHistory.Verify(h => h.AddRunToHistoryAsync(run, It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion

    #region Issue provider delegation

    [Fact]
    public async Task LoadProviderConfigsAsync_DelegatesToConfigStore()
    {
        var configs = new List<ProviderConfig> { new() { Id = "c1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" } };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(configs);

        var result = await _facade.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        result.Should().BeSameAs(configs);
    }

    [Fact]
    public void CreateIssueProvider_DelegatesToProviderFactory()
    {
        var config = new ProviderConfig { Id = "c1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" };
        var mockProvider = Mock.Of<IIssueProvider>();
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(config)).Returns(mockProvider);

        var result = _facade.CreateIssueProvider(config);
        result.Should().BeSameAs(mockProvider);
    }

    [Fact]
    public async Task LoadTemplatesForProjectAsync_DelegatesToProjectStore()
    {
        // Construct a facade with an injected IProjectStore mock via the new ProjectStore parameter
        var mockProjectStore = new Mock<IProjectStore>();
        var templates = new List<PipelineJobTemplate>
        {
            new() { Id = "tmpl-1", Name = "Template 1", IssueProviderId = "ip-1", RepoProviderId = "rp-1" }
        };
        mockProjectStore
            .Setup(s => s.LoadTemplatesForProjectAsync("proj-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);

        var facadeWithProjectStore = new AgentHubFacade(new AgentHubFacadeDependencies(
            _registry,
            _runService,
            _dispatcher,
            _mockHistory.Object,
            _mockConfigStore.Object,
            _mockProviderFactory.Object,
            _facadeLogger,
            ProjectStore: mockProjectStore.Object));

        var result = await facadeWithProjectStore.LoadTemplatesForProjectAsync("proj-1", CancellationToken.None);

        result.Should().BeSameAs(templates);
        mockProjectStore.Verify(s => s.LoadTemplatesForProjectAsync("proj-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task LoadTemplatesForProjectAsync_NullProjectStore_ReturnsEmptyList()
    {
        // Facade constructed without ProjectStore (null) — returns empty list rather than throwing
        var result = await _facade.LoadTemplatesForProjectAsync("proj-1", CancellationToken.None);

        result.Should().BeEmpty();
    }

    #endregion
}
