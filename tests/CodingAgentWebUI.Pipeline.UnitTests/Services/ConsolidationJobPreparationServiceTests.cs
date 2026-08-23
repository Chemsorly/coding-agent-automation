using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for ConsolidationJobPreparationService.
/// Covers: PrepareAsync (no template, with template, refactoring type), constructor guards,
/// empty agent labels, no matching profile.
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
        // IConfigurationStore implements both IProviderConfigStore and IAgentProfileStore
        _sut = new ConsolidationJobPreparationService(
            _configStore.Object, _projectStore.Object, _tokenVending.Object, _logger.Object);
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
}
