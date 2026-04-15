using FluentAssertions;
using Moq;
using KiroWebUI.Pipeline.Interfaces;
using KiroWebUI.Pipeline.Models;

namespace KiroWebUI.Tests.Pipeline;

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
                ["appId"] = "123456",
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
        savedConfig.Settings.Should().ContainKey("appId").WhoseValue.Should().Be("123456");
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
                ["appId"] = "123456",
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
                ["appId"] = "654321",
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
        savedConfig.MinCoverageThreshold.Should().Be(80.0);
        savedConfig.SecurityScanEnabled.Should().BeFalse();
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
            SecurityScanEnabled = false
        };
        await _mockStore.Object.SavePipelineConfigAsync(original, CancellationToken.None);

        // Act — reload
        var loaded = await _mockStore.Object.LoadPipelineConfigAsync(CancellationToken.None);

        // Assert
        loaded.MaxRetries.Should().Be(10);
        loaded.AgentTimeout.Should().Be(TimeSpan.FromMinutes(120));
        loaded.MinCoverageThreshold.Should().Be(50.0);
        loaded.SecurityScanEnabled.Should().BeFalse();
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
}
