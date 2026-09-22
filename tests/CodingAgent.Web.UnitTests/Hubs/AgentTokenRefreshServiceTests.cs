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
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Repo",
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
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitLab",
            DisplayName = "Repo",
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
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Repo",
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
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Repo",
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
            Id = "brain-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Brain",
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
    public async Task RefreshToken_BrainKind_NullBrainProviderConfigId_ThrowsHubException()
    {
        var run = new PipelineRun
        {
            RunId = "job-1",
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "issue-1",
            RepoProviderConfigId = "repo-1",
            BrainProviderConfigId = null // No brain configured
        };

        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(run);

        var service = CreateService();

        var act = () => service.RefreshTokenAsync("job-1", ProviderKind.Brain, CancellationToken.None);

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*Brain provider config ID not available*");
        _mockFacade.Verify(
            f => f.GetProviderConfigByIdAsync(It.IsAny<string>(), It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    #endregion

    #region K8s mode fallback

    [Fact]
    public async Task RefreshToken_K8sMode_ResolvesFromWorkItem()
    {
        // TODO [WARNING]: This test covers only the K8s/WorkItem fallback with ProviderKind.Repository.
        // The combined K8s-mode + ProviderKind.Brain path (repoId non-null, brainId null, Brain requested)
        // is not exercised: in that case brainProviderConfigId.HasValue is false after the null→null
        // conversion and the service should throw HubException. Add a separate test:
        // RefreshToken_K8sFallback_BrainKind_NullBrainProviderConfigId_ThrowsHubException.
        // (TestQualityReviewer)
        _mockFacade.Setup(f => f.GetRun("wi-k8s-1")).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetWorkItemProviderConfigIdsAsync("wi-k8s-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(("repo-from-payload", "brain-from-payload"));

        var repoConfig = new ProviderConfig
        {
            Id = "repo-from-payload",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Repo",
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

    #endregion

    #region includeIssuePermission = true path

    [Fact]
    public async Task RefreshToken_WithIncludeIssuePermission_True_PassesTrueToGenerateAgentTokenAsync()
    {
        // Acceptance criterion: AgentTokenRefreshService unit tests cover the
        // includeIssuePermission = true path through VendTokenAsync → GenerateAgentTokenAsync.
        // TODO [WARNING]: This test covers the GitHub App path (privateKeyBase64 present). There
        // is no corresponding test for the PAT/static-token path (no privateKeyBase64). On the
        // PAT path, VendTokenAsync returns the static token directly without calling
        // GenerateAgentTokenAsync, so includeIssuePermission is effectively a no-op. Add a test
        // that passes includeIssuePermission: true with a PAT config and asserts
        // GenerateAgentTokenAsync is NOT called — this would catch any accidental change to
        // VendTokenAsync's branching that starts calling GenerateAgentTokenAsync on the PAT path.
        // (TestQualityReviewer)
        var config = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Repo",
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
        _mockTokenVending
            .Setup(t => t.GenerateAgentTokenAsync(config, It.IsAny<CancellationToken>(), true))
            .ReturnsAsync(("ghs_issues_token", expectedExpiry));

        var service = CreateService();

        var result = await service.RefreshTokenAsync("job-1", ProviderKind.Repository, CancellationToken.None, includeIssuePermission: true);

        result.Token.Should().Be("ghs_issues_token");
        result.ExpiresAt.Should().Be(expectedExpiry);

        // Verify GenerateAgentTokenAsync was called with includeIssuePermission = true
        _mockTokenVending.Verify(
            t => t.GenerateAgentTokenAsync(config, It.IsAny<CancellationToken>(), true),
            Times.Once);

        // Verify GenerateAgentTokenAsync was NOT called with includeIssuePermission = false
        _mockTokenVending.Verify(
            t => t.GenerateAgentTokenAsync(config, It.IsAny<CancellationToken>(), false),
            Times.Never);
    }

    #endregion
}

// ── Additional coverage for whitespace token values and K8s empty repoId ──

public sealed class AgentTokenRefreshServiceAdditionalTests
{
    private readonly Mock<IAgentHubFacade> _mockFacade = new();
    private readonly Mock<ITokenVendingService> _mockTokenVending = new();
    private readonly Mock<ILogger> _mockLogger = new();

    private AgentTokenRefreshService CreateService() =>
        new(_mockFacade.Object, _mockTokenVending.Object, _mockLogger.Object);

    private static PipelineRun MakeRun(string repoConfigId = "repo-1") => new()
    {
        RunId = "job-1",
        IssueIdentifier = "org/repo#1",
        IssueTitle = "Test",
        IssueProviderConfigId = "issue-1",
        RepoProviderConfigId = repoConfigId
    };

    // K8s fallback: WorkItem found but repoProviderConfigId is empty string → throws

    [Fact]
    public async Task RefreshToken_K8sFallback_EmptyRepoProviderConfigId_ThrowsHubException()
    {
        _mockFacade.Setup(f => f.GetRun("wi-1")).Returns((PipelineRun?)null);
        _mockFacade.Setup(f => f.GetWorkItemProviderConfigIdsAsync("wi-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(("", null)); // empty repoProviderConfigId

        var service = CreateService();

        var act = () => service.RefreshTokenAsync("wi-1", ProviderKind.Repository, CancellationToken.None);

        // TODO [WARNING]: The assertion `*repoProviderConfigId*` is fragile after the ProviderConfigId
        // type migration. The empty-string guard fires in ResolveProviderConfigIdsAsync (DB-fallback path)
        // and throws with a message containing "repoProviderConfigId", but if that guard is moved or
        // re-worded the test may silently pass via a different throw path (e.g. config-not-found). Pin
        // the assertion to the specific diagnostic message from ResolveProviderConfigIdsAsync, e.g.:
        // .WithMessage("*WorkItem*has no repoProviderConfigId*"). (TestQualityReviewer)
        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*repoProviderConfigId*");
    }

    // AccessToken with whitespace-only value falls through to Token check, then to error

    [Fact]
    public async Task RefreshToken_WhitespaceAccessToken_FallsThroughToError()
    {
        var config = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitLab",
            DisplayName = "Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.AccessToken] = "   " // whitespace only — treated as empty
            }
        };

        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(MakeRun());
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var service = CreateService();

        var act = () => service.RefreshTokenAsync("job-1", ProviderKind.Repository, CancellationToken.None);

        // Falls through AccessToken (whitespace) and Token (absent) to the no-auth-method error
        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*no supported authentication method*");
    }

    // Token with whitespace-only value falls through to error

    [Fact]
    public async Task RefreshToken_WhitespaceToken_FallsThroughToError()
    {
        var config = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.Token] = "  " // whitespace only
            }
        };

        _mockFacade.Setup(f => f.GetRun("job-1")).Returns(MakeRun());
        _mockFacade.Setup(f => f.GetProviderConfigByIdAsync("repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var service = CreateService();

        var act = () => service.RefreshTokenAsync("job-1", ProviderKind.Repository, CancellationToken.None);

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*no supported authentication method*");
    }
}

// ── Retry loop tests (Issue #2759) ────────────────────────────────────────────

/// <summary>
/// Tests for the server-side retry in <see cref="AgentTokenRefreshService.ResolveTargetConfigAsync"/>.
/// Verifies that a transient null return from <see cref="IAgentHubFacade.GetProviderConfigByIdAsync"/>
/// is retried once before throwing <see cref="HubException"/>.
/// Covers both <see cref="ProviderKind.Repository"/> (repo branch) and <see cref="ProviderKind.Brain"/> (brain branch).
/// </summary>
public sealed class AgentTokenRefreshServiceRetryTests
{
    private readonly Mock<IAgentHubFacade> _mockFacade = new();
    private readonly Mock<ITokenVendingService> _mockTokenVending = new();
    private readonly Mock<ILogger> _mockLogger = new();

    private AgentTokenRefreshService CreateService() =>
        new(_mockFacade.Object, _mockTokenVending.Object, _mockLogger.Object);

    private static PipelineRun MakeRun(string repoConfigId = "repo-retry", string? brainConfigId = null) => new()
    {
        RunId = "job-retry",
        IssueIdentifier = "org/repo#1",
        IssueTitle = "Test",
        IssueProviderConfigId = "issue-1",
        RepoProviderConfigId = repoConfigId,
        BrainProviderConfigId = brainConfigId
    };

    // ── Repo kind: succeeds on second attempt ─────────────────────────────────

    // TODO [WARNING]: this test cannot verify that Task.Delay(500ms) is actually awaited between
    // attempts — it only asserts call count and the final result. A regression that removes the
    // delay (making retries instantaneous) would pass undetected. Consider injecting a time
    // abstraction or using a fake clock if the retry delay becomes a reliability concern.
    [Fact]
    public async Task RefreshToken_RepoKind_TransientNullOnFirstAttempt_SucceedsOnSecondAttempt()
    {
        // Arrange: first call returns null (transient miss), second returns a valid config
        var validConfig = new ProviderConfig
        {
            Id = "repo-retry",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.PrivateKeyBase64] = "dGVzdA==",
                [ProviderSettingKeys.ClientId] = "client-1",
                [ProviderSettingKeys.InstallationId] = "12345"
            }
        };

        _mockFacade.Setup(f => f.GetRun("job-retry")).Returns(MakeRun());
        _mockFacade
            .SetupSequence(f => f.GetProviderConfigByIdAsync("repo-retry", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null)  // first attempt — transient miss
            .ReturnsAsync(validConfig);            // second attempt — succeeds

        var expectedExpiry = DateTimeOffset.UtcNow.AddHours(1);
        _mockTokenVending
            .Setup(t => t.GenerateAgentTokenAsync(validConfig, It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(("ghs_retry_token", expectedExpiry));

        var service = CreateService();

        // Act
        var result = await service.RefreshTokenAsync("job-retry", ProviderKind.Repository, CancellationToken.None);

        // Assert: token returned successfully despite the first null
        result.Token.Should().Be("ghs_retry_token");
        result.ExpiresAt.Should().Be(expectedExpiry);

        // GetProviderConfigByIdAsync must have been called exactly twice (attempt 0 → null, attempt 1 → config)
        _mockFacade.Verify(
            f => f.GetProviderConfigByIdAsync("repo-retry", ProviderKind.Repository, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // ── Repo kind: throws after all retries exhausted ─────────────────────────

    // TODO [WARNING]: The stale comment below was accurate when written but the
    // RefreshToken_RepoKind_TransientNullOnFirstAttempt_SucceedsOnSecondAttempt test already
    // exists above (line ~552) and asserts Times.Exactly(2). The coverage gap is closed.
    // This comment should be removed in a follow-up cleanup pass to avoid misleading reviewers
    // into thinking the test is still missing.
    [Fact]
    public async Task RefreshToken_RepoKind_AllAttemptsReturnNull_ThrowsHubExceptionAfterTwoAttempts()
    {
        // Arrange: both attempts return null — config is genuinely missing
        _mockFacade.Setup(f => f.GetRun("job-retry")).Returns(MakeRun());
        _mockFacade
            .Setup(f => f.GetProviderConfigByIdAsync("repo-retry", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var service = CreateService();

        // Act
        var act = () => service.RefreshTokenAsync("job-retry", ProviderKind.Repository, CancellationToken.None);

        // Assert: HubException is thrown after retries are exhausted
        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*Provider config not found*");

        // GetProviderConfigByIdAsync must have been called exactly twice (two attempts, both null)
        _mockFacade.Verify(
            f => f.GetProviderConfigByIdAsync("repo-retry", ProviderKind.Repository, It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        // Error log must include the config ID. After migration the Error is emitted by
        // ProviderConfigResolver.ResolveRequiredAsync with template "Provider config {ConfigId} ({Kind}) not found."
        // Arg 0 = config ID (string), Arg 1 = ProviderKind. Verify both are present.
        _mockLogger.Verify(
            l => l.Error(
                It.IsAny<string>(),
                It.Is<string>(id => id == "repo-retry"),
                It.Is<ProviderKind>(k => k == ProviderKind.Repository)),
            Times.Once);
    }

    // ── Brain kind: succeeds on second attempt ────────────────────────────────

    // TODO [WARNING]: same as the repo-kind counterpart — Task.Delay(500ms) between attempts
    // cannot be verified here; only call count and final result are asserted.
    [Fact]
    public async Task RefreshToken_BrainKind_TransientNullOnFirstAttempt_SucceedsOnSecondAttempt()
    {
        // Arrange: first call returns null (transient miss), second returns a valid brain config
        var validBrainConfig = new ProviderConfig
        {
            Id = "brain-retry",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Brain",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.PrivateKeyBase64] = "dGVzdA==",
                [ProviderSettingKeys.ClientId] = "client-brain",
                [ProviderSettingKeys.InstallationId] = "99999"
            }
        };

        _mockFacade.Setup(f => f.GetRun("job-retry")).Returns(MakeRun(brainConfigId: "brain-retry"));
        _mockFacade
            .SetupSequence(f => f.GetProviderConfigByIdAsync("brain-retry", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null)     // first attempt — transient miss
            .ReturnsAsync(validBrainConfig);          // second attempt — succeeds

        var expectedExpiry = DateTimeOffset.UtcNow.AddHours(1);
        _mockTokenVending
            .Setup(t => t.GenerateAgentTokenAsync(validBrainConfig, It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(("ghs_brain_retry_token", expectedExpiry));

        var service = CreateService();

        // Act
        var result = await service.RefreshTokenAsync("job-retry", ProviderKind.Brain, CancellationToken.None);

        // Assert: token returned successfully despite the first null
        result.Token.Should().Be("ghs_brain_retry_token");
        result.ExpiresAt.Should().Be(expectedExpiry);

        // GetProviderConfigByIdAsync must have been called exactly twice (attempt 0 → null, attempt 1 → config)
        _mockFacade.Verify(
            f => f.GetProviderConfigByIdAsync("brain-retry", ProviderKind.Repository, It.IsAny<CancellationToken>()),
            Times.Exactly(2));
    }

    // ── Brain kind: throws after all retries exhausted ────────────────────────

    [Fact]
    public async Task RefreshToken_BrainKind_AllAttemptsReturnNull_ThrowsHubExceptionAfterTwoAttempts()
    {
        // Arrange: both attempts return null — brain config is genuinely missing
        _mockFacade.Setup(f => f.GetRun("job-retry")).Returns(MakeRun(brainConfigId: "brain-retry"));
        _mockFacade
            .Setup(f => f.GetProviderConfigByIdAsync("brain-retry", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var service = CreateService();

        // Act
        var act = () => service.RefreshTokenAsync("job-retry", ProviderKind.Brain, CancellationToken.None);

        // Assert: HubException is thrown after retries are exhausted
        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*brain-retry*");

        // GetProviderConfigByIdAsync must have been called exactly twice (two attempts, both null)
        _mockFacade.Verify(
            f => f.GetProviderConfigByIdAsync("brain-retry", ProviderKind.Repository, It.IsAny<CancellationToken>()),
            Times.Exactly(2));

        // Error log must include the brain config ID. After migration the Error is emitted by
        // ProviderConfigResolver.ResolveRequiredAsync with template "Provider config {ConfigId} ({Kind}) not found."
        // Arg 0 = config ID (string), Arg 1 = ProviderKind.Repository (since brain configs are stored as Repository kind).
        _mockLogger.Verify(
            l => l.Error(
                It.IsAny<string>(),
                It.Is<string>(id => id == "brain-retry"),
                It.Is<ProviderKind>(k => k == ProviderKind.Repository)),
            Times.Once);
    }
}
