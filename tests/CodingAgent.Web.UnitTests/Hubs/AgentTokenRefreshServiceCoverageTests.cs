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
/// Additional coverage tests for <see cref="AgentTokenRefreshService"/> targeting
/// paths not covered by <see cref="AgentTokenRefreshServiceTests"/>:
/// - Brain config not found in store → <see cref="HubException"/>
/// - GitLab PAT ExpiresAt is a far-future sentinel (~24 hours, never expires naturally)
/// - Pre-vended static token ExpiresAt is a far-future sentinel (~24 hours)
/// - K8s mode fallback with null brain config ID on brain kind → still throws
/// </summary>
public sealed class AgentTokenRefreshServiceCoverageTests
{
    private readonly Mock<IAgentHubFacade> _facade = new();
    private readonly Mock<ITokenVendingService> _tokenVending = new();
    private readonly Mock<ILogger> _logger = new();

    private AgentTokenRefreshService CreateService()
        => new(_facade.Object, _tokenVending.Object, _logger.Object);

    private static PipelineRun MakeRun(
        string jobId = "job-1",
        string repoConfigId = "repo-1",
        string? brainConfigId = null) => new()
        {
            RunId = jobId,
            IssueIdentifier = "org/repo#1",
            IssueTitle = "Test",
            IssueProviderConfigId = "issue-1",
            RepoProviderConfigId = repoConfigId,
            BrainProviderConfigId = brainConfigId
        };

    // ── Brain config not found in store → HubException ───────────────────

    [Fact]
    public async Task RefreshToken_BrainKind_ConfigNotFoundInStore_ThrowsHubException()
    {
        // brainProviderConfigId is set, but GetProviderConfigByIdAsync returns null
        var run = MakeRun(brainConfigId: "brain-deleted");
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _facade.Setup(f => f.GetProviderConfigByIdAsync(
                "brain-deleted", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var service = CreateService();

        var act = () => service.RefreshTokenAsync("job-1", ProviderKind.Brain, CancellationToken.None);

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*brain-deleted*not found*");
    }

    // ── GitLab PAT ExpiresAt is a far-future sentinel ─────────────────────

    [Fact]
    public async Task RefreshToken_GitLabPat_ExpiresAtIsFarFutureSentinel()
    {
        var config = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitLab",
            DisplayName = "Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.AccessToken] = "glpat-valid-token"
            }
        };

        _facade.Setup(f => f.GetRun("job-1")).Returns(MakeRun());
        _facade.Setup(f => f.GetProviderConfigByIdAsync("repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var before = DateTimeOffset.UtcNow;
        var service = CreateService();

        var result = await service.RefreshTokenAsync("job-1", ProviderKind.Repository, CancellationToken.None);

        result.Token.Should().Be("glpat-valid-token");
        // Static GitLab PATs use a far-future sentinel (24h) so agents treat them as non-expiring.
        result.ExpiresAt.Should().BeCloseTo(before.AddHours(24), TimeSpan.FromMinutes(1),
            "GitLab PAT is a static token — ExpiresAt must be a far-future sentinel, not a short-lived 1h");
    }

    // ── Pre-vended static token ExpiresAt is a far-future sentinel ────────

    [Fact]
    public async Task RefreshToken_PreVendedToken_WithoutExpiryMetadata_ExpiresAtIsFarFutureSentinel()
    {
        // A static 'token' with no tokenExpiresAt in settings (e.g. a personal access token
        // stored directly in provider config). Should return a far-future sentinel, not 1h.
        var config = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.Token] = "pre-vended-12345"
            }
        };

