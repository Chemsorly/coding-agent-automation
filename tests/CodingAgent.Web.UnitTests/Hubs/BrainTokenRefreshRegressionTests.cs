using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Web.UnitTests.Hubs;

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

    // ── BrainReadOnly token narrowing ────────────────────────────────────────────

    private ProviderConfig CreateBrainConfigWithPrivateKey(string id = "brain-repo-1") => new()
    {
        Id = id,
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

    private PipelineRun CreateRunWithBrain(string jobId = "job-1") => new()
    {
        RunId = jobId,
        IssueIdentifier = "org/repo#42",
        IssueTitle = "Test Issue",
        IssueProviderConfigId = "issue-1",
        RepoProviderConfigId = "work-repo-1",
        BrainProviderConfigId = "brain-repo-1"
    };

    /// <summary>
    /// When BrainReadOnly is true for the job, the token refresh for ProviderKind.Brain
    /// must call PrepareAgentConfigsAsync with the brain config ID in readOnlyConfigIds.
    /// </summary>
    [Fact]
    public async Task RefreshToken_BrainKind_BrainReadOnly_True_UsesPrepareAgentConfigsWithReadOnlySet()
    {
        var brainConfig = CreateBrainConfigWithPrivateKey();
        var run = CreateRunWithBrain();

        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("brain-repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(brainConfig);
        // ResolveBrainReadOnlyAsync returns true → read-only token must be vended
        _mockFacade.Setup(f => f.ResolveBrainReadOnlyAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        IReadOnlySet<string>? capturedReadOnlyIds = null;
        _mockTokenVending
            .Setup(t => t.PrepareAgentConfigsAsync(
                It.IsAny<IReadOnlyList<ProviderConfig>>(),
                It.IsAny<string>(),
                It.IsAny<CancellationToken>(),
                It.IsAny<bool>(),
                It.IsAny<IReadOnlySet<string>?>()))
            .Callback<IReadOnlyList<ProviderConfig>, string, CancellationToken, bool, IReadOnlySet<string>?>(
                (_, _, _, _, readOnlyIds) => capturedReadOnlyIds = readOnlyIds)
            .ReturnsAsync(new List<ProviderConfig>
            {
                new()
                {
                    Id = "brain-repo-1",
                    Kind = ProviderKind.Repository,
                    ProviderType = "GitHub",
                    DisplayName = "Brain Repo",
                    Settings = new Dictionary<string, string>
                    {
                        [ProviderSettingKeys.Token] = "ghs_read_token",
                        [ProviderSettingKeys.TokenExpiresAt] = "2026-12-01T00:00:00Z"
                    }
                }
            });

        var service = CreateService();
        var response = await service.RefreshTokenAsync("job-1", ProviderKind.Brain, CancellationToken.None);

        capturedReadOnlyIds.Should().NotBeNull("readOnlyConfigIds must be set when BrainReadOnly is true");
        capturedReadOnlyIds!.Should().Contain("brain-repo-1",
            "brain config ID must be in readOnlyConfigIds for read-only brain token refresh");
        response.Token.Should().Be("ghs_read_token");
    }

    /// <summary>
    /// When BrainReadOnly is false for the job, the token refresh uses GenerateAgentTokenAsync
    /// (the standard write-token path), not PrepareAgentConfigsAsync.
    /// </summary>
    [Fact]
    public async Task RefreshToken_BrainKind_BrainReadOnly_False_UsesGenerateAgentTokenAsync()
    {
        var brainConfig = CreateBrainConfigWithPrivateKey();
        var run = CreateRunWithBrain();

        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("brain-repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(brainConfig);
        // ResolveBrainReadOnlyAsync returns false → write token path
        _mockFacade.Setup(f => f.ResolveBrainReadOnlyAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        _mockTokenVending
            .Setup(t => t.GenerateAgentTokenAsync(brainConfig, It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(("ghs_write_token", DateTimeOffset.UtcNow.AddHours(1)));

        var service = CreateService();
        var response = await service.RefreshTokenAsync("job-1", ProviderKind.Brain, CancellationToken.None);

        response.Token.Should().Be("ghs_write_token");
        _mockTokenVending.Verify(
            t => t.GenerateAgentTokenAsync(brainConfig, It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Once,
            "writable brain must use GenerateAgentTokenAsync (write-token path)");
    }

    /// <summary>
    /// When ResolveBrainReadOnlyAsync throws (e.g. config store unavailable), it fails closed:
    /// the exception propagates rather than silently issuing a write token.
    /// </summary>
    [Fact]
    public async Task RefreshToken_BrainKind_ResolveBrainReadOnlyThrows_FailsClosedByPropagating()
    {
        var brainConfig = CreateBrainConfigWithPrivateKey();
        var run = CreateRunWithBrain();

        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("brain-repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(brainConfig);
        // ResolveBrainReadOnlyAsync itself returns true on internal failure (fail-closed in AgentHubFacade)
        // but here we test that even if it throws an unexpected exception, the caller does not silently
        // fall back to a write token.
        _mockFacade.Setup(f => f.ResolveBrainReadOnlyAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("config store unavailable"));

        var service = CreateService();
        var act = () => service.RefreshTokenAsync("job-1", ProviderKind.Brain, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*config store unavailable*");

        // Write token must NOT have been issued
        _mockTokenVending.Verify(
            t => t.GenerateAgentTokenAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()),
            Times.Never,
            "a failed ResolveBrainReadOnlyAsync must not silently fall back to a write token");
    }

    /// <summary>
    /// Repository kind refreshes are unchanged by BrainReadOnly — they must still use
    /// GenerateAgentTokenAsync and must NOT call ResolveBrainReadOnlyAsync.
    /// </summary>
    [Fact]
    public async Task RefreshToken_RepositoryKind_BrainReadOnly_True_UnchangedPath()
    {
        var workConfig = new ProviderConfig
        {
            Id = "work-repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Work Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.PrivateKeyBase64] = "dGVzdA==",
                [ProviderSettingKeys.ClientId] = "client-1",
                [ProviderSettingKeys.InstallationId] = "99999",
                [ProviderSettingKeys.Owner] = "org",
                [ProviderSettingKeys.Repo] = "work-repo"
            }
        };
        var run = CreateRunWithBrain();

        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("work-repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(workConfig);

        _mockTokenVending
            .Setup(t => t.GenerateAgentTokenAsync(workConfig, It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .ReturnsAsync(("ghs_work_token", DateTimeOffset.UtcNow.AddHours(1)));

        var service = CreateService();
        var response = await service.RefreshTokenAsync("job-1", ProviderKind.Repository, CancellationToken.None);

        response.Token.Should().Be("ghs_work_token");
        // ResolveBrainReadOnlyAsync must NOT be called for ProviderKind.Repository
        _mockFacade.Verify(
            f => f.ResolveBrainReadOnlyAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "repository token refresh must not call ResolveBrainReadOnlyAsync");
    }
}
