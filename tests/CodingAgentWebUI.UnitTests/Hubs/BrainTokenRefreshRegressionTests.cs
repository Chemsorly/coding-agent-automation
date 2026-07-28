using AwesomeAssertions;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Regression tests for brain repository token refresh.
///
/// Background: RequestTokenRefresh always resolved the work repo config regardless of
/// the requested ProviderKind. This meant brain providers got tokens scoped to the work
/// repo and couldn't access the brain repo (GitHub returned 404).
///
/// Fix: The token refresh service now resolves the brain provider config when ProviderKind.Brain
/// is requested, generating a correctly-scoped token.
/// </summary>
public class BrainTokenRefreshRegressionTests
{
    private readonly Mock<IAgentHubFacade> _mockFacade = new();
    private readonly Mock<ITokenVendingService> _mockTokenVending = new();
    private readonly Mock<ILogger> _mockLogger = new();

    private AgentTokenRefreshService CreateService()
    {
        return new AgentTokenRefreshService(
            _mockFacade.Object,
            _mockTokenVending.Object,
            _mockLogger.Object);
    }

    /// <summary>
    /// Regression: When ProviderKind.Brain is requested, the service must resolve the brain
    /// provider config (using BrainProviderConfigId) and generate a token from it.
    /// Previously it always used the work repo config, causing 404 on brain repo access.
    /// </summary>
    [Fact]
    public async Task RefreshToken_BrainKind_UsesBrainProviderConfig()
    {
        // Arrange
        var workConfig = new ProviderConfig
        {
            Id = "work-repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Work Repo",
            RepositoryRole = RepositoryRole.Work,
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.PrivateKeyBase64] = "dGVzdA==",
                [ProviderSettingKeys.ClientId] = "client-1",
                [ProviderSettingKeys.InstallationId] = "12345",
                [ProviderSettingKeys.Owner] = "org",
                [ProviderSettingKeys.Repo] = "work-repo"
            }
        };

        var brainConfig = new ProviderConfig
        {
            Id = "brain-repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Brain Repo",
            RepositoryRole = RepositoryRole.Brain,
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.PrivateKeyBase64] = "dGVzdA==",
                [ProviderSettingKeys.ClientId] = "client-1",
                [ProviderSettingKeys.InstallationId] = "12345",
                [ProviderSettingKeys.Owner] = "org",
                [ProviderSettingKeys.Repo] = "brain-repo"
            }
        };

        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "issue-1",
            RepoProviderConfigId = "work-repo-1",
            BrainProviderConfigId = "brain-repo-1"
        };

        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("brain-repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(brainConfig);
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("work-repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(workConfig);

        ProviderConfig? capturedConfig = null;
        _mockTokenVending
            .Setup(t => t.GenerateAgentTokenAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<ProviderConfig, CancellationToken, bool>((config, _, _) => capturedConfig = config)
            .ReturnsAsync(("ghs_brain_token", DateTimeOffset.UtcNow.AddHours(1)));

        var service = CreateService();

        // Act
        var response = await service.RefreshTokenAsync("job-1", ProviderKind.Brain, CancellationToken.None);

        // Assert: Token was generated from the BRAIN config, not the work config
        capturedConfig.Should().NotBeNull();
        capturedConfig!.Id.Should().Be("brain-repo-1", "token must be generated from brain config, not work config");
        capturedConfig.Settings[ProviderSettingKeys.Repo].Should().Be("brain-repo");
        response.Token.Should().Be("ghs_brain_token");
    }

    /// <summary>
    /// Regression: When ProviderKind.Repository is requested, the service must still use
    /// the work repo config (existing behavior preserved).
    /// </summary>
    [Fact]
    public async Task RefreshToken_RepositoryKind_UsesWorkRepoConfig()
    {
        // Arrange
        var workConfig = new ProviderConfig
        {
            Id = "work-repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Work Repo",
            RepositoryRole = RepositoryRole.Work,
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.PrivateKeyBase64] = "dGVzdA==",
                [ProviderSettingKeys.ClientId] = "client-1",
                [ProviderSettingKeys.InstallationId] = "12345",
                [ProviderSettingKeys.Owner] = "org",
                [ProviderSettingKeys.Repo] = "work-repo"
            }
        };

        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "issue-1",
            RepoProviderConfigId = "work-repo-1",
            BrainProviderConfigId = "brain-repo-1"
        };

        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("work-repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(workConfig);

        ProviderConfig? capturedConfig = null;
        _mockTokenVending
            .Setup(t => t.GenerateAgentTokenAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<ProviderConfig, CancellationToken, bool>((config, _, _) => capturedConfig = config)
            .ReturnsAsync(("ghs_work_token", DateTimeOffset.UtcNow.AddHours(1)));

        var service = CreateService();

        // Act
        var response = await service.RefreshTokenAsync("job-1", ProviderKind.Repository, CancellationToken.None);

        // Assert: Token was generated from the WORK config
        capturedConfig.Should().NotBeNull();
        capturedConfig!.Id.Should().Be("work-repo-1");
        capturedConfig.Settings[ProviderSettingKeys.Repo].Should().Be("work-repo");
        response.Token.Should().Be("ghs_work_token");
    }

    /// <summary>
    /// Regression: If brain config is not found in store (e.g., removed after run started),
    /// throws HubException instead of silently falling back to work config (misscoped token).
    /// </summary>
    [Fact]
    public async Task RefreshToken_BrainKind_BrainConfigMissing_ThrowsHubException()
    {
        // Arrange: Only work config exists, brain config was removed
        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "issue-1",
            RepoProviderConfigId = "work-repo-1",
            BrainProviderConfigId = "brain-repo-missing" // Config no longer exists
        };

        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        // brain-repo-missing returns null (default Moq behavior)

        var service = CreateService();

        // Act & Assert: Throws HubException, does not silently fall back
        var act = () => service.RefreshTokenAsync("job-1", ProviderKind.Brain, CancellationToken.None);
        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*not found for job*");
    }

    /// <summary>
    /// Regression: If no BrainProviderConfigId is set on the run, Brain kind throws HubException
    /// instead of silently falling back to work config (misscoped token).
    /// </summary>
    [Fact]
    public async Task RefreshToken_BrainKind_NoBrainConfigId_ThrowsHubException()
    {
        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#42",
            IssueTitle = "Test Issue",
            IssueProviderConfigId = "issue-1",
            RepoProviderConfigId = "work-repo-1",
            BrainProviderConfigId = null // No brain configured
        };

        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var service = CreateService();

        // Act & Assert: Throws HubException, does not silently fall back
        var act = () => service.RefreshTokenAsync("job-1", ProviderKind.Brain, CancellationToken.None);
        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*Brain provider config ID not available*");
    }
}
