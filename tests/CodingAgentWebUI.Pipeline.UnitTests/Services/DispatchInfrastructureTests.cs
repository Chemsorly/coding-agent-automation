using AwesomeAssertions;
using Moq;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using Serilog;

namespace CodingAgentWebUI.Pipeline.UnitTests;

/// <summary>
/// Verifies that <see cref="DispatchInfrastructure"/> correctly aggregates shared
/// dispatch dependencies, reducing constructor bloat in AgentJobDispatcher (11→8 deps)
/// and DispatchOrchestrationService (7→4 deps).
/// </summary>
public class DispatchInfrastructureTests
{
    private readonly Mock<ITokenVendingService> _mockTokenVending = new();
    private readonly Mock<IProviderFactory> _mockProviderFactory = new();
    private readonly Mock<ILabelService> _mockLabelService = new();
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();

    private DispatchInfrastructure CreateInfrastructure()
    {
        var resolution = new DispatchResolutionService(
            new ProfileResolver(),
            new QualityGateResolver(),
            new ReviewerResolver(),
            _mockConfigStore.Object,
            new Mock<ILogger>().Object);

        return new DispatchInfrastructure(
            _mockTokenVending.Object,
            _mockProviderFactory.Object,
            _mockLabelService.Object,
            resolution);
    }

    // ── Construction ──

