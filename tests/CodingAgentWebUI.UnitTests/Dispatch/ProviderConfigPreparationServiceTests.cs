using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;
using Serilog;

namespace CodingAgentWebUI.UnitTests.Dispatch;

/// <summary>
/// Unit tests for <see cref="ProviderConfigPreparationService"/>.
/// Verifies provider config resolution and token vending in isolation.
/// </summary>
public class ProviderConfigPreparationServiceTests
{
    private readonly Mock<IProviderConfigStore> _mockStore = new();
    private readonly Mock<ITokenVendingService> _mockTokenVending = new();
    private readonly Mock<ILogger> _mockLogger = new();
    private readonly ProviderConfigPreparationService _service;

    private static readonly ProviderConfig TestRepoConfig = new()
    {
        Id = "repo-1",
        Kind = ProviderKind.Repository,
        DisplayName = "TestRepo",
        ProviderType = "GitHub",
        Settings = new Dictionary<string, string> { ["owner"] = "test", ["repo"] = "test-repo" }
    };

    private static readonly ProviderConfig TestAgentConfig = new()
    {
        Id = "agent-1",
        Kind = ProviderKind.Agent,
        DisplayName = "TestAgent",
        ProviderType = "KiroCli",
        Settings = new Dictionary<string, string> { ["endpoint"] = "http://localhost" }
    };

    private static readonly ProviderConfig TestBrainConfig = new()
    {
        Id = "brain-1",
        Kind = ProviderKind.Repository,
        DisplayName = "BrainRepo",
        ProviderType = "GitHub",
        Settings = new Dictionary<string, string> { ["owner"] = "test", ["repo"] = "brain-repo" }
    };

    private static readonly ProviderConfig TestPipelineConfig = new()
    {
        Id = "pipeline-1",
        Kind = ProviderKind.Pipeline,
        DisplayName = "TestPipeline",
        ProviderType = "GitHubActions",
        Settings = new Dictionary<string, string> { ["workflow"] = "ci.yml" }
    };

    private static readonly ProviderConfig TestAdditionalRepoConfig = new()
    {
        Id = "repo-2",
        Kind = ProviderKind.Repository,
        DisplayName = "AdditionalRepo",
        ProviderType = "GitHub",
        Settings = new Dictionary<string, string> { ["owner"] = "test", ["repo"] = "other-repo" }
    };

    public ProviderConfigPreparationServiceTests()
    {
        _service = new ProviderConfigPreparationService(
            _mockStore.Object,
            _mockTokenVending.Object,
            _mockLogger.Object);

        // Default: token vending passes through configs unchanged
        // TODO: Add a test where token vending returns a *different* list to confirm the service
        // returns the token-vended result, not the raw configs. Current pass-through masks integration behavior.
        _mockTokenVending
            .Setup(t => t.PrepareAgentConfigsAsync(
                It.IsAny<IReadOnlyList<ProviderConfig>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>()))
            .ReturnsAsync((IReadOnlyList<ProviderConfig> configs, string _, CancellationToken _, bool _) => configs);
    }

    private void SetupRepoConfigs(params ProviderConfig[] configs)
    {
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(configs.ToList().AsReadOnly());
    }

    private void SetupAgentConfigs(params ProviderConfig[] configs)
    {
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(configs.ToList().AsReadOnly());
    }

