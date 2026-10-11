using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Registry;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Hubs;

/// <summary>Unit tests for <see cref="AgentHubFacade"/>.</summary>
public sealed class AgentHubFacadeTests
{
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly AgentRegistryService _registry;
    private readonly OrchestratorRunService _runService;
    private readonly Mock<IPipelineRunHistoryService> _mockHistory = new();
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();
    private readonly Mock<IProviderFactory> _mockProviderFactory = new();
    private readonly AgentHubFacade _facade;
    private readonly ILogger<AgentHubFacadeDependencies> _facadeLogger = NullLogger<AgentHubFacadeDependencies>.Instance;

    public AgentHubFacadeTests()
    {
        _registry = new AgentRegistryService(_mockLogger.Object);
        _runService = new OrchestratorRunService(_mockLogger.Object);

        _facade = new AgentHubFacade(new AgentHubFacadeDependencies(
            _registry,
            _runService,
            _mockHistory.Object,
            _mockConfigStore.Object,
            _mockProviderFactory.Object,
            _facadeLogger));
    }

    #region Constructor null guards

    [Fact]
    public void Ctor_NullRegistry_Throws()
    {
        var act = () => new AgentHubFacade(new AgentHubFacadeDependencies(null!, _runService, _mockHistory.Object, _mockConfigStore.Object, _mockProviderFactory.Object, _facadeLogger));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullRunService_Throws()
    {
        var act = () => new AgentHubFacade(new AgentHubFacadeDependencies(_registry, null!, _mockHistory.Object, _mockConfigStore.Object, _mockProviderFactory.Object, _facadeLogger));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullHistoryService_Throws()
    {
        var act = () => new AgentHubFacade(new AgentHubFacadeDependencies(_registry, _runService, null!, _mockConfigStore.Object, _mockProviderFactory.Object, _facadeLogger));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullConfigStore_Throws()
    {
        var act = () => new AgentHubFacade(new AgentHubFacadeDependencies(_registry, _runService, _mockHistory.Object, null!, _mockProviderFactory.Object, _facadeLogger));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullProviderFactory_Throws()
    {
        var act = () => new AgentHubFacade(new AgentHubFacadeDependencies(_registry, _runService, _mockHistory.Object, _mockConfigStore.Object, null!, _facadeLogger));
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

    [Fact]
    public void SetLocalAgentSnapshotField_DelegatesToRegistry()
    {
        // Arrange: register an agent so the registry has a live entry.
        var msg = new AgentRegistrationMessage { AgentId = "a1", Hostname = "h1", Labels = Array.Empty<string>() };
        _facade.Register(msg, "conn-1");

        // Act: the in-memory AgentRegistryService.SetLocalSnapshotField is a no-op (the live
        // AgentEntry reference is already returned by GetByConnectionId). The test verifies the
        // facade method delegates through without throwing — covering line 288 of AgentHubFacade.
        var act = () => _facade.SetLocalAgentSnapshotField(new AgentId("a1"), "activeJobId", "run-facade-test");
        act.Should().NotThrow(
            "SetLocalAgentSnapshotField must delegate to IAgentRegistryService.SetLocalSnapshotField " +
            "without throwing for a registered agent (issue #2616)");
    }

    #endregion

    #region Run state delegation

    [Fact]
    public void GetRun_DelegatesToRunService()
    {
        _facade.GetRun("nonexistent").Should().BeNull();
    }

    [Fact]
    public async Task GetOutputBacklogAsync_ReturnsLinesAppendedToRunService()
    {
        // Arrange: add a run and append lines via the run service (in-memory path)
        var run = new PipelineRun
        {
            RunId = "job-backlog-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "ip",
            RepoProviderConfigId = "rp"
        };
        _facade.AddRun(run);
        _facade.AppendOutputLines("job-backlog-1", ["line-a", "line-b"]);

        // Act: read through the facade
        var backlog = await _facade.GetOutputBacklogAsync("job-backlog-1");

        // Assert
        backlog.Should().ContainInOrder("line-a", "line-b");
        backlog.Should().HaveCount(2, "GetOutputBacklogAsync must return lines written via AppendOutputLines");
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

    // MarkIssueComplete was removed from IAgentHubFacade and JobDeduplicationGuardService
    // in T18 (arch-audit 2026-08-22) — the backing collections had no writers.

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

    #region ResolveBrainReadOnlyAsync

    private AgentHubFacade CreateFacadeWithPipelineStore(
        IPipelineConfigStore? pipelineConfigStore,
        IProjectStore? projectStore = null)
    {
        return new AgentHubFacade(new AgentHubFacadeDependencies(
            _registry,
            _runService,
            _mockHistory.Object,
            _mockConfigStore.Object,
            _mockProviderFactory.Object,
            _facadeLogger,
            ProjectStore: projectStore,
            PipelineConfigStore: pipelineConfigStore));
    }

    /// <summary>
    /// When the in-memory run has a valid repo provider ID and the resolved pipeline config
    /// has BrainReadOnly = true, ResolveBrainReadOnlyAsync must return true.
    /// </summary>
    [Fact]
    public async Task ResolveBrainReadOnlyAsync_JobWithRun_BrainReadOnlyTrue_ReturnsTrue()
    {
        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "issue-1",
            RepoProviderConfigId = "repo-1"
        };
        _runService.AddRun(run);

        var mockPipelineStore = new Mock<IPipelineConfigStore>();
        mockPipelineStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { BrainReadOnly = true });

        var mockProjectStore = new Mock<IProjectStore>();
        mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>());

        var facade = CreateFacadeWithPipelineStore(mockPipelineStore.Object, mockProjectStore.Object);

        var result = await facade.ResolveBrainReadOnlyAsync(new JobId("job-1"), CancellationToken.None);

        result.Should().BeTrue();
    }

    /// <summary>
    /// When BrainReadOnly is false in the pipeline config, ResolveBrainReadOnlyAsync returns false.
    /// </summary>
    [Fact]
    public async Task ResolveBrainReadOnlyAsync_JobWithRun_BrainReadOnlyFalse_ReturnsFalse()
    {
        var run = new PipelineRun
        {
            RunId = "job-rw",
            IssueIdentifier = "org/repo#2",
            IssueTitle = "Test",
            IssueProviderConfigId = "issue-1",
            RepoProviderConfigId = "repo-1"
        };
        _runService.AddRun(run);

        var mockPipelineStore = new Mock<IPipelineConfigStore>();
        mockPipelineStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { BrainReadOnly = false });

        var mockProjectStore = new Mock<IProjectStore>();
        mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>());

        var facade = CreateFacadeWithPipelineStore(mockPipelineStore.Object, mockProjectStore.Object);

        var result = await facade.ResolveBrainReadOnlyAsync(new JobId("job-rw"), CancellationToken.None);

        result.Should().BeFalse();
    }

    /// <summary>
    /// When IPipelineConfigStore is null (not wired), ResolveBrainReadOnlyAsync fails closed and returns true.
    /// </summary>
    [Fact]
    public async Task ResolveBrainReadOnlyAsync_NoPipelineConfigStore_FailsClosedReturnsTrue()
    {
        var run = new PipelineRun
        {
            RunId = "job-nc",
            IssueIdentifier = "org/repo#3",
            IssueTitle = "Test",
            IssueProviderConfigId = "issue-1",
            RepoProviderConfigId = "repo-1"
        };
        _runService.AddRun(run);

        // Facade without PipelineConfigStore — should fail closed
        var result = await _facade.ResolveBrainReadOnlyAsync(new JobId("job-nc"), CancellationToken.None);

        result.Should().BeTrue("missing PipelineConfigStore must fail closed to prevent write access");
    }

    /// <summary>
    /// When no run exists and no WorkItem store is configured, ResolveBrainReadOnlyAsync fails closed.
    /// </summary>
    [Fact]
    public async Task ResolveBrainReadOnlyAsync_NoRun_FailsClosedReturnsTrue()
    {
        var mockPipelineStore = new Mock<IPipelineConfigStore>();
        mockPipelineStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { BrainReadOnly = false });

        var facade = CreateFacadeWithPipelineStore(mockPipelineStore.Object);

        // Job "unknown-job" has no in-memory run and no TransitionStore
        var result = await facade.ResolveBrainReadOnlyAsync(new JobId("unknown-job"), CancellationToken.None);

        result.Should().BeTrue("no run + no WorkItem store must fail closed");
    }

