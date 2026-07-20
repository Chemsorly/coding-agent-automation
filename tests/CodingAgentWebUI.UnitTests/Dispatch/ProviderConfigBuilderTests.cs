using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Serilog;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="ProviderConfigBuilder"/> — the shared provider config
/// building and token vending logic extracted from AgentJobDispatcher and DispatchOrchestrationService.
/// </summary>
public class ProviderConfigBuilderTests
{
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();
    private readonly Mock<ITokenVendingService> _mockTokenVending = new();
    private readonly ILogger _logger = new Mock<ILogger>().Object;
    private readonly ProviderConfigBuilder _builder;

    private const string RepoProviderId = "repo-1";
    private const string AgentProviderId = "agent-1";
    private const string BrainProviderId = "brain-1";
    private const string PipelineProviderId = "pipeline-1";

    public ProviderConfigBuilderTests()
    {
        _builder = new ProviderConfigBuilder(_mockConfigStore.Object, _mockTokenVending.Object);
    }

    private ProviderConfig CreateConfig(string id, ProviderKind kind) => new()
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

    // ── Construction ──

    [Fact]
    public void Constructor_NullConfigStore_Throws()
    {
        var act = () => new ProviderConfigBuilder(null!, _mockTokenVending.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullTokenVending_Throws()
    {
        var act = () => new ProviderConfigBuilder(_mockConfigStore.Object, null!);
        act.Should().Throw<ArgumentNullException>();
    }

    // ── BuildAgentProviderConfigsAsync ──

    [Fact]
    public async Task BuildAsync_WithRepoAndAgent_ReturnsBothConfigs()
    {
        SetupConfigStore();

        var result = await _builder.BuildAgentProviderConfigsAsync(
            RepoProviderId, AgentProviderId, null, null, _logger, CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].Id.Should().Be(RepoProviderId);
        result[1].Id.Should().Be(AgentProviderId);
    }

    [Fact]
    public async Task BuildAsync_WithBrainProvider_IncludesBrainConfig()
    {
        SetupConfigStore();

        var result = await _builder.BuildAgentProviderConfigsAsync(
            RepoProviderId, AgentProviderId, BrainProviderId, null, _logger, CancellationToken.None);

        result.Should().HaveCount(3);
        result.Should().Contain(c => c.Id == BrainProviderId);
    }

    [Fact]
    public async Task BuildAsync_WithPipelineProvider_IncludesPipelineConfig()
    {
        SetupConfigStore();

        var result = await _builder.BuildAgentProviderConfigsAsync(
            RepoProviderId, AgentProviderId, null, PipelineProviderId, _logger, CancellationToken.None);

        result.Should().HaveCount(3);
        result.Should().Contain(c => c.Id == PipelineProviderId);
    }

    [Fact]
    public async Task BuildAsync_WithAllProviders_ReturnsAllFourConfigs()
    {
        SetupConfigStore();

        var result = await _builder.BuildAgentProviderConfigsAsync(
            RepoProviderId, AgentProviderId, BrainProviderId, PipelineProviderId, _logger, CancellationToken.None);

        result.Should().HaveCount(4);
        result[0].Id.Should().Be(RepoProviderId);
        result[1].Id.Should().Be(AgentProviderId);
        result[2].Id.Should().Be(BrainProviderId);
        result[3].Id.Should().Be(PipelineProviderId);
    }

    [Fact]
    public async Task BuildAsync_WithAdditionalRepoProviderIds_IncludesAdditionalRepoConfigs()
    {
        var additionalRepoId = "repo-2";
        var additionalRepoConfig = CreateConfig(additionalRepoId, ProviderKind.Repository);

        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { CreateConfig(RepoProviderId, ProviderKind.Repository), additionalRepoConfig });
        _mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { CreateConfig(AgentProviderId, ProviderKind.Agent) });

        var result = await _builder.BuildAgentProviderConfigsAsync(
            RepoProviderId, AgentProviderId, null, null, _logger, CancellationToken.None,
            additionalRepoProviderIds: new[] { additionalRepoId });

        result.Should().HaveCount(3);
        result.Should().Contain(c => c.Id == additionalRepoId);
    }

    [Fact]
    public async Task BuildAsync_WithDuplicateAdditionalRepoId_DeduplicatesCorrectly()
    {
        SetupConfigStore();

        // Pass the primary repo ID again as an additional — should be deduplicated
        var result = await _builder.BuildAgentProviderConfigsAsync(
            RepoProviderId, AgentProviderId, null, null, _logger, CancellationToken.None,
            additionalRepoProviderIds: new[] { RepoProviderId, RepoProviderId });

        // Should only have repo + agent, not duplicated repos
        result.Should().HaveCount(2);
        result.Count(c => c.Id == RepoProviderId).Should().Be(1);
    }

    // TODO: This test name is misleading — production code uses string.IsNullOrEmpty() which does NOT
    // filter whitespace. The whitespace entry "  " is not actually skipped by the guard; it passes
    // through but doesn't match any config in the store (required: false → null). Consider either
    // adding a whitespace guard in production code or renaming this test to reflect the actual behavior.
    [Fact]
    public async Task BuildAsync_WithNullAdditionalRepoId_SkipsNullEntries()
    {
        SetupConfigStore();

        var result = await _builder.BuildAgentProviderConfigsAsync(
            RepoProviderId, AgentProviderId, null, null, _logger, CancellationToken.None,
            additionalRepoProviderIds: new[] { null!, "", "  " });

        // Null/empty entries are skipped — only repo + agent
        result.Should().HaveCount(2);
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

        var result = await _builder.BuildAgentProviderConfigsAsync(
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

        var act = () => _builder.BuildAgentProviderConfigsAsync(
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

        var result = await _builder.PrepareProviderConfigsAsync(
            RepoProviderId, AgentProviderId, null, null, _logger, CancellationToken.None);

        // Should return the token-vended configs, not the raw ones
        result.Should().BeSameAs(vendedConfigs);
        _mockTokenVending.Verify(
            t => t.PrepareAgentConfigsAsync(It.IsAny<IReadOnlyList<ProviderConfig>>(), RepoProviderId, It.IsAny<CancellationToken>()),
            Times.Once);
    }
}
