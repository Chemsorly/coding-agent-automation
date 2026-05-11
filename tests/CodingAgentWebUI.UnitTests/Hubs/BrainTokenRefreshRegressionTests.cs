using AwesomeAssertions;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgentWebUI.UnitTests.Hubs;

/// <summary>
/// Regression tests for brain repository token refresh in AgentHub.
///
/// Background: RequestTokenRefresh always resolved the work repo config regardless of
/// the requested ProviderKind. This meant brain providers got tokens scoped to the work
/// repo and couldn't access the brain repo (GitHub returned 404).
///
/// Fix: RequestTokenRefresh now resolves the brain provider config when ProviderKind.Brain
/// is requested, generating a correctly-scoped token.
/// </summary>
public class BrainTokenRefreshRegressionTests
{
    private readonly Mock<IAgentHubFacade> _mockFacade = new();
    private readonly Mock<ITokenVendingService> _mockTokenVending = new();
    private readonly Mock<ILogger> _mockLogger = new();

    private AgentHub CreateHub()
    {
        // RequestTokenRefresh only uses _facade and _tokenVending,
        // so we can pass null for unused dependencies.
        var hub = new AgentHub(
            _mockFacade.Object,
            _mockTokenVending.Object,
            null!,  // PipelineOrchestrationService — not used by RequestTokenRefresh
            null!,  // ModelFetchService — not used by RequestTokenRefresh
            null!,  // IConsolidationService — not used by RequestTokenRefresh
            null!,  // ConsolidationBadgeService — not used by RequestTokenRefresh
            new Mock<IIssueProviderLabelSwapper>().Object,
            _mockLogger.Object);

        // Set up a mock HubCallerContext
        var mockContext = new Mock<HubCallerContext>();
        mockContext.Setup(c => c.ConnectionId).Returns("conn-1");
        hub.Context = mockContext.Object;

        return hub;
    }

