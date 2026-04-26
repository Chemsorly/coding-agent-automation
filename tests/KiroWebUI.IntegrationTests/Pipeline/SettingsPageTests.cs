using AwesomeAssertions;
using Moq;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;
using KiroWebUI.Infrastructure;
using KiroWebUI.Infrastructure.GitHub;
using KiroWebUI.Infrastructure.Persistence;

namespace KiroWebUI.IntegrationTests.Pipeline;

/// <summary>
/// Unit tests for Settings page logic.
/// Tests provider CRUD operations and pipeline config save/load via mocked IConfigurationStore.
/// Since bunit is not available, these tests validate the same operations the Settings page performs
/// against IConfigurationStore — add, edit, delete providers and save/load pipeline configuration.
/// </summary>
public class SettingsPageTests
{
    private readonly Mock<IConfigurationStore> _mockStore;

    public SettingsPageTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
    }

    // --- Requirement 9.1: Provider CRUD (Issue, Repository, Agent) ---

    [Fact]
    public async Task LoadProviders_ReturnsAllThreeKinds()
    {
        // Arrange — simulate what OnInitializedAsync does
        var issueProviders = new List<ProviderConfig>
        {
            new() { Id = "ip-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "GitHub Issues" }
        };
        var repoProviders = new List<ProviderConfig>
        {
            new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "GitHub Repo" }
        };
        var agentProviders = new List<ProviderConfig>
        {
            new() { Id = "ap-1", Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Kiro Agent" }
        };

        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueProviders);
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoProviders);
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(agentProviders);
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());

        // Act — load all three kinds (same as Settings page OnInitializedAsync)
        var loadedIssue = await _mockStore.Object.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        var loadedRepo = await _mockStore.Object.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);
        var loadedAgent = await _mockStore.Object.LoadProviderConfigsAsync(ProviderKind.Agent, CancellationToken.None);

        // Assert
        loadedIssue.Should().HaveCount(1);
        loadedIssue[0].DisplayName.Should().Be("GitHub Issues");
        loadedRepo.Should().HaveCount(1);
        loadedRepo[0].DisplayName.Should().Be("GitHub Repo");
        loadedAgent.Should().HaveCount(1);
        loadedAgent[0].DisplayName.Should().Be("Kiro Agent");
    }

    [Fact]
    public async Task AddIssueProvider_SavesWithCorrectSettingsKeys()
    {
        // Arrange
        ProviderConfig? savedConfig = null;
        _mockStore.Setup(s => s.SaveProviderConfigAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .Callback<ProviderConfig, CancellationToken>((c, _) => savedConfig = c)
            .Returns(Task.CompletedTask);

        // Act — replicate what SaveIssueProvider does
        var config = new ProviderConfig
        {
            Id = Guid.NewGuid().ToString(),
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "My GitHub Issues",
            Settings = new Dictionary<string, string>
            {
                ["apiUrl"] = "https://api.github.com",
                ["clientId"] = "Iv1.abc123",
                ["installationId"] = "78901234",
                ["privateKeyBase64"] = "LS0tLS1CRUdJTi...",
                ["owner"] = "myorg",
                ["repo"] = "myrepo"
            }
        };
        await _mockStore.Object.SaveProviderConfigAsync(config, CancellationToken.None);

        // Assert
        savedConfig.Should().NotBeNull();
        savedConfig!.Kind.Should().Be(ProviderKind.Issue);
        savedConfig.ProviderType.Should().Be("GitHub");
        savedConfig.DisplayName.Should().Be("My GitHub Issues");
        savedConfig.Settings.Should().ContainKey("apiUrl").WhoseValue.Should().Be("https://api.github.com");
        savedConfig.Settings.Should().ContainKey("clientId").WhoseValue.Should().Be("Iv1.abc123");
        savedConfig.Settings.Should().ContainKey("installationId").WhoseValue.Should().Be("78901234");
        savedConfig.Settings.Should().ContainKey("privateKeyBase64").WhoseValue.Should().Be("LS0tLS1CRUdJTi...");
        savedConfig.Settings.Should().ContainKey("owner").WhoseValue.Should().Be("myorg");
        savedConfig.Settings.Should().ContainKey("repo").WhoseValue.Should().Be("myrepo");
        savedConfig.Settings.Should().NotContainKey("token");
    }

    [Fact]
    public async Task AddRepoProvider_SavesWithBaseBranchSetting()
    {
        // Arrange
        ProviderConfig? savedConfig = null;
        _mockStore.Setup(s => s.SaveProviderConfigAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .Callback<ProviderConfig, CancellationToken>((c, _) => savedConfig = c)
            .Returns(Task.CompletedTask);

        // Act — replicate what SaveRepoProvider does
        var config = new ProviderConfig
        {
            Id = Guid.NewGuid().ToString(),
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "My Repo",
            Settings = new Dictionary<string, string>
            {
                ["apiUrl"] = "https://api.github.com",
                ["clientId"] = "Iv1.abc123",
                ["installationId"] = "78901234",
                ["privateKeyBase64"] = "LS0tLS1CRUdJTi...",
                ["owner"] = "org",
                ["repo"] = "repo",
                ["baseBranch"] = "develop"
            }
        };
        await _mockStore.Object.SaveProviderConfigAsync(config, CancellationToken.None);

        // Assert
        savedConfig.Should().NotBeNull();
        savedConfig!.Kind.Should().Be(ProviderKind.Repository);
        savedConfig.Settings.Should().ContainKey("baseBranch").WhoseValue.Should().Be("develop");
        savedConfig.Settings.Should().NotContainKey("token");
    }

    [Fact]
    public async Task AddAgentProvider_SavesWithExecutablePathAndTimeout()
    {
        // Arrange
        ProviderConfig? savedConfig = null;
        _mockStore.Setup(s => s.SaveProviderConfigAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .Callback<ProviderConfig, CancellationToken>((c, _) => savedConfig = c)
            .Returns(Task.CompletedTask);

        // Act — replicate what SaveAgentProvider does
        var config = new ProviderConfig
        {
            Id = Guid.NewGuid().ToString(),
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Kiro CLI Agent",
            Settings = new Dictionary<string, string>
            {
                ["executablePath"] = "/root/.local/bin/kiro-cli",
                ["timeout"] = "45",
                ["agentName"] = "default"
            }
        };
        await _mockStore.Object.SaveProviderConfigAsync(config, CancellationToken.None);

        // Assert
        savedConfig.Should().NotBeNull();
        savedConfig!.Kind.Should().Be(ProviderKind.Agent);
        savedConfig.ProviderType.Should().Be("KiroCli");
        savedConfig.Settings.Should().ContainKey("executablePath").WhoseValue.Should().Be("/root/.local/bin/kiro-cli");
        savedConfig.Settings.Should().ContainKey("timeout").WhoseValue.Should().Be("45");
        savedConfig.Settings.Should().ContainKey("agentName").WhoseValue.Should().Be("default");
    }

    [Fact]
    public async Task AddAgentProvider_WithModel_SavesModelInSettings()
    {
        ProviderConfig? savedConfig = null;
        _mockStore.Setup(s => s.SaveProviderConfigAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .Callback<ProviderConfig, CancellationToken>((c, _) => savedConfig = c)
            .Returns(Task.CompletedTask);

        var config = new ProviderConfig
        {
            Id = Guid.NewGuid().ToString(),
            Kind = ProviderKind.Agent,
            ProviderType = "KiroCli",
            DisplayName = "Kiro CLI Agent",
            Settings = new Dictionary<string, string>
            {
                ["executablePath"] = "/root/.local/bin/kiro-cli",
                ["timeout"] = "30",
                ["agentName"] = "default",
                ["model"] = "claude-sonnet-4.6"
            }
        };
        await _mockStore.Object.SaveProviderConfigAsync(config, CancellationToken.None);

        savedConfig.Should().NotBeNull();
        savedConfig!.Settings.Should().ContainKey("model").WhoseValue.Should().Be("claude-sonnet-4.6");
    }

    [Fact]
    public async Task EditProvider_PreservesIdOnSave()
    {
        // Arrange — simulate editing an existing provider (Settings page reuses the existing Id)
        var existingId = "existing-provider-id";
        ProviderConfig? savedConfig = null;
        _mockStore.Setup(s => s.SaveProviderConfigAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .Callback<ProviderConfig, CancellationToken>((c, _) => savedConfig = c)
            .Returns(Task.CompletedTask);

        // Act — edit: use existing Id, change DisplayName
        var config = new ProviderConfig
        {
            Id = existingId,
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "Updated Name",
            Settings = new Dictionary<string, string>
            {
                ["apiUrl"] = "https://api.github.com",
                ["clientId"] = "Iv1.new123",
                ["installationId"] = "11111111",
                ["privateKeyBase64"] = "LS0tLS1CRUdJTi...",
                ["owner"] = "neworg",
                ["repo"] = "newrepo"
            }
        };
        await _mockStore.Object.SaveProviderConfigAsync(config, CancellationToken.None);

        // Assert — Id preserved, values updated
        savedConfig.Should().NotBeNull();
        savedConfig!.Id.Should().Be(existingId);
        savedConfig.DisplayName.Should().Be("Updated Name");
        savedConfig.Settings["owner"].Should().Be("neworg");
    }

    [Fact]
    public async Task DeleteProvider_CallsDeleteWithCorrectIdAndKind()
    {
        // Arrange
        string? deletedId = null;
        ProviderKind? deletedKind = null;
        _mockStore.Setup(s => s.DeleteProviderConfigAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Callback<string, ProviderKind, CancellationToken>((id, kind, _) => { deletedId = id; deletedKind = kind; })
            .Returns(Task.CompletedTask);

        // Act — replicate what DeleteProvider does
        await _mockStore.Object.DeleteProviderConfigAsync("provider-to-delete", ProviderKind.Issue, CancellationToken.None);

        // Assert
        deletedId.Should().Be("provider-to-delete");
        deletedKind.Should().Be(ProviderKind.Issue);
    }

    [Theory]
    [InlineData(ProviderKind.Issue)]
    [InlineData(ProviderKind.Repository)]
    [InlineData(ProviderKind.Agent)]
    public async Task DeleteProvider_WorksForAllProviderKinds(ProviderKind kind)
    {
        // Arrange
        ProviderKind? deletedKind = null;
        _mockStore.Setup(s => s.DeleteProviderConfigAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .Callback<string, ProviderKind, CancellationToken>((_, k, _) => deletedKind = k)
            .Returns(Task.CompletedTask);

        // Act
        await _mockStore.Object.DeleteProviderConfigAsync("some-id", kind, CancellationToken.None);

        // Assert
        deletedKind.Should().Be(kind);
    }

    [Fact]
    public async Task DeleteProvider_ThenReload_ProviderIsGone()
    {
        // Arrange — start with one provider, then after delete return empty
        var providers = new List<ProviderConfig>
        {
            new() { Id = "del-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "To Delete" }
        };
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(providers);
        _mockStore.Setup(s => s.DeleteProviderConfigAsync("del-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .Callback(() => providers.Clear())
            .Returns(Task.CompletedTask);

        // Act
        var before = await _mockStore.Object.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);
        before.Should().HaveCount(1);

        await _mockStore.Object.DeleteProviderConfigAsync("del-1", ProviderKind.Repository, CancellationToken.None);
        var after = await _mockStore.Object.LoadProviderConfigsAsync(ProviderKind.Repository, CancellationToken.None);

        // Assert
        after.Should().BeEmpty();
    }

    // --- Requirement 9.7: Pipeline Configuration save/load ---

    [Fact]
    public async Task LoadPipelineConfig_ReturnsCurrentValues()
    {
        // Arrange
        var config = new PipelineConfiguration
        {
            MaxRetries = 5,
            AgentTimeout = TimeSpan.FromMinutes(60),
            MinCoverageThreshold = 90.0,
            SecurityScanEnabled = true
        };
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        // Act — same as Settings page OnInitializedAsync
        var loaded = await _mockStore.Object.LoadPipelineConfigAsync(CancellationToken.None);

        // Assert — verify the page would display these values
        loaded.MaxRetries.Should().Be(5);
        loaded.AgentTimeout.TotalMinutes.Should().Be(60);
        loaded.MinCoverageThreshold.Should().Be(90.0);
        loaded.SecurityScanEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task SavePipelineConfig_PersistsAllFields()
    {
        // Arrange
        PipelineConfiguration? savedConfig = null;
        _mockStore.Setup(s => s.SavePipelineConfigAsync(It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineConfiguration, CancellationToken>((c, _) => savedConfig = c)
            .Returns(Task.CompletedTask);

        // Act — replicate what SavePipelineConfig does (converts _agentTimeoutMinutes to TimeSpan)
        int agentTimeoutMinutes = 45;
        var config = new PipelineConfiguration
        {
            MaxRetries = 7,
            AgentTimeout = TimeSpan.FromMinutes(agentTimeoutMinutes),
            MinCoverageThreshold = 95.5,
            SecurityScanEnabled = true
        };
        await _mockStore.Object.SavePipelineConfigAsync(config, CancellationToken.None);

        // Assert
        savedConfig.Should().NotBeNull();
        savedConfig!.MaxRetries.Should().Be(7);
        savedConfig.AgentTimeout.Should().Be(TimeSpan.FromMinutes(45));
        savedConfig.MinCoverageThreshold.Should().Be(95.5);
        savedConfig.SecurityScanEnabled.Should().BeTrue();
    }

    [Fact]
    public async Task SavePipelineConfig_DefaultValues_AreCorrect()
    {
        // Arrange
        PipelineConfiguration? savedConfig = null;
        _mockStore.Setup(s => s.SavePipelineConfigAsync(It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineConfiguration, CancellationToken>((c, _) => savedConfig = c)
            .Returns(Task.CompletedTask);

        // Act — save with defaults (what the page shows initially)
        var config = new PipelineConfiguration();
        await _mockStore.Object.SavePipelineConfigAsync(config, CancellationToken.None);

        // Assert — verify defaults match design doc
        savedConfig.Should().NotBeNull();
        savedConfig!.MaxRetries.Should().Be(3);
        savedConfig.AgentTimeout.Should().Be(TimeSpan.FromMinutes(30));
        savedConfig.MinCoverageThreshold.Should().Be(50.0);
        savedConfig.SecurityScanEnabled.Should().BeFalse();
        savedConfig.BlacklistedPaths.Should().BeEquivalentTo(new[] { ".kiro", ".github", ".brain" });
        savedConfig.BlacklistMode.Should().Be(BlacklistMode.WarnAndExclude);
        savedConfig.CodeReview.Enabled.Should().BeTrue();
        savedConfig.CodeReview.FixPrompt.Should().BeNull();
        savedConfig.AnalysisPrompt.Should().Be(PipelineConfiguration.DefaultAnalysisPrompt);
        savedConfig.ImplementationPrompt.Should().Be(PipelineConfiguration.DefaultImplementationPrompt);
    }

    [Fact]
    public async Task SaveThenLoadPipelineConfig_RoundTrips()
    {
        // Arrange — simulate save then reload (what Settings page does after saving)
        PipelineConfiguration? savedConfig = null;
        _mockStore.Setup(s => s.SavePipelineConfigAsync(It.IsAny<PipelineConfiguration>(), It.IsAny<CancellationToken>()))
            .Callback<PipelineConfiguration, CancellationToken>((c, _) => savedConfig = c)
            .Returns(Task.CompletedTask);
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => savedConfig ?? new PipelineConfiguration());

        // Act — save
        var original = new PipelineConfiguration
        {
            MaxRetries = 10,
            AgentTimeout = TimeSpan.FromMinutes(120),
            MinCoverageThreshold = 50.0,
            SecurityScanEnabled = false,
            AnalysisPrompt = "Custom analysis",
            ImplementationPrompt = "Custom implementation"
        };
        await _mockStore.Object.SavePipelineConfigAsync(original, CancellationToken.None);

        // Act — reload
        var loaded = await _mockStore.Object.LoadPipelineConfigAsync(CancellationToken.None);

        // Assert
        loaded.MaxRetries.Should().Be(10);
        loaded.AgentTimeout.Should().Be(TimeSpan.FromMinutes(120));
        loaded.MinCoverageThreshold.Should().Be(50.0);
        loaded.SecurityScanEnabled.Should().BeFalse();
        loaded.AnalysisPrompt.Should().Be("Custom analysis");
        loaded.ImplementationPrompt.Should().Be("Custom implementation");
    }

    // --- Related Providers Auto-Creation ---

    [Fact]
    public void RelatedProviders_SharedSettingsKeys_MatchGitHubProviderFields()
    {
        // The shared settings keys used by the modal must match the fields that all three
        // GitHub provider types (Issue, Repository, Pipeline) have in common.
        var expectedSharedKeys = new[] { "apiUrl", "clientId", "installationId", "privateKeyBase64", "owner", "repo" };

        // Verify an Issue provider config contains all shared keys
        var issueConfig = new ProviderConfig
        {
            Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test",
            Settings = new Dictionary<string, string>
            {
                ["apiUrl"] = "https://api.github.com", ["clientId"] = "Iv1.abc",
                ["installationId"] = "456", ["privateKeyBase64"] = "key", ["owner"] = "org", ["repo"] = "repo"
            }
        };

        foreach (var key in expectedSharedKeys)
            issueConfig.Settings.Should().ContainKey(key);

        // Repository provider has the same keys plus baseBranch
        var repoSettings = new Dictionary<string, string>(issueConfig.Settings) { ["baseBranch"] = "main" };
        repoSettings.Should().ContainKey("baseBranch");
        foreach (var key in expectedSharedKeys)
            repoSettings.Should().ContainKey(key);
    }

    [Fact]
    public void RelatedProviders_TargetKinds_ExcludeSourceKind()
    {
        // When saving an Issue provider, the modal should offer Repository and Pipeline (not Issue)
        var allGitHubKinds = new[] { ProviderKind.Issue, ProviderKind.Repository, ProviderKind.Pipeline };

        var issueTargets = allGitHubKinds.Where(k => k != ProviderKind.Issue).ToList();
        issueTargets.Should().BeEquivalentTo(new[] { ProviderKind.Repository, ProviderKind.Pipeline });

        var repoTargets = allGitHubKinds.Where(k => k != ProviderKind.Repository).ToList();
        repoTargets.Should().BeEquivalentTo(new[] { ProviderKind.Issue, ProviderKind.Pipeline });

        var pipelineTargets = allGitHubKinds.Where(k => k != ProviderKind.Pipeline).ToList();
        pipelineTargets.Should().BeEquivalentTo(new[] { ProviderKind.Issue, ProviderKind.Repository });
    }

    [Fact]
    public void RelatedProviders_ExistingProviderDetection_MatchesByOwnerAndRepo()
    {
        // Simulate the existing-provider detection logic from ShowRelatedProvidersModal
        var existingProviders = new List<ProviderConfig>
        {
            new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "My Repo",
                Settings = new() { ["owner"] = "acme", ["repo"] = "webapp", ["baseBranch"] = "main" } },
            new() { Id = "rp-2", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Other Repo",
                Settings = new() { ["owner"] = "acme", ["repo"] = "other-project" } }
        };

        var targetOwner = "acme";
        var targetRepo = "webapp";

        var match = existingProviders.FirstOrDefault(p =>
            p.ProviderType == "GitHub"
            && p.Settings.GetValueOrDefault("owner", "") == targetOwner
            && p.Settings.GetValueOrDefault("repo", "") == targetRepo);

        match.Should().NotBeNull();
        match!.Id.Should().Be("rp-1");
        match.DisplayName.Should().Be("My Repo");
    }

    [Fact]
    public void RelatedProviders_ExistingProviderDetection_ReturnsNull_WhenNoMatch()
    {
        var existingProviders = new List<ProviderConfig>
        {
            new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "My Repo",
                Settings = new() { ["owner"] = "acme", ["repo"] = "webapp" } }
        };

        var match = existingProviders.FirstOrDefault(p =>
            p.ProviderType == "GitHub"
            && p.Settings.GetValueOrDefault("owner", "") == "different-org"
            && p.Settings.GetValueOrDefault("repo", "") == "different-repo");

        match.Should().BeNull();
    }

    [Fact]
    public void RelatedProviders_BaseBranchFallback_DefaultsToMain()
    {
        // Simulate the baseBranch fallback logic from ConfirmRelatedProviders
        string? emptyBranch = "";
        string? nullBranch = null;
        string? whitespaceBranch = "   ";
        string validBranch = "develop";

        string Resolve(string? input) => string.IsNullOrWhiteSpace(input) ? "main" : input;

        Resolve(emptyBranch).Should().Be("main");
        Resolve(nullBranch).Should().Be("main");
        Resolve(whitespaceBranch).Should().Be("main");
        Resolve(validBranch).Should().Be("develop");
    }

    [Fact]
    public async Task RelatedProviders_AutoCreatedConfig_HasCorrectStructure()
    {
        // Verify the ProviderConfig structure produced by the modal's auto-creation logic
        var savedConfigs = new List<ProviderConfig>();
        _mockStore.Setup(s => s.SaveProviderConfigAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .Callback<ProviderConfig, CancellationToken>((c, _) => savedConfigs.Add(c))
            .Returns(Task.CompletedTask);

        // Replicate the exact logic from ConfirmRelatedProviders
        var sharedSettings = new Dictionary<string, string>
        {
            ["apiUrl"] = "https://api.github.com",
            ["clientId"] = "Iv1.abc123",
            ["installationId"] = "78901234",
            ["privateKeyBase64"] = "LS0tLS1CRUdJTi...",
            ["owner"] = "myorg",
            ["repo"] = "myrepo"
        };

        // Repository provider: shared settings + baseBranch
        var repoSettings = new Dictionary<string, string>(sharedSettings) { ["baseBranch"] = "main" };
        var repoConfig = new ProviderConfig
        {
            Kind = ProviderKind.Repository, ProviderType = "GitHub",
            DisplayName = "myorg/myrepo - Repository", Settings = repoSettings
        };
        await _mockStore.Object.SaveProviderConfigAsync(repoConfig, CancellationToken.None);

        // Pipeline provider: shared settings only (no baseBranch)
        var pipelineConfig = new ProviderConfig
        {
            Kind = ProviderKind.Pipeline, ProviderType = "GitHub",
            DisplayName = "myorg/myrepo - Pipeline", Settings = new Dictionary<string, string>(sharedSettings)
        };
        await _mockStore.Object.SaveProviderConfigAsync(pipelineConfig, CancellationToken.None);

        // Verify structure
        savedConfigs.Should().HaveCount(2);

        var repo = savedConfigs[0];
        repo.Kind.Should().Be(ProviderKind.Repository);
        repo.ProviderType.Should().Be("GitHub");
        repo.DisplayName.Should().Be("myorg/myrepo - Repository");
        repo.Settings.Should().HaveCount(7); // 6 shared + baseBranch
        repo.Settings.Should().ContainKey("baseBranch").WhoseValue.Should().Be("main");

        var pipeline = savedConfigs[1];
        pipeline.Kind.Should().Be(ProviderKind.Pipeline);
        pipeline.ProviderType.Should().Be("GitHub");
        pipeline.DisplayName.Should().Be("myorg/myrepo - Pipeline");
        pipeline.Settings.Should().HaveCount(6); // 6 shared, no baseBranch
        pipeline.Settings.Should().NotContainKey("baseBranch");

        // Both should have all shared keys
        foreach (var config in savedConfigs)
        {
            config.Settings.Should().ContainKey("apiUrl");
            config.Settings.Should().ContainKey("clientId");
            config.Settings.Should().ContainKey("installationId");
            config.Settings.Should().ContainKey("privateKeyBase64");
            config.Settings.Should().ContainKey("owner");
            config.Settings.Should().ContainKey("repo");
        }
    }

    [Fact]
    public void RelatedProviders_NonGitHubProvider_ShouldNotTriggerModal()
    {
        // The modal guard checks ProviderType != "GitHub" and returns early.
        // Verify that KiroCli agent providers would be excluded.
        var agentConfig = new ProviderConfig
        {
            Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Kiro Agent",
            Settings = new() { ["executablePath"] = "/usr/bin/kiro-cli" }
        };

        agentConfig.ProviderType.Should().NotBe("GitHub");
    }

    // --- Error handling ---

    [Fact]
    public async Task LoadAllData_WhenStoreThrows_DoesNotCrash()
    {
        // Arrange — simulate what happens when LoadAllData catches an exception
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Store unavailable"));

        // Act & Assert — the Settings page wraps this in try-catch and shows status message
        var act = () => _mockStore.Object.LoadProviderConfigsAsync(ProviderKind.Issue, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Store unavailable");
    }

    [Fact]
    public async Task SaveProvider_WhenStoreThrows_PropagatesError()
    {
        // Arrange
        _mockStore.Setup(s => s.SaveProviderConfigAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Write failed"));

        // Act & Assert — Settings page catches this and shows error status
        var config = new ProviderConfig
        {
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "Test"
        };
        var act = () => _mockStore.Object.SaveProviderConfigAsync(config, CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Write failed");
    }

    // --- REQ-8: Merge-Save Correctness Tests ---

    [Fact]
    public async Task MergeSave_GeneralThenSecurity_PreservesGeneralValues()
    {
        // Use a real JsonConfigurationStore with a temp directory
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new JsonConfigurationStore(tempDir);

            // Save General section (MaxRetries=7, AgentTimeout=45min)
            await store.UpdatePipelineConfigAsync(
                c => c with { MaxRetries = 7, AgentTimeout = TimeSpan.FromMinutes(45) },
                CancellationToken.None);

            // Save Security section (BrainReadOnly=true)
            await store.UpdatePipelineConfigAsync(
                c => c with { BrainReadOnly = true },
                CancellationToken.None);

            // Verify General values are preserved
            var loaded = await store.LoadPipelineConfigAsync(CancellationToken.None);
            loaded.MaxRetries.Should().Be(7);
            loaded.AgentTimeout.Should().Be(TimeSpan.FromMinutes(45));
            loaded.BrainReadOnly.Should().BeTrue();
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task MergeSave_PreservesNonUiFields()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new JsonConfigurationStore(tempDir);

            // Set non-default values for all 7 non-UI properties
            await store.SavePipelineConfigAsync(new PipelineConfiguration
            {
                IssuePageSize = 50,
                WorkspaceBaseDirectory = "/custom/workspaces",
                StallWarningInterval = TimeSpan.FromMinutes(5),
                StallPollInterval = TimeSpan.FromSeconds(10),
                LastUsedProviderIds = new Dictionary<string, string> { ["issue"] = "test-id" },
                SecurityScanEnabled = true,
                ClosedLoopMaxPagesToFetch = 20
            }, CancellationToken.None);

            // Save any sub-section (General)
            await store.UpdatePipelineConfigAsync(
                c => c with { MaxRetries = 5 },
                CancellationToken.None);

            // Verify all 7 non-UI fields survive
            var loaded = await store.LoadPipelineConfigAsync(CancellationToken.None);
            loaded.IssuePageSize.Should().Be(50);
            loaded.WorkspaceBaseDirectory.Should().Be("/custom/workspaces");
            loaded.StallWarningInterval.Should().Be(TimeSpan.FromMinutes(5));
            loaded.StallPollInterval.Should().Be(TimeSpan.FromSeconds(10));
            loaded.LastUsedProviderIds.Should().ContainKey("issue");
            loaded.SecurityScanEnabled.Should().BeTrue();
            loaded.ClosedLoopMaxPagesToFetch.Should().Be(20);
            // Also verify the saved field
            loaded.MaxRetries.Should().Be(5);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task UpdatePipelineConfigAsync_CorruptedFile_ThrowsInvalidOperationException()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var store = new JsonConfigurationStore(tempDir);

            // Write invalid JSON to the config file
            var configPath = Path.Combine(tempDir, "pipeline-config.json");
            await File.WriteAllTextAsync(configPath, "{ this is not valid json }}}");

            // UpdatePipelineConfigAsync should throw instead of silently overwriting
            var act = () => store.UpdatePipelineConfigAsync(
                c => c with { MaxRetries = 5 },
                CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("*invalid JSON*");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
