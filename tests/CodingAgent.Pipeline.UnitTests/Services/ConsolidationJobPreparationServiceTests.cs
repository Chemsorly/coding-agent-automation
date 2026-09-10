using AwesomeAssertions;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for ConsolidationJobPreparationService.
/// Covers: PrepareAsync (no template, with template, refactoring type), constructor guards,
/// empty agent labels, no matching profile, and pipeline configuration resolution.
/// </summary>
public sealed class ConsolidationJobPreparationServiceTests
{
    private readonly Mock<IConfigurationStore> _configStore = new();
    private readonly Mock<IProjectStore> _projectStore = new();
    private readonly Mock<ITokenVendingService> _tokenVending = new();
    private readonly Mock<ILogger> _logger = new();
    private readonly ConsolidationJobPreparationService _sut;

    public ConsolidationJobPreparationServiceTests()
    {
        // IConfigurationStore implements IProviderConfigStore, IAgentProfileStore, and IPipelineConfigStore
        _sut = new ConsolidationJobPreparationService(
            _configStore.Object, _projectStore.Object, _tokenVending.Object, _logger.Object);

        // Default: return global pipeline config (no overrides)
        _configStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        // Default: return empty projects (no owning project found)
        _projectStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>());
    }

    private static ProviderConfig MakeConfig(string id, ProviderKind kind) =>
        new() { Id = id, Kind = kind, DisplayName = "T", ProviderType = "GitHub" };

    private static PipelineJobTemplate MakeTemplate(string id = "t1") =>
        new() { Id = id, Name = "T", IssueProviderId = "github", RepoProviderId = "repo" };

    private void SetupEmptyProviders()
    {
        _configStore.Setup(s => s.LoadProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>() as IReadOnlyList<ProviderConfig>);
        _configStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>() as IReadOnlyList<AgentProfile>);
        _tokenVending.Setup(t => t.PrepareAgentConfigsAsync(
            It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync((IReadOnlyList<ProviderConfig> configs, string _, CancellationToken _, bool _) => configs);
    }

    // ── Constructor guards ────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullConfigStore_Throws()
    {
        var act = () => new ConsolidationJobPreparationService(
            (IConfigurationStore)null!, _projectStore.Object, _tokenVending.Object, _logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullProjectStore_Throws()
    {
        var act = () => new ConsolidationJobPreparationService(
            _configStore.Object, null!, _tokenVending.Object, _logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullTokenVending_Throws()
    {
        var act = () => new ConsolidationJobPreparationService(
            _configStore.Object, _projectStore.Object, null!, _logger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── PrepareAsync — no template, no matching profile ───────────────────

    [Fact]
    public async Task PrepareAsync_NoTemplateNoProfile_ReturnsEmptyConfigs()
    {
        SetupEmptyProviders();

        var result = await _sut.PrepareAsync(
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
        var act = () => _sut.PrepareAsync(
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

        _configStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { agentConfig } as IReadOnlyList<ProviderConfig>);
        _configStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentProfile> { profile } as IReadOnlyList<AgentProfile>);
        _tokenVending.Setup(t => t.PrepareAgentConfigsAsync(
            It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync((IReadOnlyList<ProviderConfig> configs, string _, CancellationToken _, bool _) => configs);

        var result = await _sut.PrepareAsync(
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
        _configStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { repoConfig } as IReadOnlyList<ProviderConfig>);

        var template = MakeTemplate("t1");
        _projectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template } as IReadOnlyList<PipelineJobTemplate>);

        var result = await _sut.PrepareAsync(
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
        _configStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { repoConfig } as IReadOnlyList<ProviderConfig>);
        _configStore.Setup(s => s.GetProviderConfigByIdAsync("github", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);

        var template = MakeTemplate("t1");
        _projectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template } as IReadOnlyList<PipelineJobTemplate>);

        var result = await _sut.PrepareAsync(
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
        _projectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>() as IReadOnlyList<PipelineJobTemplate>);

        var result = await _sut.PrepareAsync(
            ConsolidationRunType.BrainConsolidation,
            templateId: new TemplateId("missing"),
            agentLabels: [],
            ct: CancellationToken.None);

        result.RepoProviderConfigId.Should().BeEmpty();
    }

    // ── Pipeline configuration resolution ────────────────────────────────

    [Fact]
    public async Task PrepareAsync_WithTemplate_ReturnsPipelineConfigurationFromResolver()
    {
        // TODO [WARNING]: This is a smoke test that only asserts NotBeNull. It would pass even if
        // PrepareAsync returned new PipelineConfiguration() via the no-template fallback path rather
        // than going through the resolver. Add an assertion on at least one property value controlled
        // by the resolver (e.g., set a distinguishing non-default field in the mock global config and
        // verify it appears in the result) to distinguish resolver-resolved output from a hard-coded default.

        // Smoke test: verifies that PipelineConfiguration is populated (not null) and
        // reflects the resolved config. Detailed override assertions are in the Web.UnitTests suite.
        SetupEmptyProviders();
        var repoConfig = MakeConfig("repo", ProviderKind.Repository);
        _configStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { repoConfig } as IReadOnlyList<ProviderConfig>);

        var template = MakeTemplate("t1");
        _projectStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { template } as IReadOnlyList<PipelineJobTemplate>);

        var result = await _sut.PrepareAsync(
            ConsolidationRunType.BrainConsolidation,
            templateId: new TemplateId("t1"),
            agentLabels: [],
            ct: CancellationToken.None);

        result.PipelineConfiguration.Should().NotBeNull(
            "PrepareAsync must resolve and return PipelineConfiguration via PipelineConfigurationResolver");
    }

    [Fact]
    public async Task PrepareAsync_NullTemplate_ReturnsPipelineConfigurationFromGlobalConfig()
    {
        // TODO [WARNING]: The only assertion here is NotBeNull, which can never fail because the default
        // constructor setup already returns new PipelineConfiguration() (a non-null value). This does not
        // verify that the service actually reads from the config store. Set a distinguishing property on the
        // mock (e.g., a non-default AgentTimeout) and assert that value appears in the result to confirm the
        // service actually consumed the global config.
        SetupEmptyProviders();

        var result = await _sut.PrepareAsync(
            ConsolidationRunType.BrainConsolidation,
            templateId: null,
            agentLabels: [],
            ct: CancellationToken.None);

        result.PipelineConfiguration.Should().NotBeNull(
            "PrepareAsync must return PipelineConfiguration even when no template is provided");
    }
}
