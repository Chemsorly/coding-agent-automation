using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="ConsolidationJobPreparationService"/>.
/// Directly tests the shared preparation logic used by both SignalR and K8s dispatch paths.
/// </summary>
public sealed class ConsolidationJobPreparationServiceTests
{
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();
    private readonly Mock<IProjectStore> _mockProjectStore = new();
    private readonly Mock<ITokenVendingService> _mockTokenVending = new();
    private readonly Mock<ILogger> _mockLogger = new();

    public ConsolidationJobPreparationServiceTests()
    {
        // Default: delegate GetProviderConfigByIdAsync to LoadProviderConfigsAsync + filter
        // TODO: Sync-over-async (.GetAwaiter().GetResult()) inside mock Returns lambda is fragile — could deadlock
        // if LoadProviderConfigsAsync is ever set up to return a delayed task. Consider restructuring to avoid blocking call.
        _mockConfigStore
            .Setup(s => s.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Returns((string id, ProviderKind kind, CancellationToken ct) =>
            {
                var configs = _mockConfigStore.Object.LoadProviderConfigsAsync(kind, ct).GetAwaiter().GetResult();
                return Task.FromResult(configs.FirstOrDefault(c => c.Id == id));
            });

        // Default: return empty profiles
        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>());

        // Default: return empty templates
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>());

        // Default: token vending returns input configs as-is
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(
                It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync((IReadOnlyList<ProviderConfig> configs, string _, CancellationToken _, bool _) =>
                configs.ToList().AsReadOnly());
    }

    private ConsolidationJobPreparationService CreateService() =>
        new(_mockConfigStore.Object, _mockProjectStore.Object, _mockTokenVending.Object, _mockLogger.Object);

    #region Constructor null guards

