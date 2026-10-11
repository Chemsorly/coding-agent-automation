using AwesomeAssertions;
using Moq;
using CodingAgent.Orchestration;
using CodingAgent.Orchestration.Dispatch;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Serilog;

namespace CodingAgent.Web.UnitTests.Dispatch;

/// <summary>
/// Unit tests for provider config building and token vending logic
/// on <see cref="DispatchInfrastructure"/>.
/// </summary>
public class ProviderConfigBuilderTests
{
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();
    private readonly Mock<ITokenVendingService> _mockTokenVending = new();
    private readonly Mock<IProviderFactory> _mockProviderFactory = new();
    private readonly Mock<ILabelService> _mockLabelService = new();
    private readonly ILogger _logger = new Mock<ILogger>().Object;
    private readonly DispatchInfrastructure _infra;

    private const string RepoProviderId = "repo-1";
    private const string AgentProviderId = "agent-1";
    private const string BrainProviderId = "brain-1";
    private const string PipelineProviderId = "pipeline-1";

    public ProviderConfigBuilderTests()
    {
        var resolution = new DispatchResolutionService(
            new QualityGateResolver(),
            new ReviewerResolver(),
            _mockConfigStore.Object);

        _infra = new DispatchInfrastructure(
            _mockTokenVending.Object,
            _mockProviderFactory.Object,
            _mockLabelService.Object,
            resolution);
    }

    private static ProviderConfig CreateConfig(string id, ProviderKind kind) => new()
    {
        Id = id,
        Kind = kind,
        ProviderType = "test",
        DisplayName = id,
        Settings = new Dictionary<string, string>()
    };

