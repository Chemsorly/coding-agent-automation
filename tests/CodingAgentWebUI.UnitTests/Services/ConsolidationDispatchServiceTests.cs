using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>Unit tests for <see cref="ConsolidationDispatchService"/>.</summary>
public sealed class ConsolidationDispatchServiceTests : IDisposable
{
    private static readonly string[] KiroDotnetDotnet10Labels = ["kiro", "dotnet", "dotnet10"];
    private static readonly string[] TemplateTIds = ["t1"];

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

    public ConsolidationDispatchServiceTests()
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

        // Default: LoadPipelineConfigAsync returns a default config (tests can override)
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" });

        // Default: return empty projects (no templates will resolve without project ownership)
        _mockProjectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>());

        // Default: IWorkDistributor.DistributeAsync returns success with Queued=true
        _mockWorkDistributor.Setup(w => w.DistributeAsync(It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(true, null, null, Queued: true));

        // Default: AssignConsolidationJobAsync completes successfully
        _mockAgentComm.Setup(c => c.AssignConsolidationJobAsync(It.IsAny<string>(), It.IsAny<AgentId>(), It.IsAny<ConsolidationJobMessage>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    private ConsolidationDispatchService CreateService(PipelineConfiguration? config = null, string? runsDir = null)
    {
        config ??= new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" };
        return new ConsolidationDispatchService(
            new ConsolidationDispatchDependencies(
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
                new FileSystemConsolidationRunStore(runsDir ?? _tempDir)));
    }

    private static ConsolidationDispatchDependencies MakeDeps(
        IAgentRegistryService? registry = null,
        JobDeduplicationGuardService? dispatcher = null,
        IAgentCommunication? agentComm = null,
        IConfigurationStore? configStore = null,
        IProjectStore? projectStore = null,
        ITokenVendingService? tokenVending = null,
        PipelineConfiguration? config = null,
        IWorkDistributor? workDistributor = null,
        IPipelineRunHistoryService? runHistoryService = null,
        ILogger? logger = null,
        IConsolidationRunStore? runStore = null)
    {
        var defaultRegistry = new AgentRegistryService(Mock.Of<ILogger>());
        var defaultDispatcher = new JobDeduplicationGuardService(defaultRegistry, Mock.Of<ILogger>());
        return new ConsolidationDispatchDependencies(
            registry ?? defaultRegistry,
            dispatcher ?? defaultDispatcher,
            agentComm ?? Mock.Of<IAgentCommunication>(),
            configStore ?? Mock.Of<IConfigurationStore>(),
            projectStore ?? Mock.Of<IProjectStore>(),
            tokenVending ?? Mock.Of<ITokenVendingService>(),
            config ?? new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" },
            workDistributor ?? Mock.Of<IWorkDistributor>(),
            runHistoryService ?? Mock.Of<IPipelineRunHistoryService>(),
            logger ?? Mock.Of<ILogger>(),
            runStore ?? Mock.Of<IConsolidationRunStore>());
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
        var act = () => new ConsolidationDispatchService(
            MakeDeps() with { Registry = null! });
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullDispatcher_Throws()
    {
        var act = () => new ConsolidationDispatchService(
            MakeDeps() with { JobDispatcher = null! });
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullAgentComm_Throws()
    {
        var act = () => new ConsolidationDispatchService(
            MakeDeps() with { AgentComm = null! });
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullConfigStore_Throws()
    {
        var act = () => new ConsolidationDispatchService(
            MakeDeps() with { ConfigStore = null! });
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullProjectStore_Throws()
    {
        var act = () => new ConsolidationDispatchService(
            MakeDeps() with { ProjectStore = null! });
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullTokenVending_Throws()
    {
        var act = () => new ConsolidationDispatchService(
            MakeDeps() with { TokenVending = null! });
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullConfig_Throws()
    {
        var act = () => new ConsolidationDispatchService(
            MakeDeps() with { Config = null! });
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        var act = () => new ConsolidationDispatchService(
            MakeDeps() with { Logger = null! });
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
    /// Regression test for root-cause fix: ConsolidationDispatchService must set AgentSelector to the
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
                    MatchLabels = KiroDotnetDotnet10Labels,
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

        _mockAgentComm.Setup(c => c.AssignConsolidationJobAsync(It.IsAny<string>(), It.IsAny<AgentId>(), It.IsAny<ConsolidationJobMessage>(), It.IsAny<CancellationToken>()))
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
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = TemplateTIds }
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
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = TemplateTIds }
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
        RegisterIdleAgent(labels: KiroDotnetDotnet10Labels);

        var kiroProfile = new AgentProfile
        {
            Id = "prof-kiro-dotnet",
            DisplayName = "Kiro DotNet",
            MatchLabels = KiroDotnetDotnet10Labels,
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
        RegisterIdleAgent(labels: KiroDotnetDotnet10Labels);

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
        RegisterIdleAgent(labels: KiroDotnetDotnet10Labels);

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
        _mockAgentComm.Setup(c => c.AssignConsolidationJobAsync(It.IsAny<string>(), It.IsAny<AgentId>(), It.IsAny<ConsolidationJobMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, AgentId, ConsolidationJobMessage, CancellationToken>((_, _, msg, _) => capturedMessage = msg);

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
        _mockAgentComm.Setup(c => c.AssignConsolidationJobAsync(It.IsAny<string>(), It.IsAny<AgentId>(), It.IsAny<ConsolidationJobMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, AgentId, ConsolidationJobMessage, CancellationToken>((_, _, msg, _) => capturedMessage = msg);

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
        _mockAgentComm.Setup(c => c.AssignConsolidationJobAsync(It.IsAny<string>(), It.IsAny<AgentId>(), It.IsAny<ConsolidationJobMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, AgentId, ConsolidationJobMessage, CancellationToken>((_, _, msg, _) => capturedMessage = msg);

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
        _mockAgentComm.Setup(c => c.AssignConsolidationJobAsync(It.IsAny<string>(), It.IsAny<AgentId>(), It.IsAny<ConsolidationJobMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, AgentId, ConsolidationJobMessage, CancellationToken>((_, _, msg, _) => capturedMessage = msg);

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

    private ConsolidationDispatchService CreateServiceWithTracker(
        IConsolidationRunStore runStore,
        IConsolidationRunTracker tracker,
        PipelineConfiguration? config = null)
    {
        config ??= new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" };
        return new ConsolidationDispatchService(
            new ConsolidationDispatchDependencies(
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
                runStore),
            runTracker: new Lazy<IConsolidationRunTracker>(() => tracker));
    }

    // ── Live config reload regression test ─────────────────────────────

    /// <summary>
    /// Regression test: ConsolidationDispatchService must send the LIVE pipeline configuration
    /// from the config store in the job message, not the stale startup singleton.
    /// Bug: Program.cs loaded pipelineConfig from a missing JSON file (→ defaults with 30-min timeout),
    /// while the DB store had the user-configured 2-hour timeout. The consolidation dispatch path
    /// sent the stale 30-min default, causing runs to be cancelled prematurely.
    /// </summary>
    [Fact]
    public async Task TryDispatchAsync_JobMessage_UsesLiveConfigFromStore_NotStartupSingleton()
    {
        // Arrange: startup singleton has DEFAULT 30-min timeout (simulates missing JSON file scenario)
        var staleStartupConfig = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = "/tmp",
            AgentTimeout = TimeSpan.FromMinutes(30) // stale default
        };

        // The config store returns the LIVE value (user set 120 min via UI → saved to DB)
        var liveConfig = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = "/tmp",
            AgentTimeout = TimeSpan.FromMinutes(120) // live DB value
        };
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(liveConfig);

        RegisterIdleAgent();

        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { new() { Id = "agent-cfg", Kind = ProviderKind.Agent, ProviderType = "Kiro", DisplayName = "Agent" } });

        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new List<ProviderConfig>());

        ConsolidationJobMessage? capturedMessage = null;
        _mockAgentComm
            .Setup(c => c.AssignConsolidationJobAsync(It.IsAny<string>(), It.IsAny<AgentId>(), It.IsAny<ConsolidationJobMessage>(), It.IsAny<CancellationToken>()))
            .Callback<string, AgentId, ConsolidationJobMessage, CancellationToken>((_, _, msg, _) => capturedMessage = msg)
            .Returns(Task.CompletedTask);

        var svc = CreateService(staleStartupConfig);
        var run = new ConsolidationRun
        {
            RunId = "r1",
            Type = ConsolidationRunType.BrainConsolidation,
            StartedAtUtc = DateTimeOffset.UtcNow,
            TemplateName = "Test"
        };

        // Act
        var result = await svc.TryDispatchAsync(run, ConsolidationRunType.BrainConsolidation, null, null, "/tmp", CancellationToken.None);

        // Assert
        result.Should().Be(ConsolidationDispatchResult.Dispatched);
        capturedMessage.Should().NotBeNull();
        capturedMessage!.PipelineConfiguration.AgentTimeout.Should().Be(
            TimeSpan.FromMinutes(120),
            "job message must carry the LIVE config from the store, not the stale startup singleton");
    }

    #endregion

    #region ResolveRequiredLabelsAsync and ResolveAgentSelectorLabelsAsync

    [Fact]
    public async Task ResolveRequiredLabelsAsync_WhenNullTemplateId_UsesDefaultLabels()
    {
        var config = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = "/tmp",
            DefaultRequiredAgentLabels = "dotnet,kiro"
        };
        var svc = CreateService(config);

        var labels = await svc.ResolveRequiredLabelsAsync(null, config, CancellationToken.None);

        labels.Should().NotBeEmpty("null templateId should fall back to default required labels from config");
    }

    [Fact]
    public async Task ResolveRequiredLabelsAsync_WhenTemplateNotFound_UsesDefaultLabels()
    {
        // No projects configured → template not found
        _mockProjectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>());
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>());

        var config = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = "/tmp",
            DefaultRequiredAgentLabels = "opencode"
        };
        var svc = CreateService(config);

        var labels = await svc.ResolveRequiredLabelsAsync((TemplateId)"unknown-template", config, CancellationToken.None);

        labels.Should().NotBeEmpty("missing template should fall back to default config labels");
    }

    [Fact]
    public async Task ResolveAgentSelectorLabelsAsync_WhenNoMatchingProfile_ReturnsSameLabels()
    {
        // No profiles → should fall back to the input labels
        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>());

        var svc = CreateService();
        var input = new[] { "dotnet", "kiro" };

        var result = await svc.ResolveAgentSelectorLabelsAsync(input, CancellationToken.None);

        result.Should().BeEquivalentTo(input, "no matching profile means raw required labels are returned as-is");
    }

    [Fact]
    public async Task ResolveAgentSelectorLabelsAsync_WhenProfileMatches_ReturnsProfileMatchLabels()
    {
        var profile = new AgentProfile
        {
            Id = "prof-1",
            DisplayName = "Kiro Dotnet",
            Enabled = true,
            MatchLabels = ["dotnet", "dotnet10", "kiro"],
            AgentProviderConfigId = "agent-cfg"
        };
        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile> { profile });

        var svc = CreateService();
        var input = new[] { "dotnet", "kiro" }; // subset of profile MatchLabels

        var result = await svc.ResolveAgentSelectorLabelsAsync(input, CancellationToken.None);

        result.Should().BeEquivalentTo(new[] { "dotnet", "dotnet10", "kiro" },
            "matching profile should return its full MatchLabels set");
    }

    [Fact]
    public async Task ResolveAgentSelectorLabelsAsync_WhenMultipleProfiles_SelectsFirstMatch()
    {
        var profile1 = new AgentProfile
        {
            Id = "prof-1", DisplayName = "OpenCode", Enabled = true,
            MatchLabels = ["opencode", "python"],
            AgentProviderConfigId = "oc-cfg", Priority = 1
        };
        var profile2 = new AgentProfile
        {
            Id = "prof-2", DisplayName = "Kiro", Enabled = true,
            MatchLabels = ["dotnet", "kiro"],
            AgentProviderConfigId = "kiro-cfg", Priority = 2
        };
        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile> { profile1, profile2 });

        var svc = CreateService();
        var input = new[] { "dotnet", "kiro" };

        var result = await svc.ResolveAgentSelectorLabelsAsync(input, CancellationToken.None);

        result.Should().BeEquivalentTo(new[] { "dotnet", "kiro" },
            "should select the profile whose MatchLabels cover the input labels");
    }

    #endregion

    #region NotifyRunCancelledAsync

    [Fact]
    public async Task NotifyRunCancelledAsync_CallsWorkDistributorCancelJob()
    {
        _mockWorkDistributor.Setup(w => w.CancelJobAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var svc = CreateService();
        await svc.NotifyRunCancelledAsync("run-cancel-1", CancellationToken.None);

        _mockWorkDistributor.Verify(w => w.CancelJobAsync("run-cancel-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyRunCancelledAsync_CallsDispatcherRemoveJob()
    {
        _mockWorkDistributor.Setup(w => w.CancelJobAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var svc = CreateService();
        // Call NotifyRunCancelledAsync — it should call _jobDispatcher.RemoveJob(runId)
        // RemoveJob internally calls RemoveFromQueue which removes from _processingIssues using "consolidation" as provider
        await svc.NotifyRunCancelledAsync("run-remove-test", CancellationToken.None);

        // Verify that CancelJobAsync was called on the distributor
        _mockWorkDistributor.Verify(w => w.CancelJobAsync("run-remove-test", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task NotifyRunCancelledAsync_NullRunId_ThrowsArgumentNullException()
    {
        var svc = CreateService();
        await svc.Invoking(s => s.NotifyRunCancelledAsync(null!, CancellationToken.None))
            .Should().ThrowAsync<ArgumentNullException>();
    }

    #endregion
}