    [Fact]
    public void Ctor_Convenience_NullConfigStore_Throws()
    {
        var act = () => new ConsolidationJobPreparationService(
            (IConfigurationStore)null!, _mockProjectStore.Object, _mockTokenVending.Object, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_Convenience_NullProjectStore_Throws()
    {
        var act = () => new ConsolidationJobPreparationService(
            _mockConfigStore.Object, (IProjectStore)null!, _mockTokenVending.Object, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_Convenience_NullTokenVending_Throws()
    {
        var act = () => new ConsolidationJobPreparationService(
            _mockConfigStore.Object, _mockProjectStore.Object, (ITokenVendingService)null!, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_Convenience_NullLogger_Throws()
    {
        var act = () => new ConsolidationJobPreparationService(
            _mockConfigStore.Object, _mockProjectStore.Object, _mockTokenVending.Object, (ILogger)null!);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Ctor_Primary_ProviderConfigStoreNotIAgentProfileStore_NoExplicitProfileStore_Throws()
    {
        // Use a plain IProviderConfigStore mock (does NOT implement IAgentProfileStore)
        var plainProviderConfigStore = new Mock<IProviderConfigStore>();
        var act = () => new ConsolidationJobPreparationService(
            plainProviderConfigStore.Object, _mockProjectStore.Object, _mockTokenVending.Object, _mockLogger.Object, null);
        act.Should().Throw<ArgumentException>();
    }

    #endregion

    #region Permission flag per run type

    [Fact]
    public async Task PrepareAsync_RefactoringDetection_IncludesIssuePermission()
    {
        SetupAgentConfig("agent-cfg");
        SetupTemplateWithAllProviders();
        SetupRepoConfigs();
        SetupIssueConfig();

        // TODO: Token vending mock returns empty list, so result.ProviderConfigs is always empty.
        // This test only verifies the callback captured includeIssue=true but doesn't verify the
        // result contains configs. If PrepareAsync skipped token vending, this would still pass.
        bool capturedIncludeIssue = false;
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(
                It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool>(
                (_, _, _, includeIssue) => capturedIncludeIssue = includeIssue)
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        await svc.PrepareAsync(ConsolidationRunType.RefactoringDetection, "t1", new[] { "e2e" }, CancellationToken.None);

        capturedIncludeIssue.Should().BeTrue();
    }

    [Fact]
    public async Task PrepareAsync_BrainConsolidation_ExcludesIssuePermission()
    {
        SetupAgentConfig("agent-cfg");

        bool capturedIncludeIssue = true; // Start true, expect false
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(
                It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool>(
                (_, _, _, includeIssue) => capturedIncludeIssue = includeIssue)
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        await svc.PrepareAsync(ConsolidationRunType.BrainConsolidation, null, new[] { "e2e" }, CancellationToken.None);

        capturedIncludeIssue.Should().BeFalse();
    }

    [Fact]
    public async Task PrepareAsync_HarnessSuggestions_ExcludesIssuePermission()
    {
        SetupAgentConfig("agent-cfg");

        bool capturedIncludeIssue = true;
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(
                It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool>(
                (_, _, _, includeIssue) => capturedIncludeIssue = includeIssue)
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        await svc.PrepareAsync(ConsolidationRunType.HarnessSuggestions, null, new[] { "e2e" }, CancellationToken.None);

        capturedIncludeIssue.Should().BeFalse();
    }

    #endregion

    #region Issue provider config inclusion

    [Fact]
    public async Task PrepareAsync_RefactoringDetection_IncludesIssueProviderConfig()
    {
        SetupAgentConfig("agent-cfg");
        SetupTemplateWithAllProviders();
        SetupRepoConfigs();
        SetupIssueConfig();

        IReadOnlyList<ProviderConfig>? capturedConfigs = null;
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(
                It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool>(
                (configs, _, _, _) => capturedConfigs = configs)
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        await svc.PrepareAsync(ConsolidationRunType.RefactoringDetection, "t1", new[] { "e2e" }, CancellationToken.None);

        capturedConfigs.Should().NotBeNull();
        capturedConfigs!.Any(c => c.Kind == ProviderKind.Issue).Should().BeTrue();
    }

    [Fact]
    public async Task PrepareAsync_NonRefactoring_ExcludesIssueProviderConfig()
    {
        SetupAgentConfig("agent-cfg");
        SetupTemplateWithAllProviders();
        SetupRepoConfigs();
        SetupIssueConfig();

        IReadOnlyList<ProviderConfig>? capturedConfigs = null;
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(
                It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool>(
                (configs, _, _, _) => capturedConfigs = configs)
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        await svc.PrepareAsync(ConsolidationRunType.BrainConsolidation, "t1", new[] { "e2e" }, CancellationToken.None);

        capturedConfigs.Should().NotBeNull();
        capturedConfigs!.Any(c => c.Kind == ProviderKind.Issue).Should().BeFalse();
    }

    #endregion

    #region Profile / fallback agent resolution

    [Fact]
    public async Task PrepareAsync_NoProfileMatch_FallsBackToFirstCompatibleAgentConfig()
    {
        // Agent config with RequiredLabels matching agent labels
        var kiroConfig = new ProviderConfig
        {
            Id = "kiro-agent-cfg", Kind = ProviderKind.Agent, ProviderType = "KiroCli",
            DisplayName = "KiroCli", RequiredLabels = new List<string> { "kiro" }
        };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { kiroConfig });
        // No profiles → fallback
        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>());

        IReadOnlyList<ProviderConfig>? capturedConfigs = null;
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(
                It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool>(
                (configs, _, _, _) => capturedConfigs = configs)
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        await svc.PrepareAsync(ConsolidationRunType.BrainConsolidation, null, new[] { "kiro", "dotnet" }, CancellationToken.None);

        capturedConfigs.Should().NotBeNull();
        var agentCfg = capturedConfigs!.FirstOrDefault(c => c.Kind == ProviderKind.Agent);
        agentCfg.Should().NotBeNull();
        agentCfg!.Id.Should().Be("kiro-agent-cfg");
    }

    [Fact]
    public async Task PrepareAsync_NoProfileMatch_NoCompatibleConfig_SkipsAgentProvider()
    {
        // Agent config requires "opencode" but agent has "kiro" labels → incompatible
        var openCodeConfig = new ProviderConfig
        {
            Id = "opencode-cfg", Kind = ProviderKind.Agent, ProviderType = "OpenCode",
            DisplayName = "OpenCode", RequiredLabels = new List<string> { "opencode" }
        };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { openCodeConfig });

        var svc = CreateService();
        var result = await svc.PrepareAsync(ConsolidationRunType.BrainConsolidation, null, new[] { "kiro" }, CancellationToken.None);

        // No configs → token vending not called
        _mockTokenVending.Verify(
            t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Never);
        result.ProviderConfigs.Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_EmptyAgentLabels_NoProfileMatch_FallsBackSuccessfully()
    {
        // Agent config with no RequiredLabels → compatible with any labels (including empty)
        var agentConfig = new ProviderConfig
        {
            Id = "default-agent", Kind = ProviderKind.Agent, ProviderType = "KiroCli",
            DisplayName = "Default Agent"
        };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { agentConfig });

        IReadOnlyList<ProviderConfig>? capturedConfigs = null;
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(
                It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool>(
                (configs, _, _, _) => capturedConfigs = configs)
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        // Empty agentLabels
        await svc.PrepareAsync(ConsolidationRunType.HarnessSuggestions, null, Array.Empty<string>(), CancellationToken.None);

        capturedConfigs.Should().NotBeNull();
        capturedConfigs!.Should().HaveCount(1);
        capturedConfigs[0].Id.Should().Be("default-agent");
    }

    #endregion

    #region Template resolution — partial configs

    [Fact]
    public async Task PrepareAsync_TemplateWithRepoOnly_NoBrain_NoIssue()
    {
        SetupAgentConfig("agent-cfg");

        var template = new PipelineJobTemplate
        {
            Id = "t1", Name = "Repo Only", IssueProviderId = "ip-1", RepoProviderId = "rp-1"
        };
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template });

        var repoConfig = new ProviderConfig { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo" };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { repoConfig });

        IReadOnlyList<ProviderConfig>? capturedConfigs = null;
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(
                It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool>(
                (configs, _, _, _) => capturedConfigs = configs)
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        await svc.PrepareAsync(ConsolidationRunType.BrainConsolidation, "t1", new[] { "e2e" }, CancellationToken.None);

        capturedConfigs.Should().NotBeNull();
        // Should contain agent + repo (no brain, no issue for BrainConsolidation)
        capturedConfigs!.Should().HaveCount(2);
        capturedConfigs.Any(c => c.Kind == ProviderKind.Agent).Should().BeTrue();
        capturedConfigs.Any(c => c.Kind == ProviderKind.Repository && c.Id == "rp-1").Should().BeTrue();
    }

    [Fact]
    public async Task PrepareAsync_TemplateWithRepoAndBrain_BothResolved()
    {
        SetupAgentConfig("agent-cfg");

        var template = new PipelineJobTemplate
        {
            Id = "t1", Name = "Full", IssueProviderId = "ip-1", RepoProviderId = "rp-1", BrainProviderId = "bp-1"
        };
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template });

        var repoConfig = new ProviderConfig { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo" };
        var brainConfig = new ProviderConfig { Id = "bp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Brain" };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { repoConfig, brainConfig });

        IReadOnlyList<ProviderConfig>? capturedConfigs = null;
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(
                It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool>(
                (configs, _, _, _) => capturedConfigs = configs)
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        await svc.PrepareAsync(ConsolidationRunType.BrainConsolidation, "t1", new[] { "e2e" }, CancellationToken.None);

        capturedConfigs.Should().NotBeNull();
        // agent + repo + brain
        capturedConfigs!.Should().HaveCount(3);
        capturedConfigs.Count(c => c.Kind == ProviderKind.Repository).Should().Be(2);
    }

    [Fact]
    public async Task PrepareAsync_NullTemplateId_OnlyAgentConfig()
    {
        SetupAgentConfig("agent-cfg");

        IReadOnlyList<ProviderConfig>? capturedConfigs = null;
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(
                It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool>(
                (configs, _, _, _) => capturedConfigs = configs)
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        var result = await svc.PrepareAsync(ConsolidationRunType.HarnessSuggestions, null, new[] { "e2e" }, CancellationToken.None);

        capturedConfigs.Should().NotBeNull();
        capturedConfigs!.Should().HaveCount(1);
        capturedConfigs[0].Kind.Should().Be(ProviderKind.Agent);
        result.RepoProviderConfigId.Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_TemplateNotFound_OnlyAgentConfig()
    {
        SetupAgentConfig("agent-cfg");

        // Template "nonexistent" is not in the list
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>
            {
                new() { Id = "other", Name = "Other", IssueProviderId = "ip", RepoProviderId = "rp" }
            });

        IReadOnlyList<ProviderConfig>? capturedConfigs = null;
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(
                It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool>(
                (configs, _, _, _) => capturedConfigs = configs)
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        await svc.PrepareAsync(ConsolidationRunType.BrainConsolidation, "nonexistent", new[] { "e2e" }, CancellationToken.None);

        capturedConfigs.Should().NotBeNull();
        capturedConfigs!.Should().HaveCount(1);
        capturedConfigs[0].Kind.Should().Be(ProviderKind.Agent);
    }

    #endregion

    #region Token vending correctness

    [Fact]
    public async Task PrepareAsync_TokenVendingCalledWithCorrectRepoId()
    {
        SetupAgentConfig("agent-cfg");

        var template = new PipelineJobTemplate
        {
            Id = "t1", Name = "Test", IssueProviderId = "ip-1", RepoProviderId = "rp-1"
        };
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template });

        var repoConfig = new ProviderConfig { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo" };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { repoConfig });

        string? capturedRepoId = null;
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(
                It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool>(
                (_, repoId, _, _) => capturedRepoId = repoId)
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        await svc.PrepareAsync(ConsolidationRunType.BrainConsolidation, "t1", new[] { "e2e" }, CancellationToken.None);

        capturedRepoId.Should().Be("rp-1");
    }

    [Fact]
    public async Task PrepareAsync_NoAgentConfigs_NullTemplate_SkipsTokenVending()
    {
        // No agent configs
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        var result = await svc.PrepareAsync(ConsolidationRunType.HarnessSuggestions, null, new[] { "e2e" }, CancellationToken.None);

        _mockTokenVending.Verify(
            t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Never);
        result.ProviderConfigs.Should().BeEmpty();
    }

    #endregion

    #region Cross-mode parity

    [Fact]
    public async Task PrepareAsync_SameInputs_ProducesSameResult_RegardlessOfCallerContext()
    {
        // TODO: This test is tautological — it calls the same method on the same instance twice with
        // identical inputs and deterministic mocks. It serves only as a regression guard if the shared
        // service is ever split back into separate paths. Consider using two separate instances or
        // simulating different caller contexts to add real verification value.

        // Setup: full template with all providers
        SetupAgentConfig("agent-cfg");
        SetupTemplateWithAllProviders();
        SetupRepoConfigs();
        SetupIssueConfig();

        var svc = CreateService();
        var labels = new[] { "e2e" };

        // Call twice with identical inputs
        var result1 = await svc.PrepareAsync(
            ConsolidationRunType.RefactoringDetection, "t1", labels, CancellationToken.None);
        var result2 = await svc.PrepareAsync(
            ConsolidationRunType.RefactoringDetection, "t1", labels, CancellationToken.None);

        // Assert: same result
        result1.RepoProviderConfigId.Should().Be(result2.RepoProviderConfigId);
        result1.ProviderConfigs.Should().HaveCount(result2.ProviderConfigs.Count);

        for (var i = 0; i < result1.ProviderConfigs.Count; i++)
        {
            result1.ProviderConfigs[i].Id.Should().Be(result2.ProviderConfigs[i].Id);
            result1.ProviderConfigs[i].Kind.Should().Be(result2.ProviderConfigs[i].Kind);
        }
    }

    #endregion

    #region Helpers

    private void SetupAgentConfig(string agentConfigId)
    {
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = agentConfigId, Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Agent" }
            });
    }

    private void SetupTemplateWithAllProviders()
    {
        var template = new PipelineJobTemplate
        {
            Id = "t1", Name = "Full Template", IssueProviderId = "ip-1", RepoProviderId = "rp-1", BrainProviderId = "bp-1"
        };
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template });
    }

    private void SetupRepoConfigs()
    {
        var repoConfig = new ProviderConfig { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo" };
        var brainConfig = new ProviderConfig { Id = "bp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Brain" };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { repoConfig, brainConfig });
    }

    private void SetupIssueConfig()
    {
        var issueConfig = new ProviderConfig { Id = "ip-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Issue" };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { issueConfig });
    }

    #endregion
}
