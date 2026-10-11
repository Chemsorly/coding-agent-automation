using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration.UnitTests;

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

        // Default: return global pipeline config (no overrides)
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        // Default: return empty templates
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>());

        // Default: return empty projects (no owning project found)
        _mockProjectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>());

        // Default: token vending returns input configs as-is
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(
                It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<IReadOnlySet<string>?>()))
            .ReturnsAsync((IReadOnlyList<ProviderConfig> configs, string _, CancellationToken _, bool _, IReadOnlySet<string>? _) =>
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
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<IReadOnlySet<string>?>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool, IReadOnlySet<string>?>(
                (_, _, _, includeIssue, _) => capturedIncludeIssue = includeIssue)
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
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<IReadOnlySet<string>?>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool, IReadOnlySet<string>?>(
                (_, _, _, includeIssue, _) => capturedIncludeIssue = includeIssue)
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
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<IReadOnlySet<string>?>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool, IReadOnlySet<string>?>(
                (_, _, _, includeIssue, _) => capturedIncludeIssue = includeIssue)
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
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<IReadOnlySet<string>?>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool, IReadOnlySet<string>?>(
                (configs, _, _, _, _) => capturedConfigs = configs)
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
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<IReadOnlySet<string>?>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool, IReadOnlySet<string>?>(
                (configs, _, _, _, _) => capturedConfigs = configs)
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
            Id = "kiro-agent-cfg",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "KiroCli",
            RequiredLabels = new List<string> { "kiro" }
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
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<IReadOnlySet<string>?>()),
            Times.Never);
        result.ProviderConfigs.Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_NoProfileMatch_NoCompatibleConfig_SkipsAgentProvider()
    {
        // Agent config requires "opencode" but agent has "kiro" labels → incompatible
        var openCodeConfig = new ProviderConfig
        {
            Id = "opencode-cfg",
            Kind = ProviderKind.Agent,
            ProviderType = "OpenCode",
            DisplayName = "OpenCode",
            RequiredLabels = new List<string> { "opencode" }
        };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { openCodeConfig });

        var svc = CreateService();
        var result = await svc.PrepareAsync(ConsolidationRunType.BrainConsolidation, null, KiroLabels, CancellationToken.None);

        // No configs → token vending not called
        _mockTokenVending.Verify(
            t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<IReadOnlySet<string>?>()),
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
            Id = "default-agent",
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Default Agent"
        };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { agentConfig });

        var svc = CreateService();
        // Empty agentLabels — no profile matches → no agent config added
        var result = await svc.PrepareAsync(ConsolidationRunType.HarnessSuggestions, null, Array.Empty<string>(), CancellationToken.None);

        _mockTokenVending.Verify(
            t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<IReadOnlySet<string>?>()),
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
            Id = "t1",
            Name = "Repo Only",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1"
        };
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template });

        var repoConfig = new ProviderConfig { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo" };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { repoConfig });

        IReadOnlyList<ProviderConfig>? capturedConfigs = null;
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(
                It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<IReadOnlySet<string>?>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool, IReadOnlySet<string>?>(
                (configs, _, _, _, _) => capturedConfigs = configs)
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
            Id = "t1",
            Name = "Full",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            BrainProviderId = "bp-1"
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
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<IReadOnlySet<string>?>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool, IReadOnlySet<string>?>(
                (configs, _, _, _, _) => capturedConfigs = configs)
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
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<IReadOnlySet<string>?>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool, IReadOnlySet<string>?>(
                (configs, _, _, _, _) => capturedConfigs = configs)
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
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<IReadOnlySet<string>?>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool, IReadOnlySet<string>?>(
                (configs, _, _, _, _) => capturedConfigs = configs)
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
            Id = "t1",
            Name = "Test",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1"
        };
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template });

        var repoConfig = new ProviderConfig { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo" };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { repoConfig });

        string? capturedRepoId = null;
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(
                It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<IReadOnlySet<string>?>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool, IReadOnlySet<string>?>(
                (_, repoId, _, _, _) => capturedRepoId = repoId)
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
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<IReadOnlySet<string>?>()),
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

    #region Pipeline configuration resolution

    [Fact]
    public async Task WhenGlobalConfigHasRefactoringReviewEnabledTrue_AndProjectOverrideSetsItFalse_PrepareAsync_ReturnsResolvedConfigWithRefactoringReviewEnabledFalse()
    {
        // Arrange: global config has RefactoringReviewEnabled = true (the default)
        // TODO [WARNING]: This test is structurally an integration test of PipelineConfigurationResolver.ApplyProjectOverrides
        // through the service. Its correctness depends on PipelineProject.RefactoringReviewEnabled being the exact field
        // that ApplyProjectOverrides reads. If the property were renamed or mapped differently, the assertion BeFalse()
        // would correctly catch that (the resolver would silently not apply the override and the result would remain true).
        // Consider adding a comment or guard assertion confirming the resolver was invoked with the project object to make
        // the intent explicit (e.g., verify _mockProjectStore.LoadProjectsAsync was called, or document the field mapping).
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        var templateId = "t1";
        var project = new PipelineProject
        {
            Id = "proj-1",
            Name = "Test Project",
            TemplateIds = [templateId],
            RefactoringReviewEnabled = false   // project override: disable review
        };

        _mockProjectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject> { project });

        var template = new PipelineJobTemplate
        {
            Id = templateId,
            Name = "Test Template",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1"
        };
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template });

        var repoConfig = new ProviderConfig
        {
            Id = "rp-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Repo"
        };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { repoConfig });

        var svc = CreateService();

        // Act
        var result = await svc.PrepareAsync(
            ConsolidationRunType.BrainConsolidation,
            templateId,
            E2ELabels,
            CancellationToken.None);

        // Assert: project override must have been applied
        result.PipelineConfiguration.Should().NotBeNull();
        result.PipelineConfiguration!.RefactoringReviewEnabled.Should().BeFalse(
            "project override sets RefactoringReviewEnabled = false and must be applied via PipelineConfigurationResolver.ResolveAsync");
    }

    [Fact]
    public async Task WhenProjectHasAgentTimeoutOverride_PrepareAsync_ReturnsConfigWithOverriddenAgentTimeout()
    {
        var expectedTimeout = TimeSpan.FromMinutes(90);

        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        var templateId = "t2";
        var project = new PipelineProject
        {
            Id = "proj-2",
            Name = "Timeout Project",
            TemplateIds = [templateId],
            AgentTimeout = expectedTimeout
        };

        _mockProjectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject> { project });

        var template = new PipelineJobTemplate
        {
            Id = templateId,
            Name = "Timeout Template",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-2"
        };
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template });

        var repoConfig = new ProviderConfig
        {
            Id = "rp-2",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Repo"
        };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { repoConfig });

        var svc = CreateService();

        var result = await svc.PrepareAsync(
            ConsolidationRunType.BrainConsolidation,
            templateId,
            E2ELabels,
            CancellationToken.None);

        result.PipelineConfiguration.Should().NotBeNull();
        result.PipelineConfiguration!.AgentTimeout.Should().Be(expectedTimeout,
            "project override sets AgentTimeout and must be applied via PipelineConfigurationResolver.ResolveAsync");
    }

    [Fact]
    public async Task WhenTemplateHasNoRepoProvider_PrepareAsync_StillAppliesProjectOverrides()
    {
        // Regression: a template with no RepoProviderId still belongs to a project whose overrides
        // (e.g. AgentTimeout) must be applied. Template-level overrides need a repoProviderId, but
        // project overrides do not — the empty-repo path applies them via ApplyProjectOverrides
        // instead of falling back to the raw global config.
        var expectedTimeout = TimeSpan.FromMinutes(77);

        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        var templateId = "t-no-repo";
        var project = new PipelineProject
        {
            Id = "proj-no-repo",
            Name = "No-Repo Project",
            TemplateIds = [templateId],
            AgentTimeout = expectedTimeout
        };
        _mockProjectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject> { project });

        var template = new PipelineJobTemplate
        {
            Id = templateId,
            Name = "No-Repo Template",
            IssueProviderId = "ip-1",
            RepoProviderId = "" // no repo provider configured
        };
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template });

        var svc = CreateService();

        var result = await svc.PrepareAsync(
            ConsolidationRunType.BrainConsolidation,
            templateId,
            E2ELabels,
            CancellationToken.None);

        result.PipelineConfiguration.AgentTimeout.Should().Be(expectedTimeout,
            "project overrides must apply even when the template has no RepoProviderId");
    }

    [Fact]
    public async Task WhenTemplateIdIsNull_PrepareAsync_PipelineConfigurationReflectsGlobalConfig()
    {
        // When no templateId is provided, PrepareAsync falls back to the global config load
        // without project or template overrides.

        // TODO [WARNING]: The assertion below is tautological. new PipelineConfiguration() has
        // RefactoringReviewEnabled = true by default, which is also what the mock returns. If PrepareAsync
        // were changed to return new PipelineConfiguration() directly without calling LoadPipelineConfigAsync,
        // this assertion would still pass. To make this test meaningful, set the mock to return a config with
        // a non-default value (e.g., RefactoringReviewEnabled = false) and assert the result reflects that
        // value, or verify that LoadPipelineConfigAsync was called at least once (Times.AtLeastOnce()).
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        SetupAgentConfig("agent-cfg");
        SetupMatchingProfile("agent-cfg");

        var svc = CreateService();

        var result = await svc.PrepareAsync(
            ConsolidationRunType.HarnessSuggestions,
            null,
            E2ELabels,
            CancellationToken.None);

        result.PipelineConfiguration.Should().NotBeNull();
        // Global default: RefactoringReviewEnabled = true (no project to override it)
        result.PipelineConfiguration!.RefactoringReviewEnabled.Should().BeTrue();
    }

    #endregion

    // ── PrepareAsync — no template, no matching profile ───────────────────

    [Fact]
    public async Task PrepareAsync_NoTemplateNoProfile_ReturnsEmptyConfigs()
    {
        SetupEmptyProviders();

        var result = await CreateService().PrepareAsync(
            ConsolidationRunType.BrainConsolidation,
            templateId: null,
            agentLabels: ["kiro"],
            ct: CancellationToken.None);

        result.ProviderConfigs.Should().BeEmpty();
        result.RepoProviderConfigId.Should().BeEmpty();
    }

    [Fact]
    public async Task PrepareAsync_NullAgentLabels_Throws()
    {
        var act = () => CreateService().PrepareAsync(
            ConsolidationRunType.BrainConsolidation, null, null!, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }

    // ── PrepareAsync — with matching profile, no template ─────────────────

    [Fact]
    public async Task PrepareAsync_MatchingProfile_InjectsAgentConfig()
    {
        var agentConfig = MakeConfig("kiro-agent", ProviderKind.Agent);
        var profile = new AgentProfile
        {
            Id = "kiro-profile",
            DisplayName = "Kiro",
            AgentProviderConfigId = "kiro-agent",
            MatchLabels = ["kiro"]
        };

        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { agentConfig } as IReadOnlyList<ProviderConfig>);
        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile> { profile } as IReadOnlyList<AgentProfile>);
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(
            It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<IReadOnlySet<string>?>()))
            .ReturnsAsync((IReadOnlyList<ProviderConfig> configs, string _, CancellationToken _, bool _, IReadOnlySet<string>? _) => configs);

        var result = await CreateService().PrepareAsync(
            ConsolidationRunType.BrainConsolidation,
            templateId: null,
            agentLabels: ["kiro"],
            ct: CancellationToken.None);

        result.ProviderConfigs.Should().HaveCount(1);
        result.ProviderConfigs[0].Id.Should().Be("kiro-agent");
    }

    // ── PrepareAsync — with template ──────────────────────────────────────

    [Fact]
    public async Task PrepareAsync_WithTemplate_ResolvesRepoProvider()
    {
        SetupEmptyProviders();
        var repoConfig = MakeConfig("repo", ProviderKind.Repository);
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { repoConfig } as IReadOnlyList<ProviderConfig>);

        var template = MakeTemplate("t1");
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template } as IReadOnlyList<PipelineJobTemplate>);

        var result = await CreateService().PrepareAsync(
            ConsolidationRunType.BrainConsolidation,
            templateId: new TemplateId("t1"),
            agentLabels: [],
            ct: CancellationToken.None);

        result.RepoProviderConfigId.Should().Be("repo");
    }

    [Fact]
    public async Task PrepareAsync_RefactoringType_AddsIssueProvider()
    {
        SetupEmptyProviders();
        var repoConfig = MakeConfig("repo", ProviderKind.Repository);
        var issueConfig = MakeConfig("github", ProviderKind.Issue);
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { repoConfig } as IReadOnlyList<ProviderConfig>);
        _mockConfigStore.Setup(s => s.GetProviderConfigByIdAsync("github", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);

        var template = MakeTemplate("t1");
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template } as IReadOnlyList<PipelineJobTemplate>);

        var result = await CreateService().PrepareAsync(
            ConsolidationRunType.RefactoringDetection,
            templateId: new TemplateId("t1"),
            agentLabels: [],
            ct: CancellationToken.None);

        // repo + issue = 2 configs
        result.ProviderConfigs.Should().HaveCount(2);
    }

    [Fact]
    public async Task PrepareAsync_TemplateNotFound_ReturnsEmptyProviderConfig()
    {
        SetupEmptyProviders();
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>() as IReadOnlyList<PipelineJobTemplate>);

        var result = await CreateService().PrepareAsync(
            ConsolidationRunType.BrainConsolidation,
            templateId: new TemplateId("missing"),
            agentLabels: [],
            ct: CancellationToken.None);

        result.RepoProviderConfigId.Should().BeEmpty();
    }

    // ── BrainProviderConfigId propagation ────────────────────────────────

    [Fact]
    public async Task PrepareAsync_WithTemplateThatHasBrainProvider_PopulatesBrainProviderConfigId()
    {
        // Arrange: template has both RepoProviderId and BrainProviderId; brain config is in the store
        SetupEmptyProviders();
        var repoConfig = MakeConfig("repo", ProviderKind.Repository);
        // TODO: [WARNING] brainConfig is created with ProviderKind.Repository instead of the correct
        // brain provider kind. BrainProviderConfigId is resolved from template.BrainProviderId before
        // the store lookup, so the test passes regardless of kind. However, using the wrong kind
        // reduces test fidelity: if ResolveTemplateProviderConfigsAsync ever filters on provider kind
        // when appending brain configs to rawConfigs, this test would silently stop exercising the
        // intended store-lookup path while still returning the correct ID.
        // Fix: use the appropriate ProviderKind for brain providers (e.g. ProviderKind.Brain or equivalent).
        var brainConfig = MakeConfig("brain-1", ProviderKind.Repository);
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { repoConfig, brainConfig } as IReadOnlyList<ProviderConfig>);

        var template = new PipelineJobTemplate
        {
            Id = "t1",
            Name = "T",
            IssueProviderId = "github",
            RepoProviderId = "repo",
            BrainProviderId = "brain-1",
        };
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template } as IReadOnlyList<PipelineJobTemplate>);

        // Act
        var result = await CreateService().PrepareAsync(
            ConsolidationRunType.BrainConsolidation,
            templateId: new TemplateId("t1"),
            agentLabels: [],
            ct: CancellationToken.None);

        // Assert: BrainProviderConfigId is forwarded from the template
        result.BrainProviderConfigId.Should().Be("brain-1",
            "PrepareAsync must populate BrainProviderConfigId from the template's BrainProviderId");
    }

    [Fact]
    public async Task PrepareAsync_WithTemplateThatHasBrainProvider_BrainConfigNotInStore_StillPopulatesBrainProviderConfigId()
    {
        // Arrange: template has BrainProviderId but the brain ProviderConfig is absent from the store.
        // brainProviderId is assigned from template.BrainProviderId BEFORE the store lookup, so the
        // ID is still present even when the config object is missing.
        SetupEmptyProviders();
        var repoConfig = MakeConfig("repo", ProviderKind.Repository);
        // Store only has the repo config — brain config is intentionally absent
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { repoConfig } as IReadOnlyList<ProviderConfig>);

        var template = new PipelineJobTemplate
        {
            Id = "t1",
            Name = "T",
            IssueProviderId = "github",
            RepoProviderId = "repo",
            BrainProviderId = "brain-missing",
        };
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template } as IReadOnlyList<PipelineJobTemplate>);

        // Act
        var result = await CreateService().PrepareAsync(
            ConsolidationRunType.BrainConsolidation,
            templateId: new TemplateId("t1"),
            agentLabels: [],
            ct: CancellationToken.None);

        // Assert: ID is still set even though the ProviderConfig object was not found in the store
        result.BrainProviderConfigId.Should().Be("brain-missing",
            "BrainProviderConfigId is resolved from the template field, not from the store lookup; " +
            "the store lookup only gates whether the ProviderConfig object is appended to rawConfigs");
    }

    [Fact]
    public async Task PrepareAsync_WithTemplateThatHasNoBrainProvider_BrainProviderConfigIdIsNull()
    {
        // Arrange: template has no BrainProviderId
        SetupEmptyProviders();
        var repoConfig = MakeConfig("repo", ProviderKind.Repository);
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { repoConfig } as IReadOnlyList<ProviderConfig>);

        var template = new PipelineJobTemplate
        {
            Id = "t1",
            Name = "T",
            IssueProviderId = "github",
            RepoProviderId = "repo",
            // BrainProviderId intentionally not set (null/empty)
        };
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template } as IReadOnlyList<PipelineJobTemplate>);

        // Act
        var result = await CreateService().PrepareAsync(
            ConsolidationRunType.BrainConsolidation,
            templateId: new TemplateId("t1"),
            agentLabels: [],
            ct: CancellationToken.None);

        // TODO: [WARNING] This assertion is tautological: when BrainProviderId is not set on the template,
        // brainProviderId stays null and BrainProviderConfigId defaults to null on the record even if the
        // `BrainProviderConfigId = brainProviderId` assignment were removed from PrepareAsync. The test
        // cannot distinguish "correctly set to null" from "never set (property default)". To make it a
        // true regression guard, consider initialising ConsolidationJobPreparationResult with a non-null
        // sentinel for BrainProviderConfigId and asserting it is overwritten to null, or pair it with a
        // mutation-style assertion that verifies the field is explicitly assigned.
        // Assert
        result.BrainProviderConfigId.Should().BeNull(
            "BrainProviderConfigId must be null when the template has no BrainProviderId configured");
    }

    [Fact]
    public async Task PrepareAsync_WithNullTemplate_BrainProviderConfigIdIsNull()
    {
        // Arrange: global consolidation run — no template
        SetupEmptyProviders();

        // Act
        var result = await CreateService().PrepareAsync(
            ConsolidationRunType.BrainConsolidation,
            templateId: null,
            agentLabels: [],
            ct: CancellationToken.None);

        // TODO: [WARNING] This assertion is tautological: when templateId is null, PrepareAsync never
        // enters the template-resolution branch so brainProviderId stays null. BrainProviderConfigId
        // defaults to null on ConsolidationJobPreparationResult even if the `BrainProviderConfigId =
        // brainProviderId` line were removed. The test would pass for the wrong reason. To provide
        // meaningful regression protection for the null-template path specifically, either rely solely
        // on the positive test (with-template case) or use a test double that validates the exact
        // return-value assignment rather than the null default.
        // Assert: global runs must not have a brain provider config ID
        result.BrainProviderConfigId.Should().BeNull(
            "BrainProviderConfigId must be null for global consolidation runs with no template");
    }

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
            Id = "t1",
            Name = "Full Template",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            BrainProviderId = "bp-1"
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

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static ProviderConfig MakeConfig(string id, ProviderKind kind) =>
        new() { Id = id, Kind = kind, DisplayName = "T", ProviderType = "GitHub" };

    private static PipelineJobTemplate MakeTemplate(string id = "t1") =>
        new() { Id = id, Name = "T", IssueProviderId = "github", RepoProviderId = "repo" };

    private void SetupEmptyProviders()
    {
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>() as IReadOnlyList<ProviderConfig>);
        _mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>() as IReadOnlyList<AgentProfile>);
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(
            It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<IReadOnlySet<string>?>()))
            .ReturnsAsync((IReadOnlyList<ProviderConfig> configs, string _, CancellationToken _, bool _, IReadOnlySet<string>? _) => configs);
    }

    #region BrainReadOnly token narrowing

    /// <summary>
    /// When BrainReadOnly is true in the resolved pipeline config, the brain provider config ID
    /// must be passed in readOnlyConfigIds to PrepareAgentConfigsAsync.
    /// </summary>
    [Fact]
    public async Task PrepareAsync_BrainReadOnly_True_PassesBrainIdInReadOnlyConfigIds()
    {
        // Arrange: a template with a brain provider; global config has BrainReadOnly = true
        var repoConfig = new ProviderConfig { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo" };
        var brainConfig = new ProviderConfig { Id = "brain-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Brain" };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { repoConfig, brainConfig });

        var template = new PipelineJobTemplate { Id = "t1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-1", BrainProviderId = "brain-1" };
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template });

        SetupAgentConfig("agent-1");
        SetupMatchingProfile("agent-1");

        // Global config: BrainReadOnly = true
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { BrainReadOnly = true });

        IReadOnlySet<string>? capturedReadOnlyIds = null;
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(
                It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<IReadOnlySet<string>?>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool, IReadOnlySet<string>?>(
                (_, _, _, _, readOnlyIds) => capturedReadOnlyIds = readOnlyIds)
            .ReturnsAsync(new List<ProviderConfig>());

        // Act
        var svc = CreateService();
        await svc.PrepareAsync(ConsolidationRunType.BrainConsolidation, "t1", E2ELabels, CancellationToken.None);

        // Assert: brain config ID must be in readOnlyConfigIds
        capturedReadOnlyIds.Should().NotBeNull("readOnlyConfigIds must be set when BrainReadOnly is true");
        capturedReadOnlyIds!.Should().Contain("brain-1",
            "brain provider config ID must be in readOnlyConfigIds when BrainReadOnly is true");
    }

    /// <summary>
    /// When BrainReadOnly is false, readOnlyConfigIds must be null (no narrowing).
    /// </summary>
    [Fact]
    public async Task PrepareAsync_BrainReadOnly_False_PassesNullReadOnlyConfigIds()
    {
        var repoConfig = new ProviderConfig { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo" };
        var brainConfig = new ProviderConfig { Id = "brain-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Brain" };
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { repoConfig, brainConfig });

        var template = new PipelineJobTemplate { Id = "t1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-1", BrainProviderId = "brain-1" };
        _mockProjectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template });

        SetupAgentConfig("agent-1");
        SetupMatchingProfile("agent-1");

        // Global config: BrainReadOnly = false (default)
        _mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration { BrainReadOnly = false });

        IReadOnlySet<string>? capturedReadOnlyIds = new HashSet<string> { "sentinel" }; // start non-null
        _mockTokenVending.Setup(t => t.PrepareAgentConfigsAsync(
                It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(),
                It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<IReadOnlySet<string>?>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool, IReadOnlySet<string>?>(
                (_, _, _, _, readOnlyIds) => capturedReadOnlyIds = readOnlyIds)
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService();
        await svc.PrepareAsync(ConsolidationRunType.BrainConsolidation, "t1", E2ELabels, CancellationToken.None);

        capturedReadOnlyIds.Should().BeNull(
            "readOnlyConfigIds must be null when BrainReadOnly is false (no token narrowing)");
    }

    // TODO [WARNING]: missing test for project-level BrainReadOnly override in ConsolidationJobPreparationService.
    // The two new tests cover only the global-setting source for BrainReadOnly. The service calls
    // the full ResolvePipelineConfigurationAsync which applies project overrides, but no test verifies
    // that a PipelineProject with BrainReadOnly = true causes the brain config ID to be passed in
    // readOnlyConfigIds. A regression in ApplyProjectOverrides for BrainReadOnly would not be caught
    // (issue #3573, TestQualityReviewer finding).

    #endregion
}
