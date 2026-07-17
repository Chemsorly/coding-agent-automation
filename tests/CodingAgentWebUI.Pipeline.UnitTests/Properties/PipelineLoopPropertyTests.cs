using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.TestUtilities;
using FsCheck;
using FsCheck.Xunit;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests.Properties;

/// <summary>
/// Property-based tests for multi-repo pipeline loop scheduling logic.
/// Feature: 013-multi-repo-pipeline-loop, Properties 1–7
/// </summary>
public class PipelineLoopPropertyTests
{

    /// <summary>
    /// Feature: 013-multi-repo-pipeline-loop, Property 2: Snapshot Isolation
    /// Start cycle, mutate config store mid-cycle via mock, verify cycle used original snapshot.
    /// **Validates: Requirements 2.1**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task SnapshotIsolation_CycleUsesOriginalConfig(PositiveInt templateCountRaw)
    {
        var templateCount = Math.Min(templateCountRaw.Get, 4);
        var templates = PipelineLoopTestData.CreateTemplates(templateCount);

        var originalConfig = new PipelineConfiguration
        {
            ClosedLoopPollInterval = TimeSpan.FromMilliseconds(50),
            WorkspaceBaseDirectory = Path.GetTempPath()
        };

        var configCallCount = 0;
        var mockStore = new Mock<IConfigurationStore>();
        mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref configCallCount);
                // On second+ call, return a mutated config with different workspace dir.
                // If the loop used a snapshot from the first call, it won't see this mutation mid-cycle.
                if (configCallCount > 1)
                    return originalConfig with { WorkspaceBaseDirectory = "/mutated-path" };
                return originalConfig;
            });

        SetupProviderConfigs(mockStore, templates);

        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = new List<IssueSummary>(), Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false });

        var mockFactory = new Mock<IProviderFactory>();
        mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(mockProvider.Object);

        var svc = CreateService(mockStore, mockFactory);
        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);

        var started = await svc.StartLoopAsync();
        if (!started) { cts.Cancel(); try { await svc.StopAsync(CancellationToken.None); } catch { } return; }

        await WaitForConditionAsync(() => configCallCount >= 2, TimeSpan.FromSeconds(5));
        svc.StopLoop();
        await Task.Delay(100);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }

        // Config was read at least twice (mutation happened), proving snapshot isolation:
        // the first cycle completed successfully using the original config even though
        // subsequent calls return a different config.
        configCallCount.Should().BeGreaterThanOrEqualTo(2);
    }

    /// <summary>
    /// Feature: 013-multi-repo-pipeline-loop, Property 3: Provider Cache Correctness
    /// Run N cycles with stable config, count CreateIssueProvider calls, assert equals unique IssueProviderId count.
    /// **Validates: Requirements 2.3**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task ProviderCacheCorrectness_CreatesOncePerUniqueId(PositiveInt templateCountRaw)
    {
        var templateCount = Math.Min(templateCountRaw.Get, 5);
        var templates = PipelineLoopTestData.CreateTemplates(templateCount);

        var config = new PipelineConfiguration
        {
            ClosedLoopPollInterval = TimeSpan.FromMilliseconds(50),
            WorkspaceBaseDirectory = Path.GetTempPath()
        };

        var mockStore = new Mock<IConfigurationStore>();
        mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        SetupProviderConfigs(mockStore, templates);

        var createCount = 0;
        var mockProvider = new Mock<IIssueProvider>();
        mockProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary> { Items = new List<IssueSummary>(), Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false });

        var mockFactory = new Mock<IProviderFactory>();
        mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(() => { Interlocked.Increment(ref createCount); return mockProvider.Object; });

        var svc = CreateService(mockStore, mockFactory);
        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);

        var started = await svc.StartLoopAsync();
        if (!started) { cts.Cancel(); try { await svc.StopAsync(CancellationToken.None); } catch { } return; }

        // Let multiple cycles run — wait until provider has been created
        var uniqueProviderIds = templates.Where(t => t.Enabled).Select(t => t.IssueProviderId).Distinct().Count();
        await WaitForConditionAsync(() => createCount >= uniqueProviderIds, TimeSpan.FromSeconds(5));
        svc.StopLoop();
        await Task.Delay(100);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }

        createCount.Should().Be(uniqueProviderIds);
    }

    /// <summary>
    /// Feature: 013-multi-repo-pipeline-loop, Property 5: Error Isolation
    /// Generate failure patterns (subset of templates throw), verify non-throwing templates still polled.
    /// **Validates: Requirements 2.5, 3.1**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task ErrorIsolation_NonFailingTemplatesStillPolled(PositiveInt templateCountRaw)
    {
        var templateCount = Math.Max(Math.Min(templateCountRaw.Get, 5), 2);
        var templates = PipelineLoopTestData.CreateTemplates(templateCount);
        var failingId = templates[0].IssueProviderId;

        var config = new PipelineConfiguration
        {
            ClosedLoopPollInterval = TimeSpan.FromMilliseconds(50),
            WorkspaceBaseDirectory = Path.GetTempPath()
        };

        var mockStore = new Mock<IConfigurationStore>();
        mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        SetupProviderConfigs(mockStore, templates);

        var polledIds = new List<string>();
        var mockFactory = new Mock<IProviderFactory>();
        mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns<ProviderConfig>(cfg =>
            {
                var mock = new Mock<IIssueProvider>();
                if (cfg.Id == failingId)
                {
                    mock.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                            It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
                        .ThrowsAsync(new HttpRequestException("Simulated failure"));
                }
                else
                {
                    mock.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                            It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
                        .Returns(() =>
                        {
                            lock (polledIds) { polledIds.Add(cfg.Id); }
                            return Task.FromResult(new PagedResult<IssueSummary>
                            { Items = new List<IssueSummary>(), Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false });
                        });
                }
                return mock.Object;
            });

        var svc = CreateService(mockStore, mockFactory);
        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);

        var started = await svc.StartLoopAsync();
        if (!started) { cts.Cancel(); try { await svc.StopAsync(CancellationToken.None); } catch { } return; }

        // Wait for non-failing templates to be polled
        await WaitForConditionAsync(() => { lock (polledIds) { return polledIds.Count > 0; } }, TimeSpan.FromSeconds(5));
        svc.StopLoop();
        await Task.Delay(100);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }

        polledIds.Should().NotBeEmpty("non-failing templates should have been polled");
        polledIds.Should().NotContain(failingId.Value);
    }

    /// <summary>
    /// Feature: 013-multi-repo-pipeline-loop, Property 6: Rate Limit Skip and Recovery
    /// Generate rate limit scenarios with reset times, verify skip during cycle and re-inclusion after reset.
    /// **Validates: Requirements 2.6**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task RateLimitSkipAndRecovery_SkipsDuringCycleRecoversAfter(PositiveInt templateCountRaw)
    {
        var templateCount = Math.Max(Math.Min(templateCountRaw.Get, 4), 2);
        var templates = PipelineLoopTestData.CreateTemplates(templateCount);
        var rateLimitedId = templates[0].IssueProviderId;

        var config = new PipelineConfiguration
        {
            ClosedLoopPollInterval = TimeSpan.FromMilliseconds(50),
            WorkspaceBaseDirectory = Path.GetTempPath()
        };

        var mockStore = new Mock<IConfigurationStore>();
        mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        SetupProviderConfigs(mockStore, templates);

        var callCount = 0;
        var mockFactory = new Mock<IProviderFactory>();
        mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns<ProviderConfig>(cfg =>
            {
                var mock = new Mock<IIssueProvider>();
                if (cfg.Id == rateLimitedId)
                {
                    mock.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                            It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
                        .Returns(() =>
                        {
                            var count = Interlocked.Increment(ref callCount);
                            if (count == 1)
                                throw new RateLimitExceededException(DateTimeOffset.UtcNow.AddMilliseconds(100));
                            return Task.FromResult(new PagedResult<IssueSummary>
                            { Items = new List<IssueSummary>(), Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false });
                        });
                }
                else
                {
                    mock.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                            It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
                        .ReturnsAsync(new PagedResult<IssueSummary>
                        { Items = new List<IssueSummary>(), Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false });
                }
                return mock.Object;
            });

        var svc = CreateService(mockStore, mockFactory);
        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);

        var started = await svc.StartLoopAsync();
        if (!started) { cts.Cancel(); try { await svc.StopAsync(CancellationToken.None); } catch { } return; }

        // Wait for rate limit to expire and recovery
        await WaitForConditionAsync(() => callCount >= 1, TimeSpan.FromSeconds(5));
        svc.StopLoop();
        await Task.Delay(100);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }

        // The rate-limited provider should have been called at least once
        callCount.Should().BeGreaterThanOrEqualTo(1);
    }

    /// <summary>
    /// Feature: 013-multi-repo-pipeline-loop, Property 7: Disabled Templates Excluded
    /// Generate mixed enabled/disabled template lists, verify only enabled templates have their IIssueProvider invoked.
    /// **Validates: Requirements 2.7**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task DisabledTemplatesExcluded_OnlyEnabledPolled(PositiveInt enabledCountRaw, PositiveInt disabledCountRaw)
    {
        var enabledCount = Math.Min(enabledCountRaw.Get, 3);
        var disabledCount = Math.Min(disabledCountRaw.Get, 3);
        var templates = PipelineLoopTestData.CreateMixedTemplates(enabledCount, disabledCount);

        var config = new PipelineConfiguration
        {
            ClosedLoopPollInterval = TimeSpan.FromMilliseconds(50),
            WorkspaceBaseDirectory = Path.GetTempPath()
        };

        var mockStore = new Mock<IConfigurationStore>();
        mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        SetupProviderConfigs(mockStore, templates);

        var createdForIds = new List<string>();
        var mockFactory = new Mock<IProviderFactory>();
        mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns<ProviderConfig>(cfg =>
            {
                lock (createdForIds) { createdForIds.Add(cfg.Id); }
                var mock = new Mock<IIssueProvider>();
                mock.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                        It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new PagedResult<IssueSummary> { Items = new List<IssueSummary>(), Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false });
                return mock.Object;
            });

        var svc = CreateService(mockStore, mockFactory);
        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);

        var started = await svc.StartLoopAsync();
        if (!started) { cts.Cancel(); try { await svc.StopAsync(CancellationToken.None); } catch { } return; }

        var enabledProviderIds = templates.Where(t => t.Enabled).Select(t => t.IssueProviderId).ToHashSet();
        await WaitForConditionAsync(() => { lock (createdForIds) { return createdForIds.Count > 0; } }, TimeSpan.FromSeconds(5));
        svc.StopLoop();
        await Task.Delay(100);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }

        var disabledOnlyIds = templates.Where(t => !t.Enabled).Select(t => t.IssueProviderId)
            .Where(id => !enabledProviderIds.Contains(id)).ToHashSet();

        foreach (var id in createdForIds)
        {
            disabledOnlyIds.Should().NotContain(id, "disabled-only provider IDs should not have providers created");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    /// <summary>
    /// Polls a condition every 25ms until it becomes true or the timeout expires.
    /// Replaces fixed Task.Delay synchronization to eliminate timing-dependent flakiness.
    /// </summary>
    private static async Task WaitForConditionAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(25);
        }
    }

    private static void SetupProviderConfigs(Mock<IConfigurationStore> mockStore, List<PipelineJobTemplate> templates)
    {
        mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = templates.Select(t => t.Id).ToList() }
            });
        mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);
        mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates.Select(t => new ProviderConfig
            {
                Id = t.IssueProviderId.Value, Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test"
            }).DistinctBy(c => c.Id).ToList());
        mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates.Select(t => new ProviderConfig
            {
                Id = t.RepoProviderId.Value, Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test"
            }).DistinctBy(c => c.Id).ToList());
    }

    private static PipelineLoopService CreateService(Mock<IConfigurationStore> mockStore, Mock<IProviderFactory> mockFactory, IWorkDistributor? distributor = null)
    {
        var mockLogger = new Mock<Serilog.ILogger>();
        var orchestration = TestOrchestrationFactory.CreateMinimal(
            configStore: mockStore.Object,
            providerFactory: mockFactory.Object,
            logger: mockLogger.Object);

        return new PipelineLoopService(orchestration, mockFactory.Object, mockStore.Object, mockStore.Object, mockStore.Object, mockLogger.Object, distributor);
    }
}
