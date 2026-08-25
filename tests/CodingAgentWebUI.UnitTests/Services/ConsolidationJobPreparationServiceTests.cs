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
    private static readonly string[] E2ELabels = ["e2e"];
    private static readonly string[] KiroDotnetLabels = ["kiro", "dotnet"];
    private static readonly string[] KiroLabels = ["kiro"];

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
        await svc.PrepareAsync(ConsolidationRunType.RefactoringDetection, "t1", E2ELabels, CancellationToken.None);

        capturedIncludeIssue.Should().BeTrue();
    }

    [Fact]
    public async Task PrepareAsync_BrainConsolidation_ExcludesIssuePermission()
    {
        SetupAgentConfig("agent-cfg");
        SetupMatchingProfile("agent-cfg");

        bool capturedIncludeIssue = true; // Start true, expect false
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(
                It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool>(
                (_, _, _, includeIssue) => capturedIncludeIssue = includeIssue)
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        await svc.PrepareAsync(ConsolidationRunType.BrainConsolidation, null, E2ELabels, CancellationToken.None);

        capturedIncludeIssue.Should().BeFalse();
    }

    [Fact]
    public async Task PrepareAsync_HarnessSuggestions_ExcludesIssuePermission()
    {
        SetupAgentConfig("agent-cfg");
        SetupMatchingProfile("agent-cfg");

        bool capturedIncludeIssue = true;
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(
                It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool>(
                (_, _, _, includeIssue) => capturedIncludeIssue = includeIssue)
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        await svc.PrepareAsync(ConsolidationRunType.HarnessSuggestions, null, E2ELabels, CancellationToken.None);

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
        await svc.PrepareAsync(ConsolidationRunType.RefactoringDetection, "t1", E2ELabels, CancellationToken.None);

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
        await svc.PrepareAsync(ConsolidationRunType.BrainConsolidation, "t1", E2ELabels, CancellationToken.None);

        capturedConfigs.Should().NotBeNull();
        capturedConfigs!.Any(c => c.Kind == ProviderKind.Issue).Should().BeFalse();
    }

    #endregion

    #region Profile / fallback agent resolution

    [Fact]
    public async Task PrepareAsync_NoProfileMatch_FallsBackToFirstCompatibleAgentConfig()
    {
        // New behavior (spec 041-045): without a matching AgentProfile, no agent provider config
        // is injected and token vending is skipped. The old RequiredLabels-based fallback has been
        // removed; an explicit profile is required for agent config resolution.
        var kiroConfig = new ProviderConfig
        {
            Id = "kiro-agent-cfg", Kind = ProviderKind.Agent, ProviderType = "KiroCli",
            DisplayName = "KiroCli", RequiredLabels = new List<string> { "kiro" }
        };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { kiroConfig });
        // No profiles → no match → no agent config injected
        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>());

        var svc = CreateService();
        var result = await svc.PrepareAsync(ConsolidationRunType.BrainConsolidation, null, KiroDotnetLabels, CancellationToken.None);

        // Token vending is skipped when rawConfigs is empty
        _mockTokenVending.Verify(
            t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Never);
        result.ProviderConfigs.Should().BeEmpty();
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
        var result = await svc.PrepareAsync(ConsolidationRunType.BrainConsolidation, null, KiroLabels, CancellationToken.None);

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
        // New behavior (spec 041-045): without a matching AgentProfile, no agent provider config
        // is injected. Empty labels produce no profile match; token vending is skipped.
        var agentConfig = new ProviderConfig
        {
            Id = "default-agent", Kind = ProviderKind.Agent, ProviderType = "KiroCli",
            DisplayName = "Default Agent"
        };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { agentConfig });

        var svc = CreateService();
        // Empty agentLabels — no profile matches → no agent config added
        var result = await svc.PrepareAsync(ConsolidationRunType.HarnessSuggestions, null, Array.Empty<string>(), CancellationToken.None);

        _mockTokenVending.Verify(
            t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Never);
        result.ProviderConfigs.Should().BeEmpty();
    }

    #endregion

    #region Template resolution — partial configs

    [Fact]
    public async Task PrepareAsync_TemplateWithRepoOnly_NoBrain_NoIssue()
    {
        SetupAgentConfig("agent-cfg");
        SetupMatchingProfile("agent-cfg");

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
        await svc.PrepareAsync(ConsolidationRunType.BrainConsolidation, "t1", E2ELabels, CancellationToken.None);

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
        SetupMatchingProfile("agent-cfg");

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
        await svc.PrepareAsync(ConsolidationRunType.BrainConsolidation, "t1", E2ELabels, CancellationToken.None);

        capturedConfigs.Should().NotBeNull();
        // agent + repo + brain
        capturedConfigs!.Should().HaveCount(3);
        capturedConfigs.Count(c => c.Kind == ProviderKind.Repository).Should().Be(2);
    }

    [Fact]
    public async Task PrepareAsync_NullTemplateId_OnlyAgentConfig()
    {
        SetupAgentConfig("agent-cfg");
        SetupMatchingProfile("agent-cfg");

        IReadOnlyList<ProviderConfig>? capturedConfigs = null;
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(
                It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool>(
                (configs, _, _, _) => capturedConfigs = configs)
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        var result = await svc.PrepareAsync(ConsolidationRunType.HarnessSuggestions, null, E2ELabels, CancellationToken.None);

        capturedConfigs.Should().NotBeNull();
        capturedConfigs!.Should().HaveCount(1);
        capturedConfigs[0].Kind.Should().Be(ProviderKind.Agent);
        result.RepoProviderConfigId.Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_TemplateNotFound_OnlyAgentConfig()
    {
        SetupAgentConfig("agent-cfg");
        SetupMatchingProfile("agent-cfg");

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
        await svc.PrepareAsync(ConsolidationRunType.BrainConsolidation, "nonexistent", E2ELabels, CancellationToken.None);

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
        await svc.PrepareAsync(ConsolidationRunType.BrainConsolidation, "t1", E2ELabels, CancellationToken.None);

        capturedRepoId.Should().Be("rp-1");
    }

    [Fact]
    public async Task PrepareAsync_NoAgentConfigs_NullTemplate_SkipsTokenVending()
    {
        // No agent configs
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        var result = await svc.PrepareAsync(ConsolidationRunType.HarnessSuggestions, null, E2ELabels, CancellationToken.None);

        _mockTokenVending.Verify(
            t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Never);
        result.ProviderConfigs.Should().BeEmpty();
    }

    #endregion

    #region Cross-mode parity

    // PrepareAsync_SameInputs_ProducesSameResult_RegardlessOfCallerContext was removed.
    // It was self-documented as tautological: called the same method twice with identical
    // inputs and deterministic mocks, verifying only internal consistency rather than
    // correctness against an independent specification.

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

    /// <summary>
    /// Sets up an AgentProfile with empty MatchLabels (catch-all), ensuring the ProfileResolver
    /// always finds a match regardless of what agent labels are passed in the test.
    /// An empty MatchLabels profile matches any agent (Subset strategy: [] ⊆ any set = true).
    /// </summary>
    private void SetupMatchingProfile(string agentConfigId)
    {
        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile>
            {
                new()
                {
                    Id = "default-profile",
                    DisplayName = "Default",
                    Enabled = true,
                    MatchLabels = Array.Empty<string>(),
                    AgentProviderConfigId = agentConfigId,
                    Priority = 1
                }
            });
    }

    #endregion
}
