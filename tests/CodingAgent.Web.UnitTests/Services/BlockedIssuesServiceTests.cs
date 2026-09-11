using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Services;
using Moq;

namespace CodingAgent.Web.UnitTests.Services;

public class BlockedIssuesServiceTests
{
    private static PagedResult<IssueSummary> TwoIssues(bool hasMore = false) => new()
    {
        Items = new[]
        {
            new IssueSummary { Identifier = "10", Title = "Blocked one", Labels = Array.Empty<string>(), Description = "depends on #5", Url = "https://x/issues/10" },
            new IssueSummary { Identifier = "11", Title = "Ready one", Labels = Array.Empty<string>(), Description = "", Url = "https://x/issues/11" },
        },
        Page = 1,
        PageSize = 50,
        HasMore = hasMore
    };

    /// <summary>Builds a single-issue paged result for a given identifier.</summary>
    private static PagedResult<IssueSummary> OnePage(string identifier, bool hasMore = false, int page = 1) => new()
    {
        Items = new[]
        {
            new IssueSummary { Identifier = identifier, Title = $"Issue {identifier}", Labels = Array.Empty<string>(), Description = "", Url = $"https://x/issues/{identifier}" },
        },
        Page = page,
        PageSize = 50,
        HasMore = hasMore
    };

    /// <summary>
    /// Builds a full-page paged result with <paramref name="count"/> unique issues, useful for
    /// testing the MaxIssuesPerProvider cap.
    /// </summary>
    private static PagedResult<IssueSummary> FullPage(int startId, int count, bool hasMore, int page = 1) => new()
    {
        Items = Enumerable.Range(startId, count)
            .Select(i => new IssueSummary { Identifier = i.ToString(), Title = $"Issue {i}", Labels = Array.Empty<string>(), Description = "", Url = $"https://x/issues/{i}" })
            .ToArray(),
        Page = page,
        PageSize = count,
        HasMore = hasMore
    };

    // ── Existing tests — updated to access .Issues on the BacklogResult ──────

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

        Assert.Equal(2, result.Issues.Count);
        Assert.False(result.IsTruncated);
        Assert.Contains(result.Issues, b => b.Identifier == "10" && !b.IsReady && b.BlockedBy.Contains(5));
        Assert.Contains(result.Issues, b => b.Identifier == "11" && b.IsReady && b.BlockedBy.Count == 0);
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

    [Fact]
    public async Task GetBacklogAsync_WithProjectId_FiltersToProjectTemplates()
    {
        var t1 = new PipelineJobTemplate { Id = "t1", Name = "T1", IssueProviderId = "prov1", RepoProviderId = "repo1", Enabled = true };
        var t2 = new PipelineJobTemplate { Id = "t2", Name = "T2", IssueProviderId = "prov2", RepoProviderId = "repo2", Enabled = true };

        var config = new Mock<IPipelineApiConfigClient>();
        config.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { t1, t2 });
        config.Setup(c => c.GetProjectByIdAsync("proj1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineProject { Id = "proj1", Name = "P", TemplateIds = new[] { "t1" } });
        config.Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ProviderConfig { Id = "prov1", DisplayName = "P1", Kind = ProviderKind.Issue, ProviderType = "GitHub" },
                new ProviderConfig { Id = "prov2", DisplayName = "P2", Kind = ProviderKind.Issue, ProviderType = "GitHub" },
            });

