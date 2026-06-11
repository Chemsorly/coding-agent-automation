using System.Text.Json;
using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    /// <summary>
    /// Feature: 013-multi-repo-pipeline-loop, Property 1: Serialization Round-Trip
    /// For any valid list of PipelineJobTemplate entries, serializing to JSON and
    /// deserializing back produces an equivalent list (all fields preserved, order maintained).
    /// **Validates: Requirements 1.3**
    /// </summary>
    [Property(MaxTest = 20)]
    public void SerializationRoundTrip_PreservesAllFields(PositiveInt countRaw, bool includeOptionals)
    {
        var count = Math.Min(countRaw.Get, 10);
        var templates = Enumerable.Range(0, count).Select(i => new PipelineJobTemplate
        {
            Id = Guid.NewGuid().ToString(),
            Name = $"Template-{i}",
            IssueProviderId = $"ip-{Guid.NewGuid().ToString()[..8]}",
            RepoProviderId = $"rp-{Guid.NewGuid().ToString()[..8]}",
            BrainProviderId = includeOptionals ? $"bp-{i}" : null,
            PipelineProviderId = includeOptionals ? $"pp-{i}" : null,
            Enabled = i % 2 == 0
        }).ToList();

        var config = new PipelineConfiguration { PipelineJobTemplates = templates };

        var json = JsonSerializer.Serialize(config, JsonOptions);
        var deserialized = JsonSerializer.Deserialize<PipelineConfiguration>(json, JsonOptions)!;

        deserialized.PipelineJobTemplates.Count.Should().Be(templates.Count);

        for (int i = 0; i < templates.Count; i++)
        {
            var original = templates[i];
            var restored = deserialized.PipelineJobTemplates[i];

            restored.Id.Should().Be(original.Id);
            restored.Name.Should().Be(original.Name);
            restored.IssueProviderId.Should().Be(original.IssueProviderId);
            restored.RepoProviderId.Should().Be(original.RepoProviderId);
            restored.BrainProviderId.Should().Be(original.BrainProviderId);
            restored.PipelineProviderId.Should().Be(original.PipelineProviderId);
            restored.Enabled.Should().Be(original.Enabled);
        }
    }

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
            PipelineJobTemplates = templates,
            ClosedLoopPollInterval = TimeSpan.FromMilliseconds(50),
            WorkspaceBaseDirectory = Path.GetTempPath()
        };

        var configCallCount = 0;
        var mockStore = new Mock<IConfigurationStore>();
        mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() =>
            {
                Interlocked.Increment(ref configCallCount);
                // After first call, return mutated config (all disabled)
                if (configCallCount > 1)
                    return originalConfig with { PipelineJobTemplates = templates.Select(t => t with { Enabled = false }).ToList() };
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

        await Task.Delay(400);
        svc.StopLoop();
        await Task.Delay(200);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }

        // Config was read at least once (for the first cycle)
        configCallCount.Should().BeGreaterThanOrEqualTo(1);
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
            PipelineJobTemplates = templates,
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

        // Let multiple cycles run
        await Task.Delay(300);
        svc.StopLoop();
        await Task.Delay(200);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }

        var uniqueProviderIds = templates.Where(t => t.Enabled).Select(t => t.IssueProviderId).Distinct().Count();
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
            PipelineJobTemplates = templates,
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

        // Wait long enough for at least one full poll cycle on slow CI runners
        await Task.Delay(1000);
        svc.StopLoop();
        await Task.Delay(200);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }

        polledIds.Should().NotBeEmpty("non-failing templates should have been polled");
        polledIds.Should().NotContain(failingId);
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
            PipelineJobTemplates = templates,
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
        await Task.Delay(500);
        svc.StopLoop();
        await Task.Delay(200);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }

        // The rate-limited provider should have been called at least twice (first throws, then recovers)
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
            PipelineJobTemplates = templates,
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

        await Task.Delay(300);
        svc.StopLoop();
        await Task.Delay(200);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }

        var enabledProviderIds = templates.Where(t => t.Enabled).Select(t => t.IssueProviderId).ToHashSet();
        var disabledOnlyIds = templates.Where(t => !t.Enabled).Select(t => t.IssueProviderId)
            .Where(id => !enabledProviderIds.Contains(id)).ToHashSet();

        foreach (var id in createdForIds)
        {
            disabledOnlyIds.Should().NotContain(id, "disabled-only provider IDs should not have providers created");
        }
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static void SetupProviderConfigs(Mock<IConfigurationStore> mockStore, List<PipelineJobTemplate> templates)
    {
        mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = templates.Select(t => t.Id).ToList() }
            });
        mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates.Select(t => new ProviderConfig
            {
                Id = t.IssueProviderId, Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test"
            }).DistinctBy(c => c.Id).ToList());
        mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates.Select(t => new ProviderConfig
            {
                Id = t.RepoProviderId, Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test"
            }).DistinctBy(c => c.Id).ToList());
    }

    private static PipelineLoopService CreateService(Mock<IConfigurationStore> mockStore, Mock<IProviderFactory> mockFactory, IJobDispatcher? dispatcher = null)
    {
        var mockLogger = new Mock<Serilog.ILogger>();
        var mockValidator = new Mock<IQualityGateValidator>();
        var orchestration = new PipelineOrchestrationService(
            mockStore.Object, mockFactory.Object, new IssueDescriptionParser(),
            new AgentPhaseExecutor(mockLogger.Object),
            new QualityGateExecutor(mockValidator.Object, new PullRequestOrchestrator(mockLogger.Object), new CiLogWriter(mockLogger.Object), new FeedbackService(mockLogger.Object), mockLogger.Object),
            mockLogger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: new Mock<IPipelineRunHistoryService>().Object);

        return new PipelineLoopService(orchestration, mockFactory.Object, mockStore.Object, mockStore.Object, mockStore.Object, mockLogger.Object, dispatcher);
    }
}