        _facade.Setup(f => f.GetRun("job-1")).Returns(MakeRun());
        _facade.Setup(f => f.GetProviderConfigByIdAsync("repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var before = DateTimeOffset.UtcNow;
        var service = CreateService();

        var result = await service.RefreshTokenAsync("job-1", ProviderKind.Repository, CancellationToken.None);

        result.Token.Should().Be("pre-vended-12345");
        // No expiry metadata → far-future sentinel, not a fabricated 1h.
        result.ExpiresAt.Should().BeCloseTo(before.AddHours(24), TimeSpan.FromMinutes(1),
            "static token with no expiry metadata must use a far-future sentinel (24h), not fabricate 1h");
    }

    // ── K8s fallback: brain kind, brainId is null → HubException ──────────

    [Fact]
    public async Task RefreshToken_K8sMode_BrainKind_BrainIdIsNull_ThrowsHubException()
    {
        // K8s mode: no in-memory run; WorkItem found but only repoId, no brainId
        _facade.Setup(f => f.GetRun("wi-k8s")).Returns((PipelineRun?)null);
        _facade.Setup(f => f.GetWorkItemProviderConfigIdsAsync("wi-k8s", It.IsAny<CancellationToken>()))
            .ReturnsAsync(("repo-from-payload", (string?)null)); // no brain config

        var service = CreateService();

        var act = () => service.RefreshTokenAsync("wi-k8s", ProviderKind.Brain, CancellationToken.None);

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*Brain provider config ID not available*");
    }

    // ── GitHub App token ExpiresAt propagated from vending service ────────

    [Fact]
    public async Task RefreshToken_GitHubApp_ExpiresAtMatchesVendingServiceResponse()
    {
        var expectedExpiry = new DateTimeOffset(2026, 9, 1, 12, 0, 0, TimeSpan.Zero);
        var config = new ProviderConfig
        {
            Id = "repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Repo",
            Settings = new Dictionary<string, string>
            {
                [ProviderSettingKeys.PrivateKeyBase64] = "dGVzdA==",
                [ProviderSettingKeys.ClientId] = "c",
                [ProviderSettingKeys.InstallationId] = "1"
            }
        };

        var run = MakeRun();
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _facade.Setup(f => f.GetProviderConfigByIdAsync("repo-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        _tokenVending.Setup(t => t.GenerateAgentTokenAsync(config, It.IsAny<CancellationToken>(), false))
            .ReturnsAsync(("ghs_new_token", expectedExpiry));

        var service = CreateService();

        var result = await service.RefreshTokenAsync("job-1", ProviderKind.Repository, CancellationToken.None);

        result.ExpiresAt.Should().Be(expectedExpiry,
            "GitHub App token ExpiresAt must come from the vending service, not be hardcoded");
    }

    // ── K8s fallback: brain kind, brainId non-null but config not found ───

    [Fact]
    public async Task RefreshToken_K8sMode_BrainKind_ConfigNotFound_ThrowsHubException()
    {
        _facade.Setup(f => f.GetRun("wi-k8s-brain")).Returns((PipelineRun?)null);
        _facade.Setup(f => f.GetWorkItemProviderConfigIdsAsync("wi-k8s-brain", It.IsAny<CancellationToken>()))
            .ReturnsAsync(("repo-payload", "brain-payload"));

        _facade.Setup(f => f.GetProviderConfigByIdAsync(
                "brain-payload", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var service = CreateService();

        var act = () => service.RefreshTokenAsync("wi-k8s-brain", ProviderKind.Brain, CancellationToken.None);

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*brain-payload*not found*");
    }

    // ── Repo config not found for non-brain kind → HubException ──────────

    [Fact]
    public async Task RefreshToken_RepoKind_ConfigNotFound_ThrowsHubException()
    {
        var run = MakeRun(repoConfigId: "deleted-repo-cfg");
        _facade.Setup(f => f.GetRun("job-1")).Returns(run);
        _facade.Setup(f => f.GetProviderConfigByIdAsync(
                "deleted-repo-cfg", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var service = CreateService();

        var act = () => service.RefreshTokenAsync("job-1", ProviderKind.Repository, CancellationToken.None);

        await act.Should().ThrowAsync<HubException>()
            .WithMessage("*Provider config not found*");
    }
}
