using AwesomeAssertions;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.Pipeline.Services;
using Microsoft.Extensions.Time.Testing;
using Moq;
using ILogger = Serilog.ILogger;

namespace CodingAgent.Orchestration.UnitTests;

/// <summary>
/// Unit tests for <see cref="ConsolidationStalenessService"/>.
/// </summary>
public sealed class ConsolidationStalenessServiceTests
{
    private readonly Mock<IPipelineRunHistoryService> _history = new(MockBehavior.Default);
    private readonly Mock<IProviderConfigStore> _providerConfigs = new(MockBehavior.Default);
    private readonly Mock<IProviderFactory> _providerFactory = new(MockBehavior.Default);
    private readonly Mock<ILogger> _logger = new(MockBehavior.Default);

    public ConsolidationStalenessServiceTests()
    {
        // ForContext<T> must return the mock logger itself so .Warning / .Error calls don't NRE.
        _logger.Setup(l => l.ForContext<ConsolidationStalenessService>()).Returns(_logger.Object);
        _logger.Setup(l => l.ForContext(It.IsAny<string>(), It.IsAny<object>(), It.IsAny<bool>())).Returns(_logger.Object);
    }

    private ConsolidationStalenessService CreateSut(TimeProvider? timeProvider = null) =>
        new(_history.Object, _providerConfigs.Object, _providerFactory.Object, _logger.Object, timeProvider);
    private static PipelineRunSummary MakeSummary(
        PipelineRunType runType = PipelineRunType.Implementation,
        PipelineStep finalStep = PipelineStep.Completed,
        bool brainUpdatesPushed = false,
        string? brainProviderConfigId = null,
        RunFeedback? feedback = null,
        string? issueIdentifier = "test-scope",
        DateTimeOffset? startedAt = null,
        DateTimeOffset? completedAt = null) => new()
        {
            RunId = Guid.NewGuid().ToString(),
            IssueIdentifier = new IssueIdentifier(issueIdentifier ?? "test-scope"),
            IssueTitle = "Test Issue",
            FinalStep = finalStep,
            RunType = runType,
            BrainUpdatesPushed = brainUpdatesPushed,
            BrainProviderConfigId = brainProviderConfigId,
            Feedback = feedback,
            StartedAtOffset = startedAt ?? DateTimeOffset.UtcNow.AddMinutes(-5),
            CompletedAtOffset = completedAt ?? DateTimeOffset.UtcNow,
        };

    private static PagedResult<PipelineRunSummary> PageOf(IEnumerable<PipelineRunSummary>? items = null) =>
        new() { Items = (items ?? []).ToList().AsReadOnly(), Page = 1, PageSize = 500, HasMore = false };

    private static PagedResult<IssueSummary> IssuePageOf(IEnumerable<IssueSummary>? items = null) =>
        new() { Items = (items ?? []).ToList().AsReadOnly(), Page = 1, PageSize = 100, HasMore = false };

    // ── CountBrainRunsSinceLastConsolidationAsync ───────────────────────────

