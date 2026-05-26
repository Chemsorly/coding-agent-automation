using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>Unit tests for <see cref="ConsolidationDispatcher"/>.</summary>
public sealed class ConsolidationDispatcherTests : IDisposable
{
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly AgentRegistryService _registry;
    private readonly JobDispatcherService _dispatcher;
    private readonly Mock<IAgentCommunication> _mockAgentComm = new();
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();
    private readonly Mock<ITokenVendingService> _mockTokenVending = new();
    private readonly string _tempDir;

    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    public ConsolidationDispatcherTests()
    {
        _registry = new AgentRegistryService(_mockLogger.Object);
        _dispatcher = new JobDispatcherService(_registry, _mockLogger.Object);
        _tempDir = Path.Combine(Path.GetTempPath(), $"cds-test-{Guid.NewGuid():N}");
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
            _mockTokenVending.Object,
            config,
            _mockLogger.Object,
            new ConsolidationQueueService(_mockLogger.Object),
            runsDir ?? _tempDir);
    }

    private void RegisterIdleAgent(string agentId = "agent-1", string connectionId = "conn-1", string[]? labels = null)
    {
        var msg = new AgentRegistrationMessage
        {
            AgentId = agentId,
            Hostname = "host-1",
            AgentType = "kiro",
            Labels = labels ?? Array.Empty<string>()
        };
        _registry.Register(msg, connectionId);
    }

    #region Constructor null guards

    [Fact]
    public void Ctor_NullRegistry_Throws()
    {
        var act = () => new ConsolidationDispatcher(null!, _dispatcher, _mockAgentComm.Object, _mockConfigStore.Object, _mockTokenVending.Object, new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" }, _mockLogger.Object, new ConsolidationQueueService(_mockLogger.Object));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullDispatcher_Throws()
    {
        var act = () => new ConsolidationDispatcher(_registry, null!, _mockAgentComm.Object, _mockConfigStore.Object, _mockTokenVending.Object, new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" }, _mockLogger.Object, new ConsolidationQueueService(_mockLogger.Object));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullAgentComm_Throws()
    {
        var act = () => new ConsolidationDispatcher(_registry, _dispatcher, null!, _mockConfigStore.Object, _mockTokenVending.Object, new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" }, _mockLogger.Object, new ConsolidationQueueService(_mockLogger.Object));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullConfigStore_Throws()
    {
        var act = () => new ConsolidationDispatcher(_registry, _dispatcher, _mockAgentComm.Object, null!, _mockTokenVending.Object, new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" }, _mockLogger.Object, new ConsolidationQueueService(_mockLogger.Object));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullTokenVending_Throws()
    {
        var act = () => new ConsolidationDispatcher(_registry, _dispatcher, _mockAgentComm.Object, _mockConfigStore.Object, null!, new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" }, _mockLogger.Object, new ConsolidationQueueService(_mockLogger.Object));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullConfig_Throws()
    {
        var act = () => new ConsolidationDispatcher(_registry, _dispatcher, _mockAgentComm.Object, _mockConfigStore.Object, _mockTokenVending.Object, null!, _mockLogger.Object, new ConsolidationQueueService(_mockLogger.Object));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullLogger_Throws()
    {
        var act = () => new ConsolidationDispatcher(_registry, _dispatcher, _mockAgentComm.Object, _mockConfigStore.Object, _mockTokenVending.Object, new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" }, null!, new ConsolidationQueueService(_mockLogger.Object));
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_NullQueueService_Throws()
    {
        var act = () => new ConsolidationDispatcher(_registry, _dispatcher, _mockAgentComm.Object, _mockConfigStore.Object, _mockTokenVending.Object, new PipelineConfiguration { WorkspaceBaseDirectory = "/tmp" }, _mockLogger.Object, null!);
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
        var run = new ConsolidationRun { RunId = "r1", Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTime.UtcNow };

        var result = await svc.TryDispatchAsync(run, ConsolidationRunType.BrainConsolidation, null, null, "/tmp", CancellationToken.None);

        result.Should().Be(ConsolidationDispatchResult.Queued);
    }

    [Fact]
    public async Task TryDispatchAsync_AgentAvailable_DispatchesAndReturnsDispatched()
    {
        RegisterIdleAgent();

        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { new() { Id = "agent-cfg", Kind = ProviderKind.Agent, ProviderType = "Kiro", DisplayName = "Agent" } });

        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        var run = new ConsolidationRun { RunId = "r1", Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTime.UtcNow, TemplateName = "Test" };

        var result = await svc.TryDispatchAsync(run, ConsolidationRunType.BrainConsolidation, null, null, "/tmp", CancellationToken.None);

        result.Should().Be(ConsolidationDispatchResult.Dispatched);
        _mockAgentComm.Verify(c => c.AssignConsolidationJobAsync("conn-1", "agent-1", It.IsAny<ConsolidationJobMessage>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task TryDispatchAsync_DispatchThrows_ResetsAgentAndReturnsFailed()
    {
        RegisterIdleAgent();

        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { new() { Id = "agent-cfg", Kind = ProviderKind.Agent, ProviderType = "Kiro", DisplayName = "Agent" } });

        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(new List<ProviderConfig>());

        _mockAgentComm.Setup(c => c.AssignConsolidationJobAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<ConsolidationJobMessage>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Connection lost"));

        var svc = CreateService();
        var run = new ConsolidationRun { RunId = "r1", Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTime.UtcNow };

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
            PipelineJobTemplates = new[] { template }
        };

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
        var run = new ConsolidationRun { RunId = "r1", Type = ConsolidationRunType.RefactoringDetection, TemplateId = "t1", StartedAtUtc = DateTime.UtcNow };

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
        var run = new ConsolidationRun { RunId = "r1", Type = ConsolidationRunType.HarnessSuggestions, StartedAtUtc = DateTime.UtcNow };

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
            PipelineJobTemplates = new[] { template }
        };

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
        var run = new ConsolidationRun { RunId = "r1", Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTime.UtcNow };

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
        var run = new ConsolidationRun { RunId = "r1", Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTime.UtcNow };

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
        var run = new ConsolidationRun { RunId = "r1", Type = ConsolidationRunType.BrainConsolidation, StartedAtUtc = DateTime.UtcNow };

        // Should not throw
        var result = await svc.TryDispatchAsync(run, ConsolidationRunType.BrainConsolidation, null, null, "/tmp", CancellationToken.None);
        result.Should().Be(ConsolidationDispatchResult.Dispatched);
    }

    #endregion
}