        var provider = new Mock<IIssueProvider>();
        provider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TwoIssues());
        provider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TwoIssues());

        var factory = new Mock<IProviderFactory>();
        factory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(provider.Object);

        var dep = new Mock<IDependencyChecker>();
        dep.Setup(d => d.CheckAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string?>(),
                It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        var sut = new BlockedIssuesService(config.Object, factory.Object, dep.Object);

        await sut.GetBacklogAsync(projectId: "proj1", CancellationToken.None);

        // Only t1's provider (prov1) is queried — t2 is excluded because it is not in the project.
        factory.Verify(f => f.CreateIssueProvider(It.Is<ProviderConfig>(c => c.Id == "prov1")), Times.Once);
        factory.Verify(f => f.CreateIssueProvider(It.Is<ProviderConfig>(c => c.Id == "prov2")), Times.Never);
    }

    [Fact]
    public async Task GetBacklogAsync_WhenTemplateLoadThrows_ReturnsEmpty()
    {
        var config = new Mock<IPipelineApiConfigClient>();
        config.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("config unavailable"));

        var factory = new Mock<IProviderFactory>();
        var dep = new Mock<IDependencyChecker>();

        var sut = new BlockedIssuesService(config.Object, factory.Object, dep.Object);

        var result = await sut.GetBacklogAsync(projectId: null, CancellationToken.None);

        Assert.Empty(result.Issues); // degrades to empty rather than throwing
        Assert.False(result.IsTruncated);
        factory.Verify(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()), Times.Never);
    }

    [Fact]
    public async Task GetBacklogAsync_WhenProviderThrows_SkipsProviderAndDegrades()
    {
        var template = new PipelineJobTemplate { Id = "t1", Name = "T", IssueProviderId = "prov1", RepoProviderId = "repo1", Enabled = true };

        var config = new Mock<IPipelineApiConfigClient>();
        config.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { template });
        config.Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ProviderConfig { Id = "prov1", DisplayName = "P", Kind = ProviderKind.Issue, ProviderType = "GitHub" } });

        var provider = new Mock<IIssueProvider>();
        provider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider down"));
        provider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider down"));

        var factory = new Mock<IProviderFactory>();
        factory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(provider.Object);

        var dep = new Mock<IDependencyChecker>();

        var sut = new BlockedIssuesService(config.Object, factory.Object, dep.Object);

        var result = await sut.GetBacklogAsync(projectId: null, CancellationToken.None);

        Assert.Empty(result.Issues); // the provider error is swallowed and that provider is skipped
    }

    [Fact]
    public async Task GetBacklogAsync_PropagatesIssueLabelsToBacklogIssue()
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

        var labelledIssues = new PagedResult<IssueSummary>
        {
            Items = new[]
            {
                new IssueSummary { Identifier = "42", Title = "Bug fix", Labels = new[] { "bug", "agent:next" }, Description = "", Url = null },
            },
            Page = 1, PageSize = 50, HasMore = false
        };

        var provider = new Mock<IIssueProvider>();
        provider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(labelledIssues);
        provider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(labelledIssues);

        var factory = new Mock<IProviderFactory>();
        factory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(provider.Object);

        var dep = new Mock<IDependencyChecker>();
        dep.Setup(d => d.CheckAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string?>(),
                It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        var sut = new BlockedIssuesService(config.Object, factory.Object, dep.Object);

        var result = await sut.GetBacklogAsync(projectId: null, CancellationToken.None);

        Assert.Single(result.Issues);
        Assert.NotNull(result.Issues[0].Labels);
        Assert.Equal(new[] { "bug", "agent:next" }, result.Issues[0].Labels);
    }

    [Fact]
    public async Task GetBacklogAsync_DedupesIssuesAcrossProviders()
    {
        var t1 = new PipelineJobTemplate { Id = "t1", Name = "T1", IssueProviderId = "prov1", RepoProviderId = "repo1", Enabled = true };
        var t2 = new PipelineJobTemplate { Id = "t2", Name = "T2", IssueProviderId = "prov2", RepoProviderId = "repo2", Enabled = true };

        var config = new Mock<IPipelineApiConfigClient>();
        config.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { t1, t2 });
        config.Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ProviderConfig { Id = "prov1", DisplayName = "P1", Kind = ProviderKind.Issue, ProviderType = "GitHub" },
                new ProviderConfig { Id = "prov2", DisplayName = "P2", Kind = ProviderKind.Issue, ProviderType = "GitHub" },
            });

        // Both providers return the same issues ("10", "11"); the service dedupes by identifier.
        var provider = new Mock<IIssueProvider>();
        provider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TwoIssues());
        provider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TwoIssues());

        var factory = new Mock<IProviderFactory>();
        factory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(provider.Object);

        var dep = new Mock<IDependencyChecker>();
        dep.Setup(d => d.CheckAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string?>(),
                It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        var sut = new BlockedIssuesService(config.Object, factory.Object, dep.Object);

        var result = await sut.GetBacklogAsync(projectId: null, CancellationToken.None);

        Assert.Equal(2, result.Issues.Count);
        Assert.Single(result.Issues, b => b.Identifier == "10");
        Assert.Single(result.Issues, b => b.Identifier == "11");
    }

    // ── New tests: pagination and truncation ─────────────────────────────────

    [Fact]
    public async Task GetBacklogAsync_WhenHasMoreTrue_FetchesSubsequentPages()
    {
        // Page 1: 3 issues, HasMore=true. Page 2: 1 issue, HasMore=false.
        var page1 = new PagedResult<IssueSummary>
        {
            Items = new[]
            {
                new IssueSummary { Identifier = "1", Title = "I1", Labels = Array.Empty<string>(), Description = "", Url = null },
                new IssueSummary { Identifier = "2", Title = "I2", Labels = Array.Empty<string>(), Description = "", Url = null },
                new IssueSummary { Identifier = "3", Title = "I3", Labels = Array.Empty<string>(), Description = "", Url = null },
            },
            Page = 1, PageSize = 50, HasMore = true
        };
        var page2 = new PagedResult<IssueSummary>
        {
            Items = new[]
            {
                new IssueSummary { Identifier = "4", Title = "I4", Labels = Array.Empty<string>(), Description = "", Url = null },
            },
            Page = 2, PageSize = 50, HasMore = false
        };

        var template = new PipelineJobTemplate { Id = "t1", Name = "T", IssueProviderId = "prov1", RepoProviderId = "repo1", Enabled = true };

        var config = new Mock<IPipelineApiConfigClient>();
        config.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { template });
        config.Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ProviderConfig { Id = "prov1", DisplayName = "P", Kind = ProviderKind.Issue, ProviderType = "GitHub" } });

        var provider = new Mock<IIssueProvider>();
        // Both 3-arg and 4-arg overloads set up to return the right page per call number.
        provider.SetupSequence(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(page1)
            .ReturnsAsync(page2);
        provider.SetupSequence(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(page1)
            .ReturnsAsync(page2);

        var factory = new Mock<IProviderFactory>();
        factory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(provider.Object);

        var dep = new Mock<IDependencyChecker>();
        dep.Setup(d => d.CheckAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string?>(),
                It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        var sut = new BlockedIssuesService(config.Object, factory.Object, dep.Object);

        var result = await sut.GetBacklogAsync(projectId: null, CancellationToken.None);

        Assert.Equal(4, result.Issues.Count);
        Assert.False(result.IsTruncated, "IsTruncated should be false when all pages were fetched");
        Assert.Contains(result.Issues, b => b.Identifier == "1");
        Assert.Contains(result.Issues, b => b.Identifier == "4");
        // Verify the service actually requested page 2, not page 1 twice.
        // SetupSequence is call-order based, so without this Verify a bug where 'page' is never
        // incremented would still return 4 results (sequence position 1 returns page2 regardless).
        provider.Verify(p => p.ListOpenIssuesAsync(2, It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once,
            "the service must request page 2 explicitly — not re-fetch page 1");
    }

    [Fact]
    public async Task GetBacklogAsync_WhenIssueCountReachesMaxCap_StopsAndSetsTruncatedFlag()
    {
        // The service's MaxIssuesPerProvider is 200. Return HasMore=true for every page indefinitely
        // — the service must stop on its own when providerFetched >= MaxIssuesPerProvider.
        var template = new PipelineJobTemplate { Id = "t1", Name = "T", IssueProviderId = "prov1", RepoProviderId = "repo1", Enabled = true };

        var config = new Mock<IPipelineApiConfigClient>();
        config.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { template });
        config.Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ProviderConfig { Id = "prov1", DisplayName = "P", Kind = ProviderKind.Issue, ProviderType = "GitHub" } });

        // Derive page content from the 'page' argument passed by the service rather than a shared
        // counter. This avoids double-increment bugs when both the 3-arg and 4-arg overloads are live
        // in Moq — each page number maps to a deterministic, non-overlapping range of issue IDs.
        PagedResult<IssueSummary> MakeInfinitePageByIndex(int pageArg) =>
            FullPage(startId: (pageArg - 1) * 50 + 1, count: 50, hasMore: true);

        var provider = new Mock<IIssueProvider>();
        provider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int pg, int _, CancellationToken _) => MakeInfinitePageByIndex(pg));
        provider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int pg, int _, IReadOnlyList<string>? _, CancellationToken _) => MakeInfinitePageByIndex(pg));

        var factory = new Mock<IProviderFactory>();
        factory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(provider.Object);

        var dep = new Mock<IDependencyChecker>();
        dep.Setup(d => d.CheckAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string?>(),
                It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        var sut = new BlockedIssuesService(config.Object, factory.Object, dep.Object);

        var result = await sut.GetBacklogAsync(projectId: null, CancellationToken.None);

        // The cap is 200; each page delivers 50 unique issues (no cross-page duplicates), so the
        // service fetches exactly 200 before stopping. IsTruncated must be set because HasMore=true.
        Assert.Equal(200, result.Issues.Count);
        Assert.True(result.IsTruncated, "IsTruncated should be true when the cap was hit and more pages exist");
    }

    [Fact]
    public async Task GetBacklogAsync_WhenExactlyAtCap_AndNoMorePages_IsNotTruncated()
    {
        // Provider returns exactly 200 issues across 4 pages of 50, HasMore=false on the last page.
        // Page content is derived from the 'page' argument so both the 3-arg and 4-arg overloads
        // produce the same deterministic result regardless of which one Moq intercepts — no shared
        // mutable counter that could be double-incremented.
        var template = new PipelineJobTemplate { Id = "t1", Name = "T", IssueProviderId = "prov1", RepoProviderId = "repo1", Enabled = true };

        var config = new Mock<IPipelineApiConfigClient>();
        config.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { template });
        config.Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ProviderConfig { Id = "prov1", DisplayName = "P", Kind = ProviderKind.Issue, ProviderType = "GitHub" } });

        // 4 pages of 50 issues each; page 4 (pageArg==4) signals no more pages.
        PagedResult<IssueSummary> MakeFinitePageByIndex(int pageArg) =>
            FullPage(startId: (pageArg - 1) * 50 + 1, count: 50, hasMore: pageArg < 4);

        var provider = new Mock<IIssueProvider>();
        provider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int pg, int _, CancellationToken _) => MakeFinitePageByIndex(pg));
        provider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((int pg, int _, IReadOnlyList<string>? _, CancellationToken _) => MakeFinitePageByIndex(pg));

        var factory = new Mock<IProviderFactory>();
        factory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(provider.Object);

        var dep = new Mock<IDependencyChecker>();
        dep.Setup(d => d.CheckAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string?>(),
                It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        var sut = new BlockedIssuesService(config.Object, factory.Object, dep.Object);

        var result = await sut.GetBacklogAsync(projectId: null, CancellationToken.None);

        Assert.Equal(200, result.Issues.Count);
        Assert.False(result.IsTruncated, "IsTruncated should be false when all issues were fetched (HasMore=false on last page)");
    }

    [Fact]
    public async Task GetBacklogAsync_IsTruncatedFalse_WhenAllPagesFetched()
    {
        // Basic happy path: single page, HasMore=false → IsTruncated must be false.
        var template = new PipelineJobTemplate { Id = "t1", Name = "T", IssueProviderId = "prov1", RepoProviderId = "repo1", Enabled = true };

        var config = new Mock<IPipelineApiConfigClient>();
        config.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { template });
        config.Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ProviderConfig { Id = "prov1", DisplayName = "P", Kind = ProviderKind.Issue, ProviderType = "GitHub" } });

        var provider = new Mock<IIssueProvider>();
        provider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TwoIssues(hasMore: false));
        provider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TwoIssues(hasMore: false));

        var factory = new Mock<IProviderFactory>();
        factory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(provider.Object);

        var dep = new Mock<IDependencyChecker>();
        dep.Setup(d => d.CheckAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string?>(),
                It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        var sut = new BlockedIssuesService(config.Object, factory.Object, dep.Object);

        var result = await sut.GetBacklogAsync(projectId: null, CancellationToken.None);

        Assert.False(result.IsTruncated);
        Assert.Equal(2, result.Issues.Count);
    }

    [Fact]
    public async Task GetBacklogAsync_DedupesAcrossProviders_WithMultiplePages()
    {
        // Provider 1: page 1 has issue "10", page 2 has issue "12", HasMore=false.
        // Provider 2: returns issue "10" (duplicate) and "11" (unique).
        // Final result should contain "10", "12", "11" — 3 unique issues.
        var t1 = new PipelineJobTemplate { Id = "t1", Name = "T1", IssueProviderId = "prov1", RepoProviderId = "repo1", Enabled = true };
        var t2 = new PipelineJobTemplate { Id = "t2", Name = "T2", IssueProviderId = "prov2", RepoProviderId = "repo2", Enabled = true };

        var config = new Mock<IPipelineApiConfigClient>();
        config.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { t1, t2 });
        config.Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ProviderConfig { Id = "prov1", DisplayName = "P1", Kind = ProviderKind.Issue, ProviderType = "GitHub" },
                new ProviderConfig { Id = "prov2", DisplayName = "P2", Kind = ProviderKind.Issue, ProviderType = "GitHub" },
            });

        var prov1Page1 = new PagedResult<IssueSummary>
        {
            Items = new[] { new IssueSummary { Identifier = "10", Title = "I10", Labels = Array.Empty<string>(), Description = "", Url = null } },
            Page = 1, PageSize = 50, HasMore = true
        };
        var prov1Page2 = new PagedResult<IssueSummary>
        {
            Items = new[] { new IssueSummary { Identifier = "12", Title = "I12", Labels = Array.Empty<string>(), Description = "", Url = null } },
            Page = 2, PageSize = 50, HasMore = false
        };
        var prov2Page1 = new PagedResult<IssueSummary>
        {
            Items = new[]
            {
                new IssueSummary { Identifier = "10", Title = "I10-dup", Labels = Array.Empty<string>(), Description = "", Url = null },
                new IssueSummary { Identifier = "11", Title = "I11", Labels = Array.Empty<string>(), Description = "", Url = null },
            },
            Page = 1, PageSize = 50, HasMore = false
        };

        var prov1 = new Mock<IIssueProvider>();
        prov1.SetupSequence(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(prov1Page1).ReturnsAsync(prov1Page2);
        prov1.SetupSequence(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(prov1Page1).ReturnsAsync(prov1Page2);

        var prov2 = new Mock<IIssueProvider>();
        prov2.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(prov2Page1);
        prov2.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(prov2Page1);

        var factory = new Mock<IProviderFactory>();
        factory.Setup(f => f.CreateIssueProvider(It.Is<ProviderConfig>(c => c.Id == "prov1"))).Returns(prov1.Object);
        factory.Setup(f => f.CreateIssueProvider(It.Is<ProviderConfig>(c => c.Id == "prov2"))).Returns(prov2.Object);

        var dep = new Mock<IDependencyChecker>();
        dep.Setup(d => d.CheckAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string?>(),
                It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        var sut = new BlockedIssuesService(config.Object, factory.Object, dep.Object);

        var result = await sut.GetBacklogAsync(projectId: null, CancellationToken.None);

        Assert.Equal(3, result.Issues.Count);
        Assert.Single(result.Issues, b => b.Identifier == "10"); // deduped — first occurrence wins
        Assert.Single(result.Issues, b => b.Identifier == "11");
        Assert.Single(result.Issues, b => b.Identifier == "12");
        Assert.False(result.IsTruncated);
    }

    [Fact]
    public async Task GetBacklogAsync_WhenProviderThrowsOnPage2_KeepsPage1ResultsAndDegrades()
    {
        // Page 1 succeeds and issues are added to the backlog list before page 2 throws.
        // The page loop is inside the try/catch, but the backlog list is at outer scope — so
        // issues already accumulated from page 1 are NOT rolled back when page 2 fails.
        // This is the natural behavior: partial results from earlier pages are kept.
        var template = new PipelineJobTemplate { Id = "t1", Name = "T", IssueProviderId = "prov1", RepoProviderId = "repo1", Enabled = true };

        var config = new Mock<IPipelineApiConfigClient>();
        config.Setup(c => c.GetAllTemplatesAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new[] { template });
        config.Setup(c => c.GetProviderConfigsWithSecretsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new ProviderConfig { Id = "prov1", DisplayName = "P", Kind = ProviderKind.Issue, ProviderType = "GitHub" } });

        var provider = new Mock<IIssueProvider>();
        provider.SetupSequence(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new[]
                {
                    new IssueSummary { Identifier = "10", Title = "Page1Issue", Labels = Array.Empty<string>(), Description = "", Url = null }
                },
                Page = 1, PageSize = 50, HasMore = true
            })
            .ThrowsAsync(new InvalidOperationException("provider failed on page 2"));
        provider.SetupSequence(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new[]
                {
                    new IssueSummary { Identifier = "10", Title = "Page1Issue", Labels = Array.Empty<string>(), Description = "", Url = null }
                },
                Page = 1, PageSize = 50, HasMore = true
            })
            .ThrowsAsync(new InvalidOperationException("provider failed on page 2"));

        var factory = new Mock<IProviderFactory>();
        factory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>())).Returns(provider.Object);

        var dep = new Mock<IDependencyChecker>();
        dep.Setup(d => d.CheckAsync(It.IsAny<IssueIdentifier>(), It.IsAny<string?>(),
                It.IsAny<IIssueProvider>(), It.IsAny<Dictionary<int, bool>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DependencyCheckResult.NoDependencies);

        var sut = new BlockedIssuesService(config.Object, factory.Object, dep.Object);

        var result = await sut.GetBacklogAsync(projectId: null, CancellationToken.None);

        // Page 1 issues were added to backlog before page 2 threw — they are kept (no rollback).
        Assert.Single(result.Issues);
        Assert.Equal("10", result.Issues[0].Identifier);
        Assert.False(result.IsTruncated);
    }
}
