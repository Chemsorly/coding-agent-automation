using AwesomeAssertions;
using CodingAgent.AgentGateway;
using CodingAgent.Orchestration;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Pipeline.UnitTests.Services;

/// <summary>
/// Tests for AgentTokenRefreshService.
/// Covers: token vending paths (GitHub App, GitLab PAT, static token, no-auth throws),
/// run-based vs DB-based provider resolution, missing config throws.
/// </summary>
public sealed class AgentTokenRefreshServiceTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<ITokenVendingService> _tokenVending = new();
    private readonly Mock<ILogger> _logger = new();
    private readonly AgentTokenRefreshService _sut;

    public AgentTokenRefreshServiceTests()
    {
        _sut = new AgentTokenRefreshService(_facade.Object, _tokenVending.Object, _logger.Object);
    }

    private static PipelineRun MakeRun(string repoConfigId = "github-repo", string? brainConfigId = null) =>
        PipelineRun.CreateImplementation(new PipelineRunCreationParams
        {
            RunId = "run-1",
            IssueIdentifier = "GH-1",
            IssueTitle = "T",
            IssueProviderConfigId = "github",
            RepoProviderConfigId = repoConfigId,
            BrainProviderConfigId = brainConfigId,
            AgentId = "agent-1",
            AgentProviderConfigId = "kiro",
            InitiatedBy = "test",
            StartedAt = DateTimeOffset.UtcNow
        });

    private static ProviderConfig MakeConfig(string id = "cfg-1", Dictionary<string, string>? settings = null) =>
        new()
        {
            Id = id,
            Kind = ProviderKind.Repository,
            DisplayName = "Test",
            ProviderType = "GitHub",
            Settings = settings ?? []
        };

    // ── Static access token path ──────────────────────────────────────────

    [Fact]
    public async Task RefreshTokenAsync_WithStaticAccessToken_ReturnsThatToken()
    {
        var run = MakeRun("github-repo");
        _facade.Setup(f => f.GetRun("run-1")).Returns(run);
        var config = MakeConfig("github-repo", new() { [ProviderSettingKeys.AccessToken] = "gh-pat-token" });
        _facade.Setup(f => f.GetProviderConfigByIdAsync("github-repo", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var result = await _sut.RefreshTokenAsync("run-1", ProviderKind.Repository, CancellationToken.None);

        result.Token.Should().Be("gh-pat-token");
        // Static tokens are assigned a far-future sentinel expiry (24h), not 1h.
        result.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddHours(24), TimeSpan.FromSeconds(10));
    }

    // ── Generic 'token' field path ────────────────────────────────────────

    [Fact]
    public async Task RefreshTokenAsync_WithTokenField_NoExpiry_ReturnsToken()
    {
        // A static 'token' with no tokenExpiresAt in settings (e.g. a personal access token
        // stored directly). Returns the token with a far-future sentinel expiry.
        var run = MakeRun("github-repo");
        _facade.Setup(f => f.GetRun("run-1")).Returns(run);
        var config = MakeConfig("github-repo", new() { [ProviderSettingKeys.Token] = "existing-token" });
        _facade.Setup(f => f.GetProviderConfigByIdAsync("github-repo", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var result = await _sut.RefreshTokenAsync("run-1", ProviderKind.Repository, CancellationToken.None);

        result.Token.Should().Be("existing-token");
        // No expiry metadata → far-future sentinel, not a fabricated 1h.
        result.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddHours(24), TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task RefreshTokenAsync_WithTokenField_AndFreshExpiry_ReturnsParsedExpiry()
    {
        // A pre-vended GitHub App token stored in 'token' with 'tokenExpiresAt' metadata.
        // The expiry should be the parsed value, not fabricated.
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(30);
        var run = MakeRun("github-repo");
        _facade.Setup(f => f.GetRun("run-1")).Returns(run);
        var config = MakeConfig("github-repo", new()
        {
            [ProviderSettingKeys.Token] = "short-lived-token",
            [ProviderSettingKeys.TokenExpiresAt] = expiresAt.ToString("O")
        });
        _facade.Setup(f => f.GetProviderConfigByIdAsync("github-repo", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var result = await _sut.RefreshTokenAsync("run-1", ProviderKind.Repository, CancellationToken.None);

        result.Token.Should().Be("short-lived-token");
        result.ExpiresAt.Should().BeCloseTo(expiresAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task RefreshTokenAsync_WithTokenField_AndExpiredToken_ThrowsHubException()
    {
        // Root-cause regression test for issue #2334:
        // A pre-vended token with tokenExpiresAt in the past (or within the 5-min buffer)
        // must THROW instead of silently returning the stale token.
        // Previously, the code returned it with a fabricated ExpiresAt — that is the bug.
        var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-10); // clearly expired
        var run = MakeRun("github-repo");
        _facade.Setup(f => f.GetRun("run-1")).Returns(run);
        var config = MakeConfig("github-repo", new()
        {
            [ProviderSettingKeys.Token] = "stale-token",
            [ProviderSettingKeys.TokenExpiresAt] = expiredAt.ToString("O")
        });
        _facade.Setup(f => f.GetProviderConfigByIdAsync("github-repo", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var act = () => _sut.RefreshTokenAsync("run-1", ProviderKind.Repository, CancellationToken.None);

        // Must throw with the specific re-dispatch message, not silently return the stale token.
        // Use "re-dispatched" as the unique anchor — it only appears in the expired/expiring-imminently
        // throw, not in any other path, making this assertion distinct from the malformed-expiry throw.
        await act.Should().ThrowAsync<HubException>().WithMessage("*re-dispatched*");
    }

    [Fact]
    public async Task RefreshTokenAsync_WithTokenField_AndTokenExpiringWithinBuffer_ThrowsHubException()
    {
        // Token expiring within the 5-minute renewal buffer should also throw.
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(2); // within buffer
        var run = MakeRun("github-repo");
        _facade.Setup(f => f.GetRun("run-1")).Returns(run);
        var config = MakeConfig("github-repo", new()
        {
            [ProviderSettingKeys.Token] = "about-to-expire-token",
            [ProviderSettingKeys.TokenExpiresAt] = expiresAt.ToString("O")
        });
        _facade.Setup(f => f.GetProviderConfigByIdAsync("github-repo", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var act = () => _sut.RefreshTokenAsync("run-1", ProviderKind.Repository, CancellationToken.None);

        // Use "expiring imminently" as the anchor — this phrase only appears in the expiring-within-buffer
        // throw message and uniquely distinguishes this path from the malformed-expiry throw.
        await act.Should().ThrowAsync<HubException>().WithMessage("*expiring imminently*");
    }

    [Fact]
    public async Task RefreshTokenAsync_WithTokenField_AndMalformedExpiry_ThrowsHubException()
    {
        // CRITICAL regression test: when 'tokenExpiresAt' key is present but the value is unparseable
        // (e.g. a Unix-epoch integer), the service must throw rather than silently falling through
        // to the "no expiry metadata" static-token branch and returning the token with a 24h sentinel.
        // Falling through would reintroduce the silent stale-token bug (issue #2334).
        var run = MakeRun("github-repo");
        _facade.Setup(f => f.GetRun("run-1")).Returns(run);
        var config = MakeConfig("github-repo", new()
        {
            [ProviderSettingKeys.Token] = "possibly-stale-token",
            [ProviderSettingKeys.TokenExpiresAt] = "not-a-date" // unparseable
        });
        _facade.Setup(f => f.GetProviderConfigByIdAsync("github-repo", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var act = () => _sut.RefreshTokenAsync("run-1", ProviderKind.Repository, CancellationToken.None);

        // Must throw with the malformed-expiry message, distinct from the expired/expiring throw.
        await act.Should().ThrowAsync<HubException>().WithMessage("*malformed*");
    }

    // ── GitHub App JWT path ───────────────────────────────────────────────

    [Fact]
    public async Task RefreshTokenAsync_WithPrivateKey_CallsTokenVendingService()
    {
        var run = MakeRun("github-app");
        _facade.Setup(f => f.GetRun("run-1")).Returns(run);
        var config = MakeConfig("github-app", new() { [ProviderSettingKeys.PrivateKeyBase64] = "base64key" });
        _facade.Setup(f => f.GetProviderConfigByIdAsync("github-app", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        var expiry = DateTimeOffset.UtcNow.AddMinutes(10);
        _tokenVending.Setup(t => t.GenerateAgentTokenAsync(config, It.IsAny<CancellationToken>()))
            .ReturnsAsync(("jwt-token", expiry));

        var result = await _sut.RefreshTokenAsync("run-1", ProviderKind.Repository, CancellationToken.None);

        result.Token.Should().Be("jwt-token");
        result.ExpiresAt.Should().Be(expiry);
    }

    // ── No auth method → throws HubException ─────────────────────────────

    [Fact]
    public async Task RefreshTokenAsync_NoAuthMethod_ThrowsHubException()
    {
        var run = MakeRun("cfg-no-auth");
        _facade.Setup(f => f.GetRun("run-1")).Returns(run);
        var config = MakeConfig("cfg-no-auth"); // no settings
        _facade.Setup(f => f.GetProviderConfigByIdAsync("cfg-no-auth", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var act = () => _sut.RefreshTokenAsync("run-1", ProviderKind.Repository, CancellationToken.None);
        await act.Should().ThrowAsync<HubException>().WithMessage("*no supported authentication method*");
    }

    // ── Provider config not found → throws HubException ──────────────────

    [Fact]
    public async Task RefreshTokenAsync_ProviderConfigNotFound_ThrowsHubException()
    {
        var run = MakeRun("missing-config");
        _facade.Setup(f => f.GetRun("run-1")).Returns(run);
        _facade.Setup(f => f.GetProviderConfigByIdAsync("missing-config", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var act = () => _sut.RefreshTokenAsync("run-1", ProviderKind.Repository, CancellationToken.None);
        await act.Should().ThrowAsync<HubException>().WithMessage("*Provider config not found*");
    }

    // ── No run → DB fallback ──────────────────────────────────────────────

    // ── No run → DB fallback tested via null-workitem path below ─────────

    [Fact]
    public async Task RefreshTokenAsync_NoRunAndNoWorkItem_ThrowsHubException()
    {
        _facade.Setup(f => f.GetRun("run-1")).Returns((PipelineRun?)null);
        _facade.Setup(f => f.GetWorkItemProviderConfigIdsAsync("run-1", It.IsAny<CancellationToken>()))
            .Returns(Task.FromResult<(string? RepoProviderConfigId, string? BrainProviderConfigId)?>(null));

        var act = () => _sut.RefreshTokenAsync("run-1", ProviderKind.Repository, CancellationToken.None);
        await act.Should().ThrowAsync<HubException>().WithMessage("*No active run or work item*");
    }

    // ── Brain token path ──────────────────────────────────────────────────

    [Fact]
    public async Task RefreshTokenAsync_BrainKind_WithBrainConfig_ReturnsToken()
    {
        var run = MakeRun("repo-cfg", brainConfigId: "brain-cfg");
        _facade.Setup(f => f.GetRun("run-1")).Returns(run);
        var config = MakeConfig("brain-cfg", new() { [ProviderSettingKeys.AccessToken] = "brain-token" });
        _facade.Setup(f => f.GetProviderConfigByIdAsync("brain-cfg", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var result = await _sut.RefreshTokenAsync("run-1", ProviderKind.Brain, CancellationToken.None);

        result.Token.Should().Be("brain-token");
    }

    [Fact]
    public async Task RefreshTokenAsync_BrainKind_NoBrainConfig_ThrowsHubException()
    {
        var run = MakeRun("repo-cfg", brainConfigId: null); // no brain
        _facade.Setup(f => f.GetRun("run-1")).Returns(run);

        var act = () => _sut.RefreshTokenAsync("run-1", ProviderKind.Brain, CancellationToken.None);
        await act.Should().ThrowAsync<HubException>().WithMessage("*Brain*");
    }
}