    private void SetupPipelineConfigs(params ProviderConfig[] configs)
    {
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Pipeline, It.IsAny<CancellationToken>()))
            .ReturnsAsync(configs.ToList().AsReadOnly());
    }

    // ── Basic Resolution ──

    [Fact]
    public async Task PrepareProviderConfigsAsync_ResolvesRepoAndAgentConfigs()
    {
        SetupRepoConfigs(TestRepoConfig);
        SetupAgentConfigs(TestAgentConfig);

        var result = await _service.PrepareProviderConfigsAsync(
            "repo-1", "agent-1", brainProviderId: null, pipelineProviderId: null, CancellationToken.None);

        result.Should().HaveCount(2);
        result[0].Should().BeSameAs(TestRepoConfig);
        result[1].Should().BeSameAs(TestAgentConfig);
    }

    // ── Brain Config ──

    [Fact]
    public async Task PrepareProviderConfigsAsync_IncludesBrainConfig_WhenProvided()
    {
        SetupRepoConfigs(TestRepoConfig, TestBrainConfig);
        SetupAgentConfigs(TestAgentConfig);

        var result = await _service.PrepareProviderConfigsAsync(
            "repo-1", "agent-1", brainProviderId: "brain-1", pipelineProviderId: null, CancellationToken.None);

        result.Should().HaveCount(3);
        result[0].Should().BeSameAs(TestRepoConfig);
        result[1].Should().BeSameAs(TestAgentConfig);
        result[2].Should().BeSameAs(TestBrainConfig);
    }

    [Fact]
    // TODO: This test covers "brain ID provided but not resolvable" (not found in cache or DB).
    // The branch where brainProviderId is null/empty (skipping brain entirely) is only implicitly
    // tested via other tests that pass null. Consider adding an explicit test for null/empty brain ID.
    public async Task PrepareProviderConfigsAsync_SkipsBrainConfig_WhenNotFound()
    {
        SetupRepoConfigs(TestRepoConfig); // brain-1 not in list
        SetupAgentConfigs(TestAgentConfig);
        _mockStore.Setup(s => s.GetProviderConfigByIdAsync("brain-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var result = await _service.PrepareProviderConfigsAsync(
            "repo-1", "agent-1", brainProviderId: "brain-1", pipelineProviderId: null, CancellationToken.None);

        result.Should().HaveCount(2); // only repo + agent
    }

    // ── Pipeline Config ──

    [Fact]
    public async Task PrepareProviderConfigsAsync_IncludesPipelineConfig_WhenProvided()
    {
        SetupRepoConfigs(TestRepoConfig);
        SetupAgentConfigs(TestAgentConfig);
        SetupPipelineConfigs(TestPipelineConfig);

        var result = await _service.PrepareProviderConfigsAsync(
            "repo-1", "agent-1", brainProviderId: null, pipelineProviderId: "pipeline-1", CancellationToken.None);

        result.Should().HaveCount(3);
        result[0].Should().BeSameAs(TestRepoConfig);
        result[1].Should().BeSameAs(TestAgentConfig);
        result[2].Should().BeSameAs(TestPipelineConfig);
    }

    // ── Additional Repo Configs ──

    [Fact]
    public async Task PrepareProviderConfigsAsync_IncludesAdditionalRepoConfigs()
    {
        SetupRepoConfigs(TestRepoConfig, TestAdditionalRepoConfig);
        SetupAgentConfigs(TestAgentConfig);

        var result = await _service.PrepareProviderConfigsAsync(
            "repo-1", "agent-1", brainProviderId: null, pipelineProviderId: null, CancellationToken.None,
            additionalRepoProviderIds: new[] { "repo-2" });

        result.Should().HaveCount(3);
        result[0].Should().BeSameAs(TestRepoConfig);
        result[1].Should().BeSameAs(TestAdditionalRepoConfig);
        result[2].Should().BeSameAs(TestAgentConfig);
    }

    [Fact]
    public async Task PrepareProviderConfigsAsync_SkipsDuplicateAdditionalRepoIds()
    {
        SetupRepoConfigs(TestRepoConfig, TestAdditionalRepoConfig);
        SetupAgentConfigs(TestAgentConfig);

        var result = await _service.PrepareProviderConfigsAsync(
            "repo-1", "agent-1", brainProviderId: null, pipelineProviderId: null, CancellationToken.None,
            additionalRepoProviderIds: new[] { "repo-1", "repo-2", "repo-2" }); // repo-1 is primary, repo-2 duplicated

        result.Should().HaveCount(3); // repo-1, repo-2, agent-1 (no duplicates)
    }

    [Fact]
    public async Task PrepareProviderConfigsAsync_SkipsEmptyAndNullAdditionalRepoIds()
    {
        SetupRepoConfigs(TestRepoConfig, TestAdditionalRepoConfig);
        SetupAgentConfigs(TestAgentConfig);

        var result = await _service.PrepareProviderConfigsAsync(
            "repo-1", "agent-1", brainProviderId: null, pipelineProviderId: null, CancellationToken.None,
            additionalRepoProviderIds: new[] { "", null!, "repo-2" });

        result.Should().HaveCount(3); // repo-1, repo-2, agent-1
    }

    // ── Token Vending ──

    [Fact]
    public async Task PrepareProviderConfigsAsync_CallsTokenVending_WithCorrectRepoProviderId()
    {
        SetupRepoConfigs(TestRepoConfig);
        SetupAgentConfigs(TestAgentConfig);

        await _service.PrepareProviderConfigsAsync(
            "repo-1", "agent-1", brainProviderId: null, pipelineProviderId: null, CancellationToken.None);

        _mockTokenVending.Verify(t => t.PrepareAgentConfigsAsync(
            It.IsAny<IReadOnlyList<ProviderConfig>>(),
            "repo-1",
            It.IsAny<CancellationToken>(),
            false), Times.Once);
    }

    // ── Error Handling ──

    [Fact]
    public async Task PrepareProviderConfigsAsync_Throws_WhenRepoConfigNotFound()
    {
        SetupRepoConfigs(); // empty list
        _mockStore.Setup(s => s.GetProviderConfigByIdAsync("repo-missing", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);
        SetupAgentConfigs(TestAgentConfig);

        var act = () => _service.PrepareProviderConfigsAsync(
            "repo-missing", "agent-1", brainProviderId: null, pipelineProviderId: null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task PrepareProviderConfigsAsync_Throws_WhenAgentConfigNotFound()
    {
        SetupRepoConfigs(TestRepoConfig);
        SetupAgentConfigs(); // empty list
        _mockStore.Setup(s => s.GetProviderConfigByIdAsync("agent-missing", ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var act = () => _service.PrepareProviderConfigsAsync(
            "repo-1", "agent-missing", brainProviderId: null, pipelineProviderId: null, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Config Order ──

    [Fact]
    public async Task PrepareProviderConfigsAsync_PreservesConfigOrder_RepoAdditionalAgentBrainPipeline()
    {
        SetupRepoConfigs(TestRepoConfig, TestAdditionalRepoConfig, TestBrainConfig);
        SetupAgentConfigs(TestAgentConfig);
        SetupPipelineConfigs(TestPipelineConfig);

        var result = await _service.PrepareProviderConfigsAsync(
            "repo-1", "agent-1", brainProviderId: "brain-1", pipelineProviderId: "pipeline-1", CancellationToken.None,
            additionalRepoProviderIds: new[] { "repo-2" });

        result.Should().HaveCount(5);
        result[0].Id.Should().Be("repo-1");      // primary repo
        result[1].Id.Should().Be("repo-2");      // additional repo
        result[2].Id.Should().Be("agent-1");     // agent
        result[3].Id.Should().Be("brain-1");     // brain
        result[4].Id.Should().Be("pipeline-1"); // pipeline
    }

    // ── Construction ──

    [Fact]
    public void Constructor_NullStore_Throws()
    {
        var act = () => new ProviderConfigPreparationService(null!, _mockTokenVending.Object, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullTokenVending_Throws()
    {
        var act = () => new ProviderConfigPreparationService(_mockStore.Object, null!, _mockLogger.Object);
        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLogger_Throws()
    {
        var act = () => new ProviderConfigPreparationService(_mockStore.Object, _mockTokenVending.Object, null!);
        act.Should().Throw<ArgumentNullException>();
    }
}