    private void SetupConfigStore()
    {
        var repoConfig = CreateConfig(RepoProviderId, ProviderKind.Repository);
        var agentConfig = CreateConfig(AgentProviderId, ProviderKind.Agent);
        var brainConfig = CreateConfig(BrainProviderId, ProviderKind.Repository);
        var pipelineConfig = CreateConfig(PipelineProviderId, ProviderKind.Pipeline);

        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { repoConfig, brainConfig });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { agentConfig });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Pipeline, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { pipelineConfig });
    }

    // ── BuildAgentProviderConfigsAsync ──

    [Fact]
    public async Task BuildAsync_WithRepoAndAgent_ReturnsBothConfigs()
    {
        SetupConfigStore();

        var result = await _infra.BuildAgentProviderConfigsAsync(
            RepoProviderId, AgentProviderId, null, null, _logger, CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].Id.Should().Be(RepoProviderId);
        result[1].Id.Should().Be(AgentProviderId);
    }

    [Fact]
    public async Task BuildAsync_WithBrainProvider_IncludesBrainConfig()
    {
        SetupConfigStore();

        var result = await _infra.BuildAgentProviderConfigsAsync(
            RepoProviderId, AgentProviderId, BrainProviderId, null, _logger, CancellationToken.None);

        result.Should().HaveCount(3);
        result.Should().Contain(c => c.Id == BrainProviderId);
    }

    [Fact]
    public async Task BuildAsync_WithPipelineProvider_IncludesPipelineConfig()
    {
        SetupConfigStore();

        var result = await _infra.BuildAgentProviderConfigsAsync(
            RepoProviderId, AgentProviderId, null, PipelineProviderId, _logger, CancellationToken.None);

        result.Should().HaveCount(3);
        result.Should().Contain(c => c.Id == PipelineProviderId);
    }

    [Fact]
    public async Task BuildAsync_WithAllProviders_ReturnsAllFourConfigs()
    {
        SetupConfigStore();

        var result = await _infra.BuildAgentProviderConfigsAsync(
            RepoProviderId, AgentProviderId, BrainProviderId, PipelineProviderId, _logger, CancellationToken.None);

        result.Should().HaveCount(4);
        result[0].Id.Should().Be(RepoProviderId);
        result[1].Id.Should().Be(AgentProviderId);
        result[2].Id.Should().Be(BrainProviderId);
        result[3].Id.Should().Be(PipelineProviderId);
    }

    // ── PrepareProviderConfigsAsync — a project epic's other repositories are clone-only ──

    /// <summary>Token vending that passes configs through and records what was prepared how.</summary>
    private (List<ProviderConfig> JobConfigs, List<ProviderConfig> CloneConfigs) CapturePreparedConfigs()
    {
        var jobConfigs = new List<ProviderConfig>();
        var cloneConfigs = new List<ProviderConfig>();
        _mockTokenVending
            .Setup(t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<string>(), It.IsAny<CancellationToken>(), It.IsAny<bool>(), It.IsAny<IReadOnlySet<string>?>()))
            .ReturnsAsync((IReadOnlyList<ProviderConfig> configs, string _, CancellationToken _, bool _, IReadOnlySet<string>? _) => { jobConfigs.AddRange(configs); return configs; });
        _mockTokenVending
            .Setup(t => t.PrepareReadOnlyCloneConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<ProviderConfig> configs, CancellationToken _) => { cloneConfigs.AddRange(configs); return configs; });
        return (jobConfigs, cloneConfigs);
    }

    [Fact]
    public async Task PrepareAsync_WithAdditionalRepoProviderIds_PreparesThemAsCloneOnlyConfigs()
    {
        var additionalRepoConfig = CreateConfig("repo-2", ProviderKind.Repository);
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { CreateConfig(RepoProviderId, ProviderKind.Repository), additionalRepoConfig });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { CreateConfig(AgentProviderId, ProviderKind.Agent) });
        var (jobConfigs, cloneConfigs) = CapturePreparedConfigs();

        var result = await _infra.PrepareProviderConfigsAsync(
            RepoProviderId, AgentProviderId, null, null, _logger, CancellationToken.None,
            additionalRepoProviderIds: new[] { "repo-2" });

        jobConfigs.Select(c => c.Id).Should().Equal(RepoProviderId, AgentProviderId);
        cloneConfigs.Select(c => c.Id).Should().Equal("repo-2");
        result.Select(c => c.Id).Should().Equal(RepoProviderId, AgentProviderId, "repo-2");
    }

    [Fact]
    public async Task PrepareAsync_AdditionalIds_SkipThePrimaryRepoTheBrainDuplicatesAndEmptyIds()
    {
        SetupConfigStore();
        var (_, cloneConfigs) = CapturePreparedConfigs();

        await _infra.PrepareProviderConfigsAsync(
            RepoProviderId, AgentProviderId, BrainProviderId, null, _logger, CancellationToken.None,
            additionalRepoProviderIds: new[] { RepoProviderId, BrainProviderId, null!, "", RepoProviderId });

        cloneConfigs.Should().BeEmpty("the job's own repository and brain already have job configs");
    }

    [Fact]
    public async Task PrepareAsync_WithoutAdditionalRepoProviderIds_PreparesNoCloneConfigs()
    {
        SetupConfigStore();
        CapturePreparedConfigs();

        await _infra.PrepareProviderConfigsAsync(
            RepoProviderId, AgentProviderId, null, null, _logger, CancellationToken.None);

        _mockTokenVending.Verify(t => t.PrepareReadOnlyCloneConfigsAsync(
            It.IsAny<IReadOnlyList<ProviderConfig>>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BuildAsync_WithMissingBrainProvider_ExcludesBrainConfig()
    {
        // Setup with no brain config in the store
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { CreateConfig(RepoProviderId, ProviderKind.Repository) });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { CreateConfig(AgentProviderId, ProviderKind.Agent) });
        // Brain not found in cached list, and DB fallback returns null
        _mockConfigStore.Setup(s => s.GetProviderConfigByIdAsync(BrainProviderId, ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var result = await _infra.BuildAgentProviderConfigsAsync(
            RepoProviderId, AgentProviderId, BrainProviderId, null, _logger, CancellationToken.None);

        // Brain is optional (required:false) — should gracefully exclude it
        result.Should().HaveCount(2);
        result.Should().NotContain(c => c.Id == BrainProviderId);
    }

    [Fact]
    public async Task BuildAsync_WithMissingRequiredRepoProvider_ThrowsInvalidOperationException()
    {
        // No configs in store
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());
        _mockConfigStore.Setup(s => s.GetProviderConfigByIdAsync(RepoProviderId, ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var act = () => _infra.BuildAgentProviderConfigsAsync(
            RepoProviderId, AgentProviderId, null, null, _logger, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── PrepareProviderConfigsAsync ──

    // TODO: This test uses It.IsAny<IReadOnlyList<ProviderConfig>>() which doesn't verify the correct
    // raw configs (repo + agent) were passed to PrepareAgentConfigsAsync. If the builder assembled
    // configs incorrectly (wrong order, missing entries), this test would still pass. Consider using
    // a callback capture or It.Is<> matcher to assert the correct configs are forwarded.
    [Fact]
    public async Task PrepareAsync_AppliesTokenVending()
    {
        SetupConfigStore();

        var vendedConfigs = new List<ProviderConfig>
        {
            CreateConfig("vended-repo", ProviderKind.Repository),
            CreateConfig("vended-agent", ProviderKind.Agent)
        }.AsReadOnly();
        _mockTokenVending
            .Setup(t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), RepoProviderId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(vendedConfigs);

        var result = await _infra.PrepareProviderConfigsAsync(
            RepoProviderId, AgentProviderId, null, null, _logger, CancellationToken.None);

        // Should return the token-vended configs, not the raw ones
        result.Should().BeSameAs(vendedConfigs);
        _mockTokenVending.Verify(
            t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), RepoProviderId, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