    /// <summary>
    /// Regression: When ProviderKind.Brain is requested, the hub must resolve the brain
    /// provider config (using BrainProviderConfigId) and generate a token from it.
    /// Previously it always used the work repo config, causing 404 on brain repo access.
    /// </summary>
    [Fact]
    public async Task RequestTokenRefresh_BrainKind_UsesbrainProviderConfig()
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
                ["privateKeyBase64"] = "dGVzdA==",
                ["clientId"] = "client-1",
                ["installationId"] = "12345",
                ["owner"] = "org",
                ["repo"] = "work-repo"
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
                ["privateKeyBase64"] = "dGVzdA==",
                ["clientId"] = "client-1",
                ["installationId"] = "12345",
                ["owner"] = "org",
                ["repo"] = "brain-repo"
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
        _mockFacade.Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { workConfig, brainConfig });

        ProviderConfig? capturedConfig = null;
        _mockTokenVending
            .Setup(t => t.GenerateAgentTokenAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<ProviderConfig, CancellationToken, bool>((config, _, _) => capturedConfig = config)
            .ReturnsAsync(("ghs_brain_token", DateTimeOffset.UtcNow.AddHours(1)));

        var hub = CreateHub();

        // Act
        var response = await hub.RequestTokenRefresh("job-1", ProviderKind.Brain);

        // Assert: Token was generated from the BRAIN config, not the work config
        capturedConfig.Should().NotBeNull();
        capturedConfig!.Id.Should().Be("brain-repo-1", "token must be generated from brain config, not work config");
        capturedConfig.Settings["repo"].Should().Be("brain-repo");
        response.Token.Should().Be("ghs_brain_token");
    }

    /// <summary>
    /// Regression: When ProviderKind.Repository is requested, the hub must still use
    /// the work repo config (existing behavior preserved).
    /// </summary>
    [Fact]
    public async Task RequestTokenRefresh_RepositoryKind_UsesWorkRepoConfig()
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
                ["privateKeyBase64"] = "dGVzdA==",
                ["clientId"] = "client-1",
                ["installationId"] = "12345",
                ["owner"] = "org",
                ["repo"] = "work-repo"
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
        _mockFacade.Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { workConfig });

        ProviderConfig? capturedConfig = null;
        _mockTokenVending
            .Setup(t => t.GenerateAgentTokenAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<ProviderConfig, CancellationToken, bool>((config, _, _) => capturedConfig = config)
            .ReturnsAsync(("ghs_work_token", DateTimeOffset.UtcNow.AddHours(1)));

        var hub = CreateHub();

        // Act
        var response = await hub.RequestTokenRefresh("job-1", ProviderKind.Repository);

        // Assert: Token was generated from the WORK config
        capturedConfig.Should().NotBeNull();
        capturedConfig!.Id.Should().Be("work-repo-1");
        capturedConfig.Settings["repo"].Should().Be("work-repo");
        response.Token.Should().Be("ghs_work_token");
    }

    /// <summary>
    /// Regression: If brain config is not found (e.g., removed after run started),
    /// falls back to work repo config gracefully.
    /// </summary>
    [Fact]
    public async Task RequestTokenRefresh_BrainKind_BrainConfigMissing_FallsBackToWorkConfig()
    {
        // Arrange: Only work config exists, brain config was removed
        var workConfig = new ProviderConfig
        {
            Id = "work-repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Work Repo",
            RepositoryRole = RepositoryRole.Work,
            Settings = new Dictionary<string, string>
            {
                ["privateKeyBase64"] = "dGVzdA==",
                ["clientId"] = "client-1",
                ["installationId"] = "12345",
                ["owner"] = "org",
                ["repo"] = "work-repo"
            }
        };

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
        _mockFacade.Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { workConfig });

        ProviderConfig? capturedConfig = null;
        _mockTokenVending
            .Setup(t => t.GenerateAgentTokenAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<ProviderConfig, CancellationToken, bool>((config, _, _) => capturedConfig = config)
            .ReturnsAsync(("ghs_fallback_token", DateTimeOffset.UtcNow.AddHours(1)));

        var hub = CreateHub();

        // Act: Should not throw, falls back to work config
        var response = await hub.RequestTokenRefresh("job-1", ProviderKind.Brain);

        // Assert: Fell back to work config
        capturedConfig.Should().NotBeNull();
        capturedConfig!.Id.Should().Be("work-repo-1");
        response.Token.Should().Be("ghs_fallback_token");
    }

    /// <summary>
    /// Regression: If no BrainProviderConfigId is set on the run, Brain kind falls back
    /// to work config (brain sync was never configured for this run).
    /// </summary>
    [Fact]
    public async Task RequestTokenRefresh_BrainKind_NoBrainConfigId_FallsBackToWorkConfig()
    {
        var workConfig = new ProviderConfig
        {
            Id = "work-repo-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Work Repo",
            Settings = new Dictionary<string, string>
            {
                ["privateKeyBase64"] = "dGVzdA==",
                ["clientId"] = "client-1",
                ["installationId"] = "12345",
                ["owner"] = "org",
                ["repo"] = "work-repo"
            }
        };

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
        _mockFacade.Setup(f => f.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { workConfig });

        ProviderConfig? capturedConfig = null;
        _mockTokenVending
            .Setup(t => t.GenerateAgentTokenAsync(It.IsAny<ProviderConfig>(), It.IsAny<CancellationToken>(), It.IsAny<bool>()))
            .Callback<ProviderConfig, CancellationToken, bool>((config, _, _) => capturedConfig = config)
            .ReturnsAsync(("ghs_work_token", DateTimeOffset.UtcNow.AddHours(1)));

        var hub = CreateHub();

        // Act
        var response = await hub.RequestTokenRefresh("job-1", ProviderKind.Brain);

        // Assert: Falls back to work config since no brain is configured
        capturedConfig.Should().NotBeNull();
        capturedConfig!.Id.Should().Be("work-repo-1");
    }
}
