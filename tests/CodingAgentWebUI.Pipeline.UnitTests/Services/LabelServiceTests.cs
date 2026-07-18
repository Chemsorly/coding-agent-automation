using AwesomeAssertions;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Services;

/// <summary>
/// Unit tests for <see cref="LabelService"/>.
/// Tests label swap routing based on LabelTargetKind (Issue vs PullRequest).
/// Feature: 025-pr-review-pipeline, Requirements: Req 6, 11
/// </summary>
public class LabelServiceTests
{
    private readonly Mock<IProviderConfigStore> _configStore = new();
    private readonly Mock<IProviderFactory> _providerFactory = new();
    private readonly Serilog.ILogger _logger = new Serilog.LoggerConfiguration().CreateLogger();

    private LabelService CreateLabelService() => new(_configStore.Object, _providerFactory.Object, _logger);

    [Fact]
    public async Task SwapLabelAsync_IssueTargetKind_RoutesToIssueProvider()
    {
        var issueProvider = new Mock<IIssueProvider>();
        issueProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var issueConfig = new ProviderConfig
        {
            Id = "ip-1",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "Test Issue Provider"
        };

        _configStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { issueConfig });
        _configStore.Setup(s => s.GetProviderConfigByIdAsync("ip-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);
        _providerFactory.Setup(f => f.CreateIssueProvider(issueConfig))
            .Returns(issueProvider.Object);

        var swapper = CreateLabelService();

        await swapper.SwapLabelAsync("ip-1", "42", AgentLabels.InProgress, LabelTargetKind.Issue, CancellationToken.None);

        // Should have removed all agent labels EXCEPT the target (avoids redundant remove+add)
        foreach (var label in AgentLabels.All)
        {
            if (label == AgentLabels.InProgress)
                issueProvider.Verify(p => p.RemoveLabelAsync("42", label, It.IsAny<CancellationToken>()), Times.Never);
            else
                issueProvider.Verify(p => p.RemoveLabelAsync("42", label, It.IsAny<CancellationToken>()), Times.Once);
        }

        // Should have added the new label
        issueProvider.Verify(p => p.AddLabelAsync("42", AgentLabels.InProgress, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SwapLabelAsync_PullRequestTargetKind_RoutesToRepositoryProvider()
    {
        var repoProvider = new Mock<IRepositoryProvider>();
        repoProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var repoConfig = new ProviderConfig
        {
            Id = "rp-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test Repo Provider"
        };

        _configStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { repoConfig });
        _configStore.Setup(s => s.GetProviderConfigByIdAsync("rp-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfig);
        _providerFactory.Setup(f => f.CreateRepositoryProvider(repoConfig))
            .Returns(repoProvider.Object);

        var swapper = CreateLabelService();

        await swapper.SwapLabelAsync("rp-1", "55", AgentLabels.Done, LabelTargetKind.PullRequest, CancellationToken.None);

        // Should have removed all agent labels EXCEPT the target (avoids redundant remove+add)
        foreach (var label in AgentLabels.All)
        {
            if (label == AgentLabels.Done)
                repoProvider.Verify(p => p.RemovePrLabelAsync(55, label, It.IsAny<CancellationToken>()), Times.Never);
            else
                repoProvider.Verify(p => p.RemovePrLabelAsync(55, label, It.IsAny<CancellationToken>()), Times.Once);
        }

        // Should have added the new label
        repoProvider.Verify(p => p.AddPrLabelAsync(55, AgentLabels.Done, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SwapLabelAsync_BackwardCompatibleOverload_DefaultsToIssue()
    {
        var issueProvider = new Mock<IIssueProvider>();
        issueProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var issueConfig = new ProviderConfig
        {
            Id = "ip-1",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "Test"
        };

        _configStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { issueConfig });
        _configStore.Setup(s => s.GetProviderConfigByIdAsync("ip-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);
        _providerFactory.Setup(f => f.CreateIssueProvider(issueConfig))
            .Returns(issueProvider.Object);

        // Cast to ILabelService to access the backward-compatible default interface method
        ILabelService swapper = CreateLabelService();

        // Use the backward-compatible overload (no LabelTargetKind parameter — defaults to Issue)
        await swapper.SwapLabelAsync("ip-1", "99", AgentLabels.Error, CancellationToken.None);

        // Should route to issue provider (default behavior)
        issueProvider.Verify(p => p.AddLabelAsync("99", AgentLabels.Error, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnsureAgentLabelsAsync_IssueTargetKind_RoutesToIssueProvider()
    {
        var issueProvider = new Mock<IIssueProvider>();
        issueProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        issueProvider.Setup(p => p.EnsureAgentLabelsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var issueConfig = new ProviderConfig
        {
            Id = "ip-1",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "Test"
        };

        _configStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { issueConfig });
        _configStore.Setup(s => s.GetProviderConfigByIdAsync("ip-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);
        _providerFactory.Setup(f => f.CreateIssueProvider(issueConfig))
            .Returns(issueProvider.Object);

        var swapper = CreateLabelService();

        var result = await swapper.EnsureAgentLabelsAsync("ip-1", LabelTargetKind.Issue, CancellationToken.None);

        result.Should().BeTrue();
        issueProvider.Verify(p => p.EnsureAgentLabelsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task EnsureAgentLabelsAsync_PullRequestTargetKind_RoutesToRepositoryProvider()
    {
        var repoProvider = new Mock<IRepositoryProvider>();
        repoProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        repoProvider.Setup(p => p.EnsureAgentLabelsForPullRequestsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var repoConfig = new ProviderConfig
        {
            Id = "rp-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test"
        };

        _configStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { repoConfig });
        _configStore.Setup(s => s.GetProviderConfigByIdAsync("rp-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfig);
        _providerFactory.Setup(f => f.CreateRepositoryProvider(repoConfig))
            .Returns(repoProvider.Object);

        var swapper = CreateLabelService();

        var result = await swapper.EnsureAgentLabelsAsync("rp-1", LabelTargetKind.PullRequest, CancellationToken.None);

        result.Should().BeTrue();
        repoProvider.Verify(p => p.EnsureAgentLabelsForPullRequestsAsync(It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task SwapLabelAsync_PullRequest_InvalidIdentifier_LogsWarningAndSkips()
    {
        var repoConfig = new ProviderConfig
        {
            Id = "rp-1",
            Kind = ProviderKind.Repository,
            ProviderType = "GitHub",
            DisplayName = "Test"
        };

        var repoProvider = new Mock<IRepositoryProvider>();
        repoProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);

        _configStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { repoConfig });
        _configStore.Setup(s => s.GetProviderConfigByIdAsync("rp-1", ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(repoConfig);
        _providerFactory.Setup(f => f.CreateRepositoryProvider(repoConfig))
            .Returns(repoProvider.Object);

        var swapper = CreateLabelService();

        // Non-numeric identifier for PR should be handled gracefully
        await swapper.SwapLabelAsync("rp-1", "not-a-number", AgentLabels.InProgress, LabelTargetKind.PullRequest, CancellationToken.None);

        // Should NOT call any PR label methods (identifier is invalid)
        repoProvider.Verify(p => p.AddPrLabelAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        repoProvider.Verify(p => p.RemovePrLabelAsync(It.IsAny<int>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task SwapLabelAsync_ProviderNotFound_LogsWarningAndDoesNotThrow()
    {
        _configStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>()); // Empty — config not found

        var swapper = CreateLabelService();

        // Should not throw
        await swapper.SwapLabelAsync("nonexistent-id", "42", AgentLabels.InProgress, LabelTargetKind.Issue, CancellationToken.None);
    }

    [Fact]
    public async Task SwapLabelAsync_ProviderThrows_CatchesAndDoesNotPropagate()
    {
        var issueProvider = new Mock<IIssueProvider>();
        issueProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        issueProvider.Setup(p => p.RemoveLabelAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network error"));

        var issueConfig = new ProviderConfig
        {
            Id = "ip-1",
            Kind = ProviderKind.Issue,
            ProviderType = "GitHub",
            DisplayName = "Test"
        };

        _configStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig> { issueConfig });
        _configStore.Setup(s => s.GetProviderConfigByIdAsync("ip-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);
        _providerFactory.Setup(f => f.CreateIssueProvider(issueConfig))
            .Returns(issueProvider.Object);

        var swapper = CreateLabelService();

        // Should not throw — errors are caught and logged
        await swapper.SwapLabelAsync("ip-1", "42", AgentLabels.Error, LabelTargetKind.Issue, CancellationToken.None);
    }
}