    [Fact]
    public async Task CountBrainRuns_CountsOnlyRunsThatPushedToThisBrain()
    {
        // Arrange: runs pushed to bp-1, a run pushed to bp-2, and a consolidation run — only the
        // first two are non-consolidation runs that pushed to bp-1.
        _history
            .Setup(h => h.GetRunHistoryAsync(
                It.Is<RunHistoryQuery>(q => q.FinalStep == PipelineStep.Completed && q.RunType == PipelineRunType.Consolidation),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PageOf()); // no previous consolidation success

        _history
            .Setup(h => h.GetRunHistoryAsync(
                It.Is<RunHistoryQuery>(q => q.FinalStep == null && q.RunType == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PageOf([
                MakeSummary(brainUpdatesPushed: true, brainProviderConfigId: "bp-1"),
                MakeSummary(brainUpdatesPushed: true, brainProviderConfigId: "bp-1"),
                MakeSummary(brainUpdatesPushed: true, brainProviderConfigId: "bp-2"),
                MakeSummary(runType: PipelineRunType.Consolidation, brainUpdatesPushed: true, brainProviderConfigId: "bp-1"),
                MakeSummary(brainUpdatesPushed: false, brainProviderConfigId: "bp-1"),
            ]));

        var sut = CreateSut();

        // Act
        var count = await sut.CountBrainRunsSinceLastConsolidationAsync("bp-1", CancellationToken.None);

        // Assert: only the 2 non-consolidation runs that pushed to bp-1
        count.Should().Be(2);
    }

    [Fact]
    public async Task CountBrainRuns_ExcludesConsolidationRuns()
    {
        _history
            .Setup(h => h.GetRunHistoryAsync(
                It.Is<RunHistoryQuery>(q => q.FinalStep == PipelineStep.Completed && q.RunType == PipelineRunType.Consolidation),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PageOf());

        // Only a consolidation run pushed to the brain — should not be counted
        _history
            .Setup(h => h.GetRunHistoryAsync(
                It.Is<RunHistoryQuery>(q => q.FinalStep == null && q.RunType == null),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PageOf([
                MakeSummary(runType: PipelineRunType.Consolidation, brainUpdatesPushed: true, brainProviderConfigId: "bp-1"),
            ]));

        var sut = CreateSut();
        var count = await sut.CountBrainRunsSinceLastConsolidationAsync("bp-1", CancellationToken.None);

        count.Should().Be(0);
    }

    // ── CountClosedIssuesSinceLastScanAsync ────────────────────────────────

    [Fact]
    public async Task CountClosedIssuesSinceLastScan_IgnoresScanIssues()
    {
        var template = MakeTemplate();

        SetupProviderConfig(template.IssueProviderId);

        var scanIssue = MakeIssueSummary(
            description: "Prefix\n\n" + RefactoringScanIssues.Footer);
        var normalIssue = MakeIssueSummary(description: "Normal issue description");

        var mockProvider = SetupIssueProvider();
        mockProvider
            .Setup(p => p.ListClosedIssuesAsync(1, 100, null, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(IssuePageOf([scanIssue, normalIssue]));

        _history
            .Setup(h => h.GetRunHistoryAsync(
                It.Is<RunHistoryQuery>(q => q.FinalStep == PipelineStep.Completed && q.RunType == PipelineRunType.Consolidation),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PageOf());

        var sut = CreateSut();
        var count = await sut.CountClosedIssuesSinceLastScanAsync(template, CancellationToken.None);

        count.Should().Be(1, "scan issues are excluded; only the normal issue counts");
    }

    [Fact]
    public async Task CountClosedIssuesSinceLastScan_MissingIssueProviderConfig_ReturnsZero()
    {
        var template = MakeTemplate();

        _providerConfigs
            .Setup(s => s.GetProviderConfigByIdAsync(template.IssueProviderId, ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync((ProviderConfig?)null);

        var sut = CreateSut();
        var count = await sut.CountClosedIssuesSinceLastScanAsync(template, CancellationToken.None);

        count.Should().Be(0);
    }

    [Fact]
    public async Task CountClosedIssuesSinceLastScan_NoLastScan_FallsBackTo30DayWindow()
    {
        var template = MakeTemplate();
        var fakeTime = new FakeTimeProvider();
        var now = new DateTimeOffset(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
        fakeTime.SetUtcNow(now);

        SetupProviderConfig(template.IssueProviderId);

        DateTime? capturedSince = null;
        var mockProvider = SetupIssueProvider();
        mockProvider
            .Setup(p => p.ListClosedIssuesAsync(1, 100, null, It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
            .Callback<int, int, IReadOnlyList<string>?, DateTime?, CancellationToken>((_, _, _, since, _) =>
                capturedSince = since)
            .ReturnsAsync(IssuePageOf());

        _history
            .Setup(h => h.GetRunHistoryAsync(
                It.Is<RunHistoryQuery>(q => q.FinalStep == PipelineStep.Completed && q.RunType == PipelineRunType.Consolidation),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PageOf()); // no previous scan

        var sut = CreateSut(fakeTime);
        await sut.CountClosedIssuesSinceLastScanAsync(template, CancellationToken.None);

        capturedSince.Should().NotBeNull();
        capturedSince!.Value.Should().BeCloseTo(now.AddDays(-30).UtcDateTime, TimeSpan.FromSeconds(1),
            "fallback window is 30 days before now");
    }

    // ── IsPausedAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task IsPaused_LatestRunFailed_ReturnsTrue()
    {
        const string scopeKey = "BrainConsolidation:bp-1";
        var failedRun = MakeSummary(
            issueIdentifier: scopeKey,
            finalStep: PipelineStep.Failed,
            startedAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        _history
            .Setup(h => h.GetRunHistoryAsync(
                It.Is<RunHistoryQuery>(q => q.RunType == PipelineRunType.Consolidation),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PageOf([failedRun]));

        var sut = CreateSut();
        var paused = await sut.IsPausedAsync(scopeKey, CancellationToken.None);

        paused.Should().BeTrue();
    }

    [Fact]
    public async Task IsPaused_LatestRunSucceededAfterAFailure_ReturnsFalse()
    {
        const string scopeKey = "BrainConsolidation:bp-1";
        var earlierFailed = MakeSummary(
            issueIdentifier: scopeKey,
            finalStep: PipelineStep.Failed,
            startedAt: DateTimeOffset.UtcNow.AddMinutes(-20));
        var laterSucceeded = MakeSummary(
            issueIdentifier: scopeKey,
            finalStep: PipelineStep.Completed,
            startedAt: DateTimeOffset.UtcNow.AddMinutes(-5));

        _history
            .Setup(h => h.GetRunHistoryAsync(
                It.Is<RunHistoryQuery>(q => q.RunType == PipelineRunType.Consolidation),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PageOf([earlierFailed, laterSucceeded]));

        var sut = CreateSut();
        var paused = await sut.IsPausedAsync(scopeKey, CancellationToken.None);

        paused.Should().BeFalse("the latest run succeeded; a failed earlier run does not override");
    }

    [Fact]
    public async Task IsPaused_NoRunsForScope_ReturnsFalse()
    {
        _history
            .Setup(h => h.GetRunHistoryAsync(
                It.Is<RunHistoryQuery>(q => q.RunType == PipelineRunType.Consolidation),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(PageOf()); // no runs at all

        var sut = CreateSut();
        var paused = await sut.IsPausedAsync("BrainConsolidation:bp-x", CancellationToken.None);

        paused.Should().BeFalse();
    }

    // ── Helpers ────────────────────────────────────────────────────────────
    // NOTE [WARNING]: CountOpenScanIssuesAsync has no direct unit test. The method shares
    // the provider-lookup pattern with CountClosedIssuesSinceLastScanAsync but filters for
    // IsScanIssue == true (not false). An inverted filter or nil-config early-return regression
    // would not be caught because AutoConsolidationServiceTests mocks the method rather than
    // exercising the real implementation. Add a direct test covering: (a) scan issues are counted,
    // (b) non-scan issues are excluded, (c) null provider config returns 0.

    private static PipelineJobTemplate MakeTemplate() => new()
    {
        Id = "tmpl-stale-1",
        Name = "Stale Test Repo",
        IssueProviderId = "ip-stale",
        RepoProviderId = "rp-stale",
        Enabled = true,
    };

    private static IssueSummary MakeIssueSummary(string? description = null) => new()
    {
        Identifier = "42",
        Title = "Some Issue",
        Labels = [],
        Description = description,
    };

    private void SetupProviderConfig(string configId)
    {
        var config = new ProviderConfig { Id = configId, Kind = ProviderKind.Issue, DisplayName = "Test Provider", ProviderType = "github" };
        _providerConfigs
            .Setup(s => s.GetProviderConfigByIdAsync(configId, ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
    }

    private Mock<IIssueProvider> SetupIssueProvider()
    {
        var mockProvider = new Mock<IIssueProvider>(MockBehavior.Default);
        mockProvider.Setup(p => p.ValidateAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        mockProvider.Setup(p => p.DisposeAsync()).Returns(ValueTask.CompletedTask);
        // Default empty results for open issues (overridden per-test when needed)
        mockProvider
            .Setup(p => p.ListOpenIssuesAsync(1, 100, (IReadOnlyList<string>?)null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(IssuePageOf());
        _providerFactory
            .Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockProvider.Object);
        return mockProvider;
    }
}
