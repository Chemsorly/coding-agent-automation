using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Additional coverage tests for <see cref="AgentTokenRefreshService"/> targeting
/// paths not covered by <see cref="AgentTokenRefreshServiceTests"/>:
/// - Brain config not found in store → <see cref="HubException"/>
/// - GitLab PAT ExpiresAt is approximately 1 hour in the future
/// - Pre-vended token ExpiresAt is approximately 1 hour in the future
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

    // ── GitLab PAT ExpiresAt is ~1 hour in the future ─────────────────────

    [Fact]
    public async Task RefreshToken_GitLabPat_ExpiresAtIsApproximatelyOneHourFromNow()
    {
        var config = new ProviderConfig
        {
            Id = "repo-1", Kind = ProviderKind.Repository, ProviderType = "GitLab", DisplayName = "Repo",
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
        result.ExpiresAt.Should().BeCloseTo(before.AddHours(1), TimeSpan.FromMinutes(1),
            "GitLab PAT refresh must set ExpiresAt to ~1 hour from now so agents schedule the next refresh correctly");
    }

    // ── Pre-vended token ExpiresAt is ~1 hour in the future ──────────────

    [Fact]
    public async Task RefreshToken_PreVendedToken_ExpiresAtIsApproximatelyOneHourFromNow()
    {
        var config = new ProviderConfig
        {
            Id = "repo-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo",
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
        result.ExpiresAt.Should().BeCloseTo(before.AddHours(1), TimeSpan.FromMinutes(1),
            "pre-vended token refresh must set ExpiresAt to ~1 hour from now");
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
            Id = "repo-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo",
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