    [Fact]
    public void Constructor_NullTokenVending_Throws()
    {
        var resolution = new DispatchResolutionService(
            new ProfileResolver(), new QualityGateResolver(), new ReviewerResolver(),
            _mockConfigStore.Object, new Mock<ILogger>().Object);

        var act = () => new DispatchInfrastructure(
            null!, _mockProviderFactory.Object, _mockLabelService.Object, resolution);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullProviderFactory_Throws()
    {
        var resolution = new DispatchResolutionService(
            new ProfileResolver(), new QualityGateResolver(), new ReviewerResolver(),
            _mockConfigStore.Object, new Mock<ILogger>().Object);

        var act = () => new DispatchInfrastructure(
            _mockTokenVending.Object, null!, _mockLabelService.Object, resolution);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullLabelService_Throws()
    {
        var resolution = new DispatchResolutionService(
            new ProfileResolver(), new QualityGateResolver(), new ReviewerResolver(),
            _mockConfigStore.Object, new Mock<ILogger>().Object);

        var act = () => new DispatchInfrastructure(
            _mockTokenVending.Object, _mockProviderFactory.Object, null!, resolution);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Constructor_NullResolution_Throws()
    {
        var act = () => new DispatchInfrastructure(
            _mockTokenVending.Object, _mockProviderFactory.Object, _mockLabelService.Object, null!);

        act.Should().Throw<ArgumentNullException>();
    }

    // ── Property Access ──

    [Fact]
    public void Properties_ExposeInjectedDependencies()
    {
        var infra = CreateInfrastructure();

        infra.TokenVending.Should().BeSameAs(_mockTokenVending.Object);
        infra.ProviderFactory.Should().BeSameAs(_mockProviderFactory.Object);
        infra.LabelService.Should().BeSameAs(_mockLabelService.Object);
        infra.Resolution.Should().NotBeNull();
    }

    // ── ConfigStore convenience accessor ──

    [Fact]
    public void ConfigStore_DelegatesToResolution()
    {
        var infra = CreateInfrastructure();

        // DispatchResolutionService.ConfigStore is the same store passed in
        infra.Resolution.ConfigStore.Should().BeSameAs(_mockConfigStore.Object);
    }

    // TODO: Add dedicated unit tests for PrepareAndResolveConfigAsync and BuildSyntheticIssueContext
    // (extracted from AgentJobDispatcher.Execution.cs in #1733). PrepareAndResolveConfigAsync
    // orchestrates PrepareProviderConfigsAsync → PipelineConfigurationResolver.ResolveAsync and
    // should be tested for: provider config build failure, pipeline config load failure, template
    // load failure, and successful resolution. BuildSyntheticIssueContext should be tested for
    // null description coalescing and IssueDescriptionParser.Parse interaction.

    // ── BuildSyntheticIssueContext ─────────────────────────────────────────

    [Fact]
    public void BuildSyntheticIssueContext_NullDescription_DescriptionIsEmpty()
    {
        var (issueDetail, _) = DispatchInfrastructure.BuildSyntheticIssueContext(
            identifier: "owner/repo#1",
            title: "Some Title",
            description: null);

        issueDetail.Description.Should().Be(string.Empty);
    }

    [Fact]
    public void BuildSyntheticIssueContext_NullDescription_LabelsIsEmpty()
    {
        var (issueDetail, _) = DispatchInfrastructure.BuildSyntheticIssueContext(
            identifier: "owner/repo#1",
            title: "Some Title",
            description: null);

        issueDetail.Labels.Should().BeEmpty();
    }

    [Fact]
    public void BuildSyntheticIssueContext_NonNullDescription_PassesThroughDescription()
    {
        const string desc = "This is the description.";

        var (issueDetail, _) = DispatchInfrastructure.BuildSyntheticIssueContext(
            identifier: "owner/repo#2",
            title: "Another Title",
            description: desc);

        issueDetail.Description.Should().Be(desc);
        issueDetail.Labels.Should().BeEmpty();
    }

    [Fact]
    public void BuildSyntheticIssueContext_EmptyStringDescription_DescriptionIsEmpty()
    {
        var (issueDetail, _) = DispatchInfrastructure.BuildSyntheticIssueContext(
            identifier: "owner/repo#3",
            title: "Title",
            description: string.Empty);

        // Empty string is NOT coalesced — null-coalescing only applies to null
        issueDetail.Description.Should().Be(string.Empty);
    }
}

// ── Staleness detection tests (1F-001) ────────────────────────────────────────

public class DispatchInfrastructureStalenessTests
{
    private readonly Mock<ITokenVendingService> _mockTokenVending = new();
    private readonly Mock<IProviderFactory> _mockProviderFactory = new();
    private readonly Mock<ILabelService> _mockLabelService = new();
    private readonly Mock<IConfigurationStore> _mockConfigStore = new();
    private readonly Mock<IPipelineApiWorkItemClient> _mockWorkItemClient = new();

    private DispatchInfrastructure CreateInfrastructure(bool includeWorkItemClient = false)
    {
        var resolution = new DispatchResolutionService(
            new ProfileResolver(), new QualityGateResolver(), new ReviewerResolver(),
            _mockConfigStore.Object, new Mock<Serilog.ILogger>().Object);

        return new DispatchInfrastructure(
            _mockTokenVending.Object,
            _mockProviderFactory.Object,
            _mockLabelService.Object,
            resolution,
            includeWorkItemClient ? _mockWorkItemClient.Object : null);
    }

    private static DispatchInfrastructure.IssueContextResult MakeContextWithAnalysis(
        IReadOnlyList<IssueComment>? comments = null)
    {
        var analysisComment = new IssueComment
        {
            Id = "c1",
            Body = $"{CommentMarkers.AnalysisHeader}\nsome analysis",
            Author = "bot",
            CreatedAt = DateTime.UtcNow.AddHours(-1)
        };

        return new DispatchInfrastructure.IssueContextResult(
            new IssueDetail { Identifier = "org/repo#1", Title = "T", Description = "", Labels = [] },
            new ParsedIssue { AcceptanceCriteria = [], RequirementsSection = "" },
            comments ?? [analysisComment],
            ExistingAnalysis: analysisComment.Body,
            ForceRefreshAnalysis: false,
            StalenessSignal: null,
            RefreshCount: 0);
    }

    private static ProviderConfig MakeRepoConfig(string id) => new()
    {
        Id = id,
        Kind = ProviderKind.Repository,
        ProviderType = "Test",
        DisplayName = "Test Provider"
    };

    // ── CheckCommitCountStalenessAsync ────────────────────────────────────

    [Fact]
    public async Task CheckCommitCountStaleness_NoRepoConfig_ReturnsFalse()
    {
        var infra = CreateInfrastructure();
        var context = MakeContextWithAnalysis();

        var (forceRefresh, signal) = await infra.CheckCommitCountStalenessAsync(
            context, new ProviderConfigId("missing"), [], 30,
            new Mock<Serilog.ILogger>().Object, CancellationToken.None);

        forceRefresh.Should().BeFalse();
        signal.Should().BeNull();
    }

    [Fact]
    public async Task CheckCommitCountStaleness_ProviderNotAnalytics_ReturnsFalse()
    {
        var infra = CreateInfrastructure();
        var context = MakeContextWithAnalysis();
        var repoConfig = MakeRepoConfig("rp-1");

        var nonAnalyticsProvider = new Mock<IRepositoryProvider>();
        nonAnalyticsProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _mockProviderFactory.Setup(f => f.CreateRepositoryProvider(repoConfig)).Returns(nonAnalyticsProvider.Object);

        var (forceRefresh, signal) = await infra.CheckCommitCountStalenessAsync(
            context, new ProviderConfigId("rp-1"), [repoConfig], 30,
            new Mock<Serilog.ILogger>().Object, CancellationToken.None);

        forceRefresh.Should().BeFalse();
        signal.Should().BeNull();
    }

    [Fact]
    public async Task CheckCommitCountStaleness_NoAnalysisComment_ReturnsFalse()
    {
        var infra = CreateInfrastructure();
        var contextNoAnalysis = MakeContextWithAnalysis(comments: [
            new IssueComment { Id = "c1", Body = "regular comment", Author = "user", CreatedAt = DateTime.UtcNow }
        ]);
        var repoConfig = MakeRepoConfig("rp-1");

        var analyticsProvider = new Mock<IRepositoryProvider>();
        analyticsProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _mockProviderFactory.Setup(f => f.CreateRepositoryProvider(repoConfig)).Returns(analyticsProvider.Object);

        var (forceRefresh, signal) = await infra.CheckCommitCountStalenessAsync(
            contextNoAnalysis, new ProviderConfigId("rp-1"), [repoConfig], 30,
            new Mock<Serilog.ILogger>().Object, CancellationToken.None);

        forceRefresh.Should().BeFalse();
        signal.Should().BeNull();
    }

    [Fact]
    public async Task CheckCommitCountStaleness_CommitsBelowThreshold_ReturnsFalse()
    {
        var infra = CreateInfrastructure();
        var context = MakeContextWithAnalysis();
        var repoConfig = MakeRepoConfig("rp-1");

        var analyticsProvider = new Mock<IRepositoryProvider>();
        analyticsProvider.Setup(p => p.GetCommitCountSinceAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(5);
        analyticsProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _mockProviderFactory.Setup(f => f.CreateRepositoryProvider(repoConfig)).Returns(analyticsProvider.Object);

        var (forceRefresh, signal) = await infra.CheckCommitCountStalenessAsync(
            context, new ProviderConfigId("rp-1"), [repoConfig], 30,
            new Mock<Serilog.ILogger>().Object, CancellationToken.None);

        forceRefresh.Should().BeFalse();
        signal.Should().BeNull();
    }

    [Fact]
    public async Task CheckCommitCountStaleness_CommitsAtThreshold_ReturnsForceRefresh()
    {
        var infra = CreateInfrastructure();
        var context = MakeContextWithAnalysis();
        var repoConfig = MakeRepoConfig("rp-1");

        var analyticsProvider = new Mock<IRepositoryProvider>();
        analyticsProvider.Setup(p => p.GetCommitCountSinceAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(30);
        analyticsProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _mockProviderFactory.Setup(f => f.CreateRepositoryProvider(repoConfig)).Returns(analyticsProvider.Object);

        var (forceRefresh, signal) = await infra.CheckCommitCountStalenessAsync(
            context, new ProviderConfigId("rp-1"), [repoConfig], 30,
            new Mock<Serilog.ILogger>().Object, CancellationToken.None);

        forceRefresh.Should().BeTrue();
        signal.Should().Be("commit_threshold");
    }

    [Fact]
    public async Task CheckCommitCountStaleness_ProviderThrows_ReturnsFalseNonFatal()
    {
        var infra = CreateInfrastructure();
        var context = MakeContextWithAnalysis();
        var repoConfig = MakeRepoConfig("rp-1");

        var analyticsProvider = new Mock<IRepositoryProvider>();
        analyticsProvider.Setup(p => p.GetCommitCountSinceAsync(It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("provider error"));
        analyticsProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _mockProviderFactory.Setup(f => f.CreateRepositoryProvider(repoConfig)).Returns(analyticsProvider.Object);

        var (forceRefresh, signal) = await infra.CheckCommitCountStalenessAsync(
            context, new ProviderConfigId("rp-1"), [repoConfig], 30,
            new Mock<Serilog.ILogger>().Object, CancellationToken.None);

        forceRefresh.Should().BeFalse("provider failure is non-fatal");
        signal.Should().BeNull();
    }

    // ── BuildIssueContextAsync — agent-error staleness path ──────────────

    [Fact]
    public async Task BuildIssueContext_WithWorkItemClient_AgentErrorSince_SetsForceRefresh()
    {
        var issueConfig = new ProviderConfig { Id = "ip-1", Kind = ProviderKind.Issue, ProviderType = "Test", DisplayName = "IP" };
        _mockConfigStore.Setup(s => s.GetProviderConfigByIdAsync("ip-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);

        var analysisComment = new IssueComment
        {
            Id = "c1",
            Body = $"{CommentMarkers.AnalysisHeader}\ncontent",
            Author = "bot",
            CreatedAt = DateTime.UtcNow.AddHours(-2)
        };

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail { Identifier = "org/repo#1", Title = "T", Description = "", Labels = [] });
        mockIssueProvider.Setup(p => p.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<IssueComment>)[analysisComment]);
        mockIssueProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockIssueProvider.Object);

        _mockWorkItemClient.Setup(c => c.GetStalenessAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkItemStalenessResult { HasAgentErrorSince = true, LastSuccessfulCompletion = null });

        var infra = CreateInfrastructure(includeWorkItemClient: true);

        var result = await infra.BuildIssueContextAsync(
            new IssueIdentifier("org/repo#1"), new ProviderConfigId("ip-1"), CancellationToken.None);

        result.Should().NotBeNull();
        result!.ForceRefreshAnalysis.Should().BeTrue("agent errored since last analysis");
        result.StalenessSignal.Should().Be("agent_error_since");
    }

    [Fact]
    public async Task BuildIssueContext_WithWorkItemClient_NoAgentError_DoesNotForceRefresh()
    {
        var issueConfig = new ProviderConfig { Id = "ip-1", Kind = ProviderKind.Issue, ProviderType = "Test", DisplayName = "IP" };
        _mockConfigStore.Setup(s => s.GetProviderConfigByIdAsync("ip-1", ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(issueConfig);

        var analysisComment = new IssueComment
        {
            Id = "c1",
            Body = $"{CommentMarkers.AnalysisHeader}\ncontent",
            Author = "bot",
            CreatedAt = DateTime.UtcNow.AddHours(-2)
        };

        var mockIssueProvider = new Mock<IIssueProvider>();
        mockIssueProvider.Setup(p => p.GetIssueAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IssueDetail { Identifier = "org/repo#1", Title = "T", Description = "", Labels = [] });
        mockIssueProvider.Setup(p => p.ListCommentsAsync(It.IsAny<IssueIdentifier>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<IssueComment>)[analysisComment]);
        mockIssueProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        _mockProviderFactory.Setup(f => f.CreateIssueProvider(issueConfig)).Returns(mockIssueProvider.Object);

        _mockWorkItemClient.Setup(c => c.GetStalenessAsync(
            It.IsAny<string>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkItemStalenessResult { HasAgentErrorSince = false, LastSuccessfulCompletion = null });

        var infra = CreateInfrastructure(includeWorkItemClient: true);

        var result = await infra.BuildIssueContextAsync(
            new IssueIdentifier("org/repo#1"), new ProviderConfigId("ip-1"), CancellationToken.None);

        result.Should().NotBeNull();
        result!.ForceRefreshAnalysis.Should().BeFalse();
        result.StalenessSignal.Should().BeNull();
    }
}

