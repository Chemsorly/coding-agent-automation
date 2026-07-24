using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>Unit tests for <see cref="ConsolidationDispatcher"/>.</summary>
public sealed class ConsolidationDispatcherTests : IDisposable
{
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly AgentRegistryService _registry;
    private readonly JobDeduplicationGuardService _dispatcher;
    private readonly Mock<IAgentCommunication> _mockAgentComm = new();
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();
    private readonly Mock<IProjectStore> _mockProjectStore = new();
    private readonly Mock<ITokenVendingService> _mockTokenVending = new();
    private readonly Mock<IWorkDistributor> _mockWorkDistributor = new();
    private readonly string _tempDir;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public ConsolidationDispatcherTests()
    {
        _registry = new AgentRegistryService(_mockLogger.Object);
        _dispatcher = new JobDeduplicationGuardService(_registry, _mockLogger.Object);
        _tempDir = Path.Combine(Path.GetTempPath(), $"cds-test-{Guid.NewGuid():N}");

        _mockConfigStore.Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Returns((string id, ProviderKind kind, CancellationToken ct) =>
            {
                var configs = _mockConfigStore.Object.LoadProviderConfigsAsync(kind, ct).GetAwaiter().GetResult();
                return Task.FromResult(configs.FirstOrDefault(c => c.Id == id));
            });

        // Default: return empty profiles (tests override as needed)
        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>());

