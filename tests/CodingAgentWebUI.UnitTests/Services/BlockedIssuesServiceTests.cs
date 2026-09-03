using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using Moq;

namespace CodingAgentWebUI.UnitTests.Services;

public class BlockedIssuesServiceTests
{
    private static PagedResult<IssueSummary> TwoIssues() => new()
    {
        Items = new[]
        {
            new IssueSummary { Identifier = "10", Title = "Blocked one", Labels = Array.Empty<string>(), Description = "depends on #5", Url = "https://x/issues/10" },
            new IssueSummary { Identifier = "11", Title = "Ready one", Labels = Array.Empty<string>(), Description = "", Url = "https://x/issues/11" },
        },
        Page = 1,
        PageSize = 20,
        HasMore = false
    };

    [Fact]
    public async Task GetBlockedIssuesAsync_ReturnsOnlyIssuesBlockedByOpenDependencies()
    {
        var template = new PipelineJobTemplate
        {
            Id = "t1", Name = "T", IssueProviderId = "prov1", RepoProviderId = "repo1", Enabled = true
        };

        var config = new Mock<IPipelineApiConfigClient>();
        config.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { template });
        config.Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ProviderConfig { Id = "prov1", DisplayName = "P", Kind = ProviderKind.Issue, ProviderType = "GitHub" } });

        var provider = new Mock<IIssueProvider>();
        // Set up both overloads: the service calls the 3-arg (a default interface method that delegates
        // to the labelled 4-arg), so cover whichever Moq actually intercepts.
        provider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TwoIssues());
        provider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TwoIssues());

        var factory = new Mock<IProviderFactory>();
        factory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(provider.Object);

        var dep = new Mock<IDependencyChecker>();
        dep.Setup(d => d.CheckAsync(It.Is<IssueIdentifier>(i => i.Value == "10"), It.IsAny<string?>(),
                It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DependencyCheckResult { IsReady = false, BlockedBy = new[] { 5 }, TotalDependencies = 1 });
        dep.Setup(d => d.CheckAsync(It.Is<IssueIdentifier>(i => i.Value == "11"), It.IsAny<string?>(),
                It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        var sut = new BlockedIssuesService(config.Object, factory.Object, dep.Object);

        var result = await sut.GetBlockedIssuesAsync(projectId: null, CancellationToken.None);

        Assert.Single(result);
        Assert.Equal("10", result[0].Identifier);
        Assert.Equal(new[] { 5 }, result[0].BlockedBy);
        Assert.Equal("https://x/issues/10", result[0].Url);
    }

    [Fact]
    public async Task GetBacklogAsync_ReturnsAllOpenIssues_WithReadiness()
    {
        var template = new PipelineJobTemplate
        {
            Id = "t1", Name = "T", IssueProviderId = "prov1", RepoProviderId = "repo1", Enabled = true
        };

        var config = new Mock<IPipelineApiConfigClient>();
        config.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { template });
        config.Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ProviderConfig { Id = "prov1", DisplayName = "P", Kind = ProviderKind.Issue, ProviderType = "GitHub" } });

        var provider = new Mock<IIssueProvider>();
        provider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TwoIssues());
        provider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TwoIssues());

        var factory = new Mock<IProviderFactory>();
        factory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(provider.Object);

        var dep = new Mock<IDependencyChecker>();
        dep.Setup(d => d.CheckAsync(It.Is<IssueIdentifier>(i => i.Value == "10"), It.IsAny<string?>(),
                It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DependencyCheckResult { IsReady = false, BlockedBy = new[] { 5 }, TotalDependencies = 1 });
        dep.Setup(d => d.CheckAsync(It.Is<IssueIdentifier>(i => i.Value == "11"), It.IsAny<string?>(),
                It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        var sut = new BlockedIssuesService(config.Object, factory.Object, dep.Object);

        var result = await sut.GetBacklogAsync(projectId: null, CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, b => b.Identifier == "10" && !b.IsReady && b.BlockedBy.Contains(5));
        Assert.Contains(result, b => b.Identifier == "11" && b.IsReady && b.BlockedBy.Count == 0);
    }

    [Fact]
    public async Task GetBlockedIssuesAsync_DisabledTemplatesIgnored_ReturnsEmpty()
    {
        var config = new Mock<IPipelineApiConfigClient>();
        config.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new PipelineJobTemplate { Id = "t1", Name = "T", IssueProviderId = "prov1", RepoProviderId = "repo1", Enabled = false }
            });
        config.Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        var factory = new Mock<IProviderFactory>();
        var dep = new Mock<IDependencyChecker>();

        var sut = new BlockedIssuesService(config.Object, factory.Object, dep.Object);

        var result = await sut.GetBlockedIssuesAsync(projectId: null, CancellationToken.None);

        Assert.Empty(result);
        factory.Verify(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()), Times.Never);
    }
}
