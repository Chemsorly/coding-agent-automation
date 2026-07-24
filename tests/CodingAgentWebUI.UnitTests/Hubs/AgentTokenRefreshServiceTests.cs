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
/// Unit tests for <see cref="AgentTokenRefreshService"/>, verifying all auth mechanism branches
/// and config resolution paths (SignalR mode vs K8s mode).
/// </summary>
public sealed class AgentTokenRefreshServiceTests
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

    #region GitHub App JWT path

    [Fact]
    public async Task RefreshToken_PipelineRun_GitHubApp_GeneratesToken()
    {
        var config = new ProviderConfig
        {
            Id = "repo-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.PrivateKeyBase64] = "dGVzdA==",
                [ProviderSettingKeys.ClientId] = "client-1",
                [ProviderSettingKeys.InstallationId] = "12345"
            }
        };

        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "issue-1",
            RepoProviderConfigId = "repo-1"
        };

        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var expectedExpiry = DateTimeOffset.UtcNow.AddHours(1);
        _mockTokenVending.Setup(t => t.GenerateAgentTokenAsync(config, It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(("ghs_fresh_token", expectedExpiry));

        var service = CreateService();

        var result = await service.RefreshTokenAsync("job-1", ProviderKind.Repository, CancellationToken.None);

        result.Token.Should().Be("ghs_fresh_token");
        result.ExpiresAt.Should().Be(expectedExpiry);
        _mockTokenVending.Verify(t => t.GenerateAgentTokenAsync(config, It.IsAny<CancellationToken>(), false), Times.Once);
    }

    #endregion

    #region GitLab PAT path

    [Fact]
    public async Task RefreshToken_PipelineRun_GitLabPat_ReturnsAccessToken()
    {
        var config = new ProviderConfig
        {
            Id = "repo-1", Kind = ProviderKind.Repository, ProviderType = "GitLab", DisplayName = "Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.AccessToken] = "glpat-secret-token"
            }
        };

        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "issue-1",
            RepoProviderConfigId = "repo-1"
        };

        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var service = CreateService();

        var result = await service.RefreshTokenAsync("job-1", ProviderKind.Repository, CancellationToken.None);

        result.Token.Should().Be("glpat-secret-token");
        // TODO: Assert result.ExpiresAt is approximately 1 hour in the future — the 1-hour expiry is a behavioral contract agents rely on for scheduling refresh
        _mockTokenVending.Verify(t => t.GenerateAgentTokenAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    #endregion

    #region Pre-vended token fallback

    [Fact]
    public async Task RefreshToken_PipelineRun_PreVendedToken_ReturnsExistingToken()
    {
        var config = new ProviderConfig
        {
            Id = "repo-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.Token] = "pre-vended-token-123"
            }
        };

        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "issue-1",
            RepoProviderConfigId = "repo-1"
        };

        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var service = CreateService();

        var result = await service.RefreshTokenAsync("job-1", ProviderKind.Repository, CancellationToken.None);

        result.Token.Should().Be("pre-vended-token-123");
        // TODO: Assert result.ExpiresAt is approximately 1 hour in the future — the 1-hour expiry is a behavioral contract agents rely on for scheduling refresh
        _mockTokenVending.Verify(t => t.GenerateAgentTokenAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()), Times.Never);
    }

    #endregion

    #region Error paths

    [Fact]
    public async Task RefreshToken_NoRunOrWorkItem_Throws()
    {
        _mockFacade.Setup(f => f.GetRun("missing")).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetWorkItemProviderConfigIdsAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync(((string?, string?)?)null);

        var service = CreateService();

        var act = () => service.RefreshTokenAsync("missing", ProviderKind.Repository, CancellationToken.None);

        await act.Should().ThrowAsync<HubException>().WithMessage("*No active run or work item*");
    }

    [Fact]
    public async Task RefreshToken_ConfigNotFound_Throws()
    {
        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "issue-1",
            RepoProviderConfigId = "deleted-config"
        };

        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("deleted-config", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var service = CreateService();

        var act = () => service.RefreshTokenAsync("job-1", ProviderKind.Repository, CancellationToken.None);

        await act.Should().ThrowAsync<HubException>().WithMessage("*Provider config not found*");
    }

    [Fact]
    public async Task RefreshToken_NoSupportedAuthMethod_Throws()
    {
        var config = new ProviderConfig
        {
            Id = "repo-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo",
            Settings = new Dictionary<string, string>() // No auth keys
        };

        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "issue-1",
            RepoProviderConfigId = "repo-1"
        };

        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var service = CreateService();

        var act = () => service.RefreshTokenAsync("job-1", ProviderKind.Repository, CancellationToken.None);

        await act.Should().ThrowAsync<HubException>().WithMessage("*no supported authentication method*");
    }

    #endregion

    #region Brain kind resolution

    [Fact]
    public async Task RefreshToken_BrainKind_ResolvesBrainConfig()
    {
        var brainConfig = new ProviderConfig
        {
            Id = "brain-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Brain",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.PrivateKeyBase64] = "dGVzdA==",
                [ProviderSettingKeys.ClientId] = "c",
                [ProviderSettingKeys.InstallationId] = "1"
            }
        };

        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "issue-1",
            RepoProviderConfigId = "repo-1",
            BrainProviderConfigId = "brain-1"
        };

        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("brain-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(brainConfig);

        _mockTokenVending.Setup(t => t.GenerateAgentTokenAsync(brainConfig, It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(("brain-token", DateTimeOffset.UtcNow.AddHours(1)));

        var service = CreateService();

        var result = await service.RefreshTokenAsync("job-1", ProviderKind.Brain, CancellationToken.None);

        result.Token.Should().Be("brain-token");
        _mockFacade.Verify(f => f.GetProviderConfigByIdAsync("brain-1", ProviderKind.Repository, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RefreshToken_BrainKind_NoBrainConfig_FallsBackToRepoConfig()
    {
        var repoConfig = new ProviderConfig
        {
            Id = "repo-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.PrivateKeyBase64] = "dGVzdA==",
                [ProviderSettingKeys.ClientId] = "c",
                [ProviderSettingKeys.InstallationId] = "1"
            }
        };

        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "issue-1",
            RepoProviderConfigId = "repo-1",
            BrainProviderConfigId = null // No brain
        };

        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfig);

        _mockTokenVending.Setup(t => t.GenerateAgentTokenAsync(repoConfig, It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(("repo-token", DateTimeOffset.UtcNow.AddHours(1)));

        var service = CreateService();

        var result = await service.RefreshTokenAsync("job-1", ProviderKind.Brain, CancellationToken.None);

        result.Token.Should().Be("repo-token");
    }

    #endregion

    #region K8s mode fallback

    [Fact]
    public async Task RefreshToken_K8sMode_ResolvesFromWorkItem()
    {
        _mockFacade.Setup(f => f.GetRun("wi-k8s-1")).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetWorkItemProviderConfigIdsAsync("wi-k8s-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(("repo-from-payload", "brain-from-payload"));

        var repoConfig = new ProviderConfig
        {
            Id = "repo-from-payload", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.PrivateKeyBase64] = "key",
                [ProviderSettingKeys.ClientId] = "c",
                [ProviderSettingKeys.InstallationId] = "1"
            }
        };
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("repo-from-payload", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfig);
        _mockTokenVending.Setup(t => t.GenerateAgentTokenAsync(repoConfig, It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(("k8s-token", DateTimeOffset.UtcNow.AddHours(1)));

        var service = CreateService();

        var result = await service.RefreshTokenAsync("wi-k8s-1", ProviderKind.Repository, CancellationToken.None);

        result.Token.Should().Be("k8s-token");
        _mockFacade.Verify(f => f.GetWorkItemProviderConfigIdsAsync("wi-k8s-1", It.IsAny<CancellationToken>()), Times.Once);
    }

    // TODO: Add negative test for K8s mode when repoProviderConfigId is empty — the guard at AgentTokenRefreshService.cs:56 throws HubException but no test covers this path (e.g., GetWorkItemProviderConfigIdsAsync returns ("", "brain-id"))

    #endregion
}