    /// <summary>
    /// When template has BrainReadOnly = true (template override), resolution returns true
    /// even when global config is false.
    /// </summary>
    [Fact]
    public async Task ResolveBrainReadOnlyAsync_TemplateOverride_BrainReadOnlyTrue_ReturnsTrue()
    {
        var run = new PipelineRun
        {
            RunId = "job-tmpl",
            IssueIdentifier = "org/repo#4",
            IssueTitle = "Test",
            IssueProviderConfigId = "issue-1",
            RepoProviderConfigId = "repo-tmpl"
        };
        _runService.AddRun(run);

        var mockPipelineStore = new Mock<IPipelineConfigStore>();
        // Global: BrainReadOnly = false
        mockPipelineStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { BrainReadOnly = false });

        var mockProjectStore = new Mock<IProjectStore>();
        // Template for repo-tmpl has BrainReadOnly = true — overrides global false
        mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "t-tmpl", Name = "Template", IssueProviderId = "issue-1", RepoProviderId = "repo-tmpl", BrainReadOnly = true, Enabled = true }
            });

        var facade = CreateFacadeWithPipelineStore(mockPipelineStore.Object, mockProjectStore.Object);

        var result = await facade.ResolveBrainReadOnlyAsync(new JobId("job-tmpl"), CancellationToken.None);

        result.Should().BeTrue("template-level BrainReadOnly = true must override global false");
    }

    // TODO [WARNING]: missing test for project-level BrainReadOnly override in
    // ResolveBrainReadOnlyAsync. The issue spec requires: "With BrainReadOnly true from …
    // a project override … the brain config is vended read-only." The five existing tests cover
    // global setting, template override, null store, no-run, and null store failure, but
    // PipelineProject.BrainReadOnly = true (project-level override) is not exercised.
    // A misconfiguration in ApplyProjectOverrides for BrainReadOnly would not be caught
    // (issue #3573, TestQualityReviewer finding).

    // TODO [WARNING]: missing tests for the K8s fallback path in ResolveBrainReadOnlyAsync
    // (the _transitionStore.GetWorkItemProviderConfigIdsAsync branch). The existing
    // ResolveBrainReadOnlyAsync_NoRun_FailsClosedReturnsTrue test covers the case where both
    // run and transition store are absent, but the path where the transition store IS present
    // and returns a repoProviderId is untested. This is the production path for K8s-mode agents
    // (issue #3573, TestQualityReviewer finding).

    #endregion
}