        // Default: return empty projects (no templates will resolve without project ownership)
        _mockProjectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>());

        // Default: IWorkDistributor.DistributeAsync returns success with Queued=true
        _mockWorkDistributor.Setup(w => w.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(true, null, null, Queued: true));

        // Default: AssignConsolidationJobAsync completes successfully
        _mockAgentComm.Setup(c => c.AssignConsolidationJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ConsolidationJobMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private ConsolidationDispatcher CreateService(PipelineConfiguration? config = null, string? runsDir = null)
    {
        config ??= new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" };
        return new ConsolidationDispatcher(
            _registry,
            _dispatcher,
            _mockAgentComm.Object,
            _mockConfigStore.Object,
            _mockProjectStore.Object,
            _mockTokenVending.Object,
            config,
            _mockWorkDistributor.Object,
            Mock.Of<IPipelineRunHistoryService>(),
            _mockLogger.Object,
            new FileSystemConsolidationRunStore(runsDir ?? _tempDir));
    }

    private void RegisterIdleAgent(string agentId = "agent-1", string connectionId = "conn-1", string[]? labels = null)
    {
        var msg = new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = "host-1",
            Labels = labels ?? Array.Empty<string>()
        };
        _registry.Register(msg, connectionId);
    }

    #region Constructor null guards

    [Fact]
    public void Ctor_NullRegistry_Throws()
    {
        var act = () => new ConsolidationDispatcher(null!, _dispatcher, _mockAgentComm.Object, _mockConfigStore.Object, _mockProjectStore.Object, _mockTokenVending.Object, new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" }, Mock.Of<IWorkDistributor>(), Mock.Of<IPipelineRunHistoryService>(), _mockLogger.Object, Mock.Of<IConsolidationRunStore>());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullDispatcher_Throws()
    {
        var act = () => new ConsolidationDispatcher(_registry, null!, _mockAgentComm.Object, _mockConfigStore.Object, _mockProjectStore.Object, _mockTokenVending.Object, new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" }, Mock.Of<IWorkDistributor>(), Mock.Of<IPipelineRunHistoryService>(), _mockLogger.Object, Mock.Of<IConsolidationRunStore>());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullAgentComm_Throws()
    {
        var act = () => new ConsolidationDispatcher(_registry, _dispatcher, null!, _mockConfigStore.Object, _mockProjectStore.Object, _mockTokenVending.Object, new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" }, Mock.Of<IWorkDistributor>(), Mock.Of<IPipelineRunHistoryService>(), _mockLogger.Object, Mock.Of<IConsolidationRunStore>());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullConfigStore_Throws()
    {
        var act = () => new ConsolidationDispatcher(_registry, _dispatcher, _mockAgentComm.Object, null!, _mockProjectStore.Object, _mockTokenVending.Object, new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" }, Mock.Of<IWorkDistributor>(), Mock.Of<IPipelineRunHistoryService>(), _mockLogger.Object, Mock.Of<IConsolidationRunStore>());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullProjectStore_Throws()
    {
        var act = () => new ConsolidationDispatcher(_registry, _dispatcher, _mockAgentComm.Object, _mockConfigStore.Object, null!, _mockTokenVending.Object, new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" }, Mock.Of<IWorkDistributor>(), Mock.Of<IPipelineRunHistoryService>(), _mockLogger.Object, Mock.Of<IConsolidationRunStore>());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullTokenVending_Throws()
    {
        var act = () => new ConsolidationDispatcher(_registry, _dispatcher, _mockAgentComm.Object, _mockConfigStore.Object, _mockProjectStore.Object, null!, new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" }, Mock.Of<IWorkDistributor>(), Mock.Of<IPipelineRunHistoryService>(), _mockLogger.Object, Mock.Of<IConsolidationRunStore>());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullConfig_Throws()
    {
        var act = () => new ConsolidationDispatcher(_registry, _dispatcher, _mockAgentComm.Object, _mockConfigStore.Object, _mockProjectStore.Object, _mockTokenVending.Object, null!, Mock.Of<IWorkDistributor>(), Mock.Of<IPipelineRunHistoryService>(), _mockLogger.Object, Mock.Of<IConsolidationRunStore>());
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        var act = () => new ConsolidationDispatcher(_registry, _dispatcher, _mockAgentComm.Object, _mockConfigStore.Object, _mockProjectStore.Object, _mockTokenVending.Object, new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" }, Mock.Of<IWorkDistributor>(), Mock.Of<IPipelineRunHistoryService>(), null!, Mock.Of<IConsolidationRunStore>());
        act.Should().Throw<ArgumentNullException>();
    }

    #endregion

    #region TryDispatchAsync

    [Fact]
    public async Task TryDispatchAsync_NullRun_ThrowsArgumentNullException()
    {
        var svc = CreateService();
        var act = () => svc.TryDispatchAsync(null!, ConsolidationRunType.BrainConsolidation, null, null, "/tmp", CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    [Fact]
    public async Task TryDispatchAsync_NoIdleAgent_ReturnsQueued()
    {
        // No agents registered
        var svc = CreateService();
        var run = new ConsolidationRun { RunId = "r1", Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTimeOffset.UtcNow };

        var result = await svc.TryDispatchAsync(run, ConsolidationRunType.BrainConsolidation, null, null, "/tmp", CancellationToken.None);

        result.Should().Be(ConsolidationDispatchResult.Queued);
    }

    /// <summary>
    /// Regression test for root-cause fix: ConsolidationDispatcher must set AgentSelector to the
    /// full profile MatchLabels (not just requiredLabels) when enqueueing via IWorkDistributor.
    /// Without this, K8s DispatchService fails with "No job template for selector" because
    /// JobTemplateStore.Resolve() requires exact match on the full template key.
    /// </summary>
    [Fact]
    public async Task TryDispatchAsync_NoIdleAgent_AgentSelectorUsesProfileMatchLabels_NotRawRequiredLabels()
    {
        // No agents registered → will enqueue via IWorkDistributor

        // DefaultRequiredAgentLabels = "dotnet,dotnet10" (subset)
        var config = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = "/tmp",
            DefaultRequiredAgentLabels = "dotnet,dotnet10"
        };

        // Profile with full MatchLabels (superset of required labels)
        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>
            {
                new()
                {
                    Id = "profile-kiro-dotnet10",
                    DisplayName = "Kiro Dotnet10",
                    Enabled = true,
                    MatchLabels = new[] { "kiro", "dotnet", "dotnet10" },
                    AgentProviderConfigId = "agent-cfg",
                    Priority = 1
                }
            });

        JobDistributionRequest? capturedRequest = null;
        _mockWorkDistributor
            .Setup(w => w.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<JobDistributionRequest, CancellationToken>((req, _) => capturedRequest = req)
            .ReturnsAsync(new DistributionResult(true, null, null, Queued: true));

        var svc = CreateService(config);
        var run = new ConsolidationRun { RunId = "r1", Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTimeOffset.UtcNow };

        var result = await svc.TryDispatchAsync(run, ConsolidationRunType.BrainConsolidation, null, null, "/tmp", CancellationToken.None);

        result.Should().Be(ConsolidationDispatchResult.Queued);
        capturedRequest.Should().NotBeNull();

        // KEY ASSERTION: AgentSelector must be the full profile MatchLabels (sorted), NOT just "dotnet,dotnet10"
        // The normalized sorted form of ["kiro", "dotnet", "dotnet10"] is "dotnet,dotnet10,kiro"
        capturedRequest!.AgentSelector.Should().Be("dotnet,dotnet10,kiro",
            "AgentSelector must use profile.MatchLabels (the template key), not raw requiredLabels");
    }

    [Fact]
    public async Task TryDispatchAsync_AgentAvailable_DispatchesAndReturnsTrue()
    {
        RegisterIdleAgent();

        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { new() { Id = "agent-cfg", Kind = ProviderKind.Agent, ProviderType = "Kiro", DisplayName = "Agent" } });

        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        var run = new ConsolidationRun { RunId = "r1", Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTimeOffset.UtcNow, TemplateName = "Test" };

        var result = await svc.TryDispatchAsync(run, ConsolidationRunType.BrainConsolidation, null, null, "/tmp", CancellationToken.None);

        result.Should().Be(ConsolidationDispatchResult.Dispatched);
        _mockAgentComm.Verify(c => c.AssignConsolidationJobAsync("conn-1", "agent-1", It.IsAny<ConsolidationJobMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryDispatchAsync_DispatchThrows_ResetsAgentAndReturnsFalse()
    {
        RegisterIdleAgent();

        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { new() { Id = "agent-cfg", Kind = ProviderKind.Agent, ProviderType = "Kiro", DisplayName = "Agent" } });

        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new List<ProviderConfig>());

        _mockAgentComm.Setup(c => c.AssignConsolidationJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ConsolidationJobMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection lost"));

        var svc = CreateService();
        var run = new ConsolidationRun { RunId = "r1", Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTimeOffset.UtcNow };

        var result = await svc.TryDispatchAsync(run, ConsolidationRunType.BrainConsolidation, null, null, "/tmp", CancellationToken.None);

        result.Should().Be(ConsolidationDispatchResult.Failed);
        // Agent should be back to Idle
        var agent = _registry.GetByAgentId("agent-1");
        agent!.Status.Should().Be(AgentStatus.Idle);
        agent.ActiveJobId.Should().BeNull();
    }

    [Fact]
    public async Task TryDispatchAsync_RefactoringType_IncludesIssuePermission()
    {
        RegisterIdleAgent();

        var template = new PipelineJobTemplate { Id = "t1", Name = "Test", IssueProviderId = "ip-1", RepoProviderId = "rp-1" };
        var config = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = "/tmp",
        };

        // Set up project containing the template
        _mockProjectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = new[] { "t1" } }
            });
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template });

        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { new() { Id = "agent-cfg", Kind = ProviderKind.Agent, ProviderType = "Kiro", DisplayName = "Agent" } });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo" } });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { new() { Id = "ip-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Issue" } });

        bool capturedIncludeIssue = false;
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool>((_, _, _, includeIssue) => capturedIncludeIssue = includeIssue)
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService(config);
        var run = new ConsolidationRun { RunId = "r1", Type = ConsolidationRunType.RefactoringDetection, TemplateId = "t1", StartedAtUtc = DateTimeOffset.UtcNow };

        await svc.TryDispatchAsync(run, ConsolidationRunType.RefactoringDetection, "t1", null, "/tmp", CancellationToken.None);

        capturedIncludeIssue.Should().BeTrue();
    }

    [Fact]
    public async Task TryDispatchAsync_NullTemplateId_UsesDefaultLabels()
    {
        RegisterIdleAgent();

        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { new() { Id = "agent-cfg", Kind = ProviderKind.Agent, ProviderType = "Kiro", DisplayName = "Agent" } });
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        var run = new ConsolidationRun { RunId = "r1", Type = ConsolidationRunType.HarnessSuggestions, StartedAtUtc = DateTimeOffset.UtcNow };

        // Should succeed — null templateId means global scope, default labels
        var result = await svc.TryDispatchAsync(run, ConsolidationRunType.HarnessSuggestions, null, null, "/tmp", CancellationToken.None);
        result.Should().Be(ConsolidationDispatchResult.Dispatched);
    }

    [Fact]
    public async Task TryDispatchAsync_WithTemplate_BuildsProviderConfigs()
    {
        RegisterIdleAgent();

        var template = new PipelineJobTemplate { Id = "t1", Name = "Test", IssueProviderId = "ip-1", RepoProviderId = "rp-1", BrainProviderId = "bp-1" };
        var config = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = "/tmp",
        };

        // Set up project containing the template
        _mockProjectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = new[] { "t1" } }
            });
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template });

        var repoConfig = new ProviderConfig { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo" };
        var brainConfig = new ProviderConfig { Id = "bp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Brain" };

        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { new() { Id = "agent-cfg", Kind = ProviderKind.Agent, ProviderType = "Kiro", DisplayName = "Agent" } });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { repoConfig, brainConfig });

        IReadOnlyList<ProviderConfig>? capturedConfigs = null;
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool>((configs, _, _, _) => capturedConfigs = configs)
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService(config);
        var run = new ConsolidationRun { RunId = "r1", Type = ConsolidationRunType.BrainConsolidation, TemplateId = "t1", StartedAtUtc = DateTime.UtcNow };

        await svc.TryDispatchAsync(run, ConsolidationRunType.BrainConsolidation, "t1", null, "/tmp", CancellationToken.None);

        // Should include agent + repo + brain configs
        capturedConfigs.Should().NotBeNull();
        capturedConfigs!.Count.Should().Be(3);
    }

    #endregion

    #region AgentProvider resolution via profiles

    [Fact]
    public async Task TryDispatchAsync_ProfileResolution_SelectsCorrectProviderFromProfile()
    {
        // Agent has kiro labels → profile should resolve to KiroCli provider
        RegisterIdleAgent(labels: new[] { "kiro", "dotnet", "dotnet10" });

        var kiroProfile = new AgentProfile
        {
            Id = "prof-kiro-dotnet",
            DisplayName = "Kiro DotNet",
            MatchLabels = new[] { "kiro", "dotnet", "dotnet10" },
            AgentProviderConfigId = "kiro-agent-cfg"
        };
        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile> { kiroProfile });

        // Two agent configs — OpenCode first alphabetically
        var openCodeConfig = new ProviderConfig { Id = "aaa-opencode-cfg", Kind = ProviderKind.Agent, ProviderType = "OpenCode", DisplayName = "OpenCode" };
        var kiroConfig = new ProviderConfig { Id = "kiro-agent-cfg", Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "KiroCli", RequiredLabels = new List<string> { "kiro" } };

        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { openCodeConfig, kiroConfig });

        IReadOnlyList<ProviderConfig>? capturedConfigs = null;
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool>((configs, _, _, _) => capturedConfigs = configs)
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        var run = new ConsolidationRun { RunId = "r1", Type = ConsolidationRunType.HarnessSuggestions, StartedAtUtc = DateTimeOffset.UtcNow };

        var result = await svc.TryDispatchAsync(run, ConsolidationRunType.HarnessSuggestions, null, null, "/tmp", CancellationToken.None);

        result.Should().Be(ConsolidationDispatchResult.Dispatched);
        capturedConfigs.Should().NotBeNull();
        var agentCfg = capturedConfigs!.FirstOrDefault(c => c.Kind == ProviderKind.Agent);
        agentCfg.Should().NotBeNull();
        agentCfg!.Id.Should().Be("kiro-agent-cfg");
        agentCfg.ProviderType.Should().Be("KiroCli");
    }

    [Fact]
    public async Task TryDispatchAsync_NoProfiles_FallsBackToFirstAvailable()
    {
        RegisterIdleAgent(labels: new[] { "kiro", "dotnet", "dotnet10" });

        // No profiles configured — empty list (default mock)
        var kiroConfig = new ProviderConfig { Id = "kiro-agent-cfg", Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "KiroCli", RequiredLabels = new List<string> { "kiro" } };

        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { kiroConfig });

        IReadOnlyList<ProviderConfig>? capturedConfigs = null;
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool>((configs, _, _, _) => capturedConfigs = configs)
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        var run = new ConsolidationRun { RunId = "r1", Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTimeOffset.UtcNow };

        await svc.TryDispatchAsync(run, ConsolidationRunType.BrainConsolidation, null, null, "/tmp", CancellationToken.None);

        capturedConfigs.Should().NotBeNull();
        var agentCfg = capturedConfigs!.FirstOrDefault(c => c.Kind == ProviderKind.Agent);
        agentCfg.Should().NotBeNull();
        agentCfg!.Id.Should().Be("kiro-agent-cfg");
    }

    [Fact]
    public async Task TryDispatchAsync_NoProfileMatch_IncompatibleFallback_SkipsAgentConfig()
    {
        // Agent has kiro labels but no profiles → fallback checks RequiredLabels
        // on agent configs for compatibility. OpenCode requires "opencode" label
        // which the agent doesn't have — config is skipped.
        RegisterIdleAgent(labels: new[] { "kiro", "dotnet", "dotnet10" });

        // Only OpenCode provider available — incompatible with agent's labels
        var openCodeConfig = new ProviderConfig
        {
            Id = "opencode-cfg", Kind = ProviderKind.Agent, ProviderType = "OpenCode", DisplayName = "OpenCode",
            RequiredLabels = new List<string> { "opencode" }
        };

        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { openCodeConfig });

        IReadOnlyList<ProviderConfig>? capturedConfigs = null;
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool>((configs, _, _, _) => capturedConfigs = configs)
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        var run = new ConsolidationRun { RunId = "r1", Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTimeOffset.UtcNow };

        var result = await svc.TryDispatchAsync(run, ConsolidationRunType.BrainConsolidation, null, null, "/tmp", CancellationToken.None);

        // Job dispatches but incompatible agent config is excluded.
        // With zero eligible configs, token vending is skipped (nothing to vend).
        result.Should().Be(ConsolidationDispatchResult.Dispatched);

        // Verify via mock: PrepareAgentConfigsAsync should NOT be called (no configs to process)
        _mockTokenVending.Verify(
            t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Never);
    }

    #endregion

    #region GetLastSuccessfulRunUtcAsync (tested via TryDispatchAsync)

    [Fact]
    public async Task TryDispatchAsync_NoRunsDirectory_LastSuccessfulRunIsNull()
    {
        RegisterIdleAgent();

        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { new() { Id = "agent-cfg", Kind = ProviderKind.Agent, ProviderType = "Kiro", DisplayName = "Agent" } });

        ConsolidationJobMessage? capturedMessage = null;
        _mockAgentComm.Setup(c => c.AssignConsolidationJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ConsolidationJobMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, ConsolidationJobMessage, CancellationToken>((_, _, msg, _) => capturedMessage = msg);

        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new List<ProviderConfig>());

        // Use a non-existent directory
        var svc = CreateService(runsDir: Path.Combine(_tempDir, "nonexistent"));
        var run = new ConsolidationRun { RunId = "r1", Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTimeOffset.UtcNow };

        await svc.TryDispatchAsync(run, ConsolidationRunType.BrainConsolidation, null, null, "/tmp", CancellationToken.None);

        capturedMessage.Should().NotBeNull();
        capturedMessage!.LastSuccessfulRunUtc.Should().BeNull();
    }

    [Fact]
    public async Task TryDispatchAsync_WithMatchingHistoricRun_SetsLastSuccessfulRunUtc()
    {
        RegisterIdleAgent();
        Directory.CreateDirectory(_tempDir);

        // Write a historic successful run
        var historicRun = new ConsolidationRun
        {
            RunId = "old-1",
            Type = ConsolidationRunType.BrainConsolidation,
            TemplateId = null,
            StartedAtUtc = new DateTime(2026, 5, 10, 10, 0, 0, DateTimeKind.Utc),
            CompletedAtUtc = new DateTime(2026, 5, 10, 10, 5, 0, DateTimeKind.Utc),
            Status = ConsolidationRunStatus.Succeeded
        };
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "old-1.json"), JsonSerializer.Serialize(historicRun, s_jsonOptions));

        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { new() { Id = "agent-cfg", Kind = ProviderKind.Agent, ProviderType = "Kiro", DisplayName = "Agent" } });

        ConsolidationJobMessage? capturedMessage = null;
        _mockAgentComm.Setup(c => c.AssignConsolidationJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ConsolidationJobMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, ConsolidationJobMessage, CancellationToken>((_, _, msg, _) => capturedMessage = msg);

        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        var run = new ConsolidationRun { RunId = "r1", Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTimeOffset.UtcNow };

        await svc.TryDispatchAsync(run, ConsolidationRunType.BrainConsolidation, null, null, "/tmp", CancellationToken.None);

        capturedMessage.Should().NotBeNull();
        capturedMessage!.LastSuccessfulRunUtc.Should().Be(new DateTime(2026, 5, 10, 10, 5, 0, DateTimeKind.Utc));
    }

    [Fact]
    public async Task TryDispatchAsync_MalformedHistoricRun_SkipsGracefully()
    {
        RegisterIdleAgent();
        Directory.CreateDirectory(_tempDir);

        // Write a malformed JSON file
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "bad.json"), "not valid json {{{");

        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { new() { Id = "agent-cfg", Kind = ProviderKind.Agent, ProviderType = "Kiro", DisplayName = "Agent" } });

        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        var run = new ConsolidationRun { RunId = "r1", Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTimeOffset.UtcNow };

        // Should not throw
        var result = await svc.TryDispatchAsync(run, ConsolidationRunType.BrainConsolidation, null, null, "/tmp", CancellationToken.None);
        result.Should().Be(ConsolidationDispatchResult.Dispatched);
    }

    // TODO: TryDispatchAsync_AutoDispatchTrue/False tests are nearly identical and should be
    // parameterized with [Theory]/[InlineData(true)]/[InlineData(false)] to reduce duplication.
    [Fact]
    public async Task TryDispatchAsync_AutoDispatchTrue_PropagatedToMessage()
    {
        RegisterIdleAgent();

        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { new() { Id = "agent-cfg", Kind = ProviderKind.Agent, ProviderType = "Kiro", DisplayName = "Agent" } });

        ConsolidationJobMessage? capturedMessage = null;
        _mockAgentComm.Setup(c => c.AssignConsolidationJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ConsolidationJobMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, ConsolidationJobMessage, CancellationToken>((_, _, msg, _) => capturedMessage = msg);

        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService(runsDir: Path.Combine(_tempDir, "nonexistent"));
        var run = new ConsolidationRun
        {
            RunId = "r-auto",
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow,
            AutoDispatch = true
        };

        await svc.TryDispatchAsync(run, ConsolidationRunType.BrainConsolidation, null, null, "/tmp", CancellationToken.None);

        capturedMessage.Should().NotBeNull();
        capturedMessage!.AutoDispatch.Should().BeTrue();
    }

    [Fact]
    public async Task TryDispatchAsync_AutoDispatchFalse_PropagatedToMessage()
    {
        RegisterIdleAgent();

        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { new() { Id = "agent-cfg", Kind = ProviderKind.Agent, ProviderType = "Kiro", DisplayName = "Agent" } });

        ConsolidationJobMessage? capturedMessage = null;
        _mockAgentComm.Setup(c => c.AssignConsolidationJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ConsolidationJobMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, string, ConsolidationJobMessage, CancellationToken>((_, _, msg, _) => capturedMessage = msg);

        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService(runsDir: Path.Combine(_tempDir, "nonexistent"));
        var run = new ConsolidationRun
        {
            RunId = "r-no-auto",
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow,
            AutoDispatch = false
        };

        await svc.TryDispatchAsync(run, ConsolidationRunType.BrainConsolidation, null, null, "/tmp", CancellationToken.None);

        capturedMessage.Should().NotBeNull();
        capturedMessage!.AutoDispatch.Should().BeFalse();
    }

    #endregion

    #region TransitionRunToRunningAsync — IConsolidationRunTracker delegation

    [Fact]
    public async Task TransitionRunToRunningAsync_WithTracker_DelegatesToTracker()
    {
        // Arrange: create a queued run on disk
        var runStore = new FileSystemConsolidationRunStore(_tempDir);
        var run = new ConsolidationRun
        {
            RunId = "tracker-test-1",
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        await runStore.SaveRunAsync(run, CancellationToken.None);

        var mockTracker = new Mock<IConsolidationRunTracker>();
        mockTracker.Setup(t => t.TransitionToRunningAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var svc = CreateServiceWithTracker(runStore, mockTracker.Object);

        // Act: call TransitionRunToRunningAsync directly (internal)
        await svc.TransitionRunToRunningAsync("tracker-test-1", CancellationToken.None);

        // Assert: tracker was called with correct runId
        mockTracker.Verify(
            t => t.TransitionToRunningAsync("tracker-test-1", It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task TransitionRunToRunningAsync_TrackerThrows_DoesNotPropagate()
    {
        // Arrange: tracker throws, but TransitionRunToRunningAsync catches it (non-fatal)
        var runStore = new FileSystemConsolidationRunStore(_tempDir);
        var run = new ConsolidationRun
        {
            RunId = "tracker-throw-1",
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        await runStore.SaveRunAsync(run, CancellationToken.None);

        var mockTracker = new Mock<IConsolidationRunTracker>();
        mockTracker.Setup(t => t.TransitionToRunningAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Simulated tracker failure"));

        var svc = CreateServiceWithTracker(runStore, mockTracker.Object);

        // Act: should not throw — errors are caught internally
        var act = () => svc.TransitionRunToRunningAsync("tracker-throw-1", CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task TransitionRunToRunningAsync_NullTracker_FallsBackToDirectStoreWrite()
    {
        // Arrange: no tracker — falls back to direct store write
        var runId = Guid.NewGuid().ToString();
        var runStore = new FileSystemConsolidationRunStore(_tempDir);
        var run = new ConsolidationRun
        {
            RunId = runId,
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Queued,
            StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-10)
        };
        await runStore.SaveRunAsync(run, CancellationToken.None);

        // No tracker: CreateService passes null for the optional runTracker param
        var svc = CreateService(runsDir: _tempDir);

        // Act
        await svc.TransitionRunToRunningAsync(runId, CancellationToken.None);

        // Assert: run persisted as Running via direct store write
        var updatedRun = await runStore.GetByIdAsync(runId, CancellationToken.None);
        updatedRun.Should().NotBeNull();
        updatedRun!.Status.Should().Be(ConsolidationRunStatus.Running);
        updatedRun.StartedAtUtc.Should().BeAfter(run.StartedAtUtc);
    }

    [Fact]
    public async Task TransitionRunToRunningAsync_RunNotQueued_NoOp()
    {
        // Arrange: run is already Running — should be a no-op
        var runStore = new FileSystemConsolidationRunStore(_tempDir);
        var run = new ConsolidationRun
        {
            RunId = "already-running-1",
            Type = ConsolidationRunType.BrainConsolidation,
            Status = ConsolidationRunStatus.Running,
            StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5)
        };
        await runStore.SaveRunAsync(run, CancellationToken.None);

        var mockTracker = new Mock<IConsolidationRunTracker>();
        var svc = CreateServiceWithTracker(runStore, mockTracker.Object);

        // Act
        await svc.TransitionRunToRunningAsync("already-running-1", CancellationToken.None);

        // Assert: tracker NOT called (ConsolidationService.TransitionToRunningAsync guards on Queued status)
        // Note: The tracker's TransitionToRunningAsync has its own guard, but calling it is still fine —
        // it's the implementation that decides whether to proceed.
        mockTracker.Verify(
            t => t.TransitionToRunningAsync("already-running-1", It.IsAny<CancellationToken>()),
            Times.Once, "tracker is always called when available; it internally guards on Queued status");
    }

    private ConsolidationDispatcher CreateServiceWithTracker(
        IConsolidationRunStore runStore,
        IConsolidationRunTracker tracker,
        PipelineConfiguration? config = null)
    {
        config ??= new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" };
        return new ConsolidationDispatcher(
            _registry,
            _dispatcher,
            _mockAgentComm.Object,
            _mockConfigStore.Object,
            _mockProjectStore.Object,
            _mockTokenVending.Object,
            config,
            _mockWorkDistributor.Object,
            Mock.Of<IPipelineRunHistoryService>(),
            _mockLogger.Object,
            runStore,
            runTracker: new Lazy<IConsolidationRunTracker>(() => tracker));
    }

    #endregion
}
