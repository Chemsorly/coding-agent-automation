using AwesomeAssertions;
using CodingAgentWebUI.Hub;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

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
        result.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromSeconds(10));
    }

    // ── Generic 'token' field path ────────────────────────────────────────

    [Fact]
    public async Task RefreshTokenAsync_WithTokenField_ReturnsToken()
    {
        var run = MakeRun("github-repo");
        _facade.Setup(f => f.GetRun("run-1")).Returns(run);
        var config = MakeConfig("github-repo", new() { [ProviderSettingKeys.Token] = "existing-token" });
        _facade.Setup(f => f.GetProviderConfigByIdAsync("github-repo", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);

        var result = await _sut.RefreshTokenAsync("run-1", ProviderKind.Repository, CancellationToken.None);

        result.Token.Should().Be("existing-token");
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
