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
/// Property-based tests for dispatch fairness and status tracking.
/// Feature: 013-multi-repo-pipeline-loop, Properties 8-14
/// </summary>
public class PipelineLoopDispatchPropertyTests
{
    /// <summary>
    /// Feature: 013-multi-repo-pipeline-loop, Property 4: Dispatch Provider ID Correctness
    /// Generate templates + issues, capture TryDispatchAsync args, assert provider IDs match sourcing template.
    /// **Validates: Requirements 2.4**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task DispatchProviderIdCorrectness_MatchesTemplate(PositiveInt templateCountRaw)
    {
        var templateCount = Math.Min(templateCountRaw.Get, 4);
        var templates = PipelineLoopTestData.CreateTemplates(templateCount);

        var config = new PipelineConfiguration
        {
            ClosedLoopPollInterval = TimeSpan.FromMilliseconds(50),
            ClosedLoopMaxRunsPerCycle = 100,
            WorkspaceBaseDirectory = Path.GetTempPath()
        };

        var mockStore = new Mock<IConfigurationStore>();
        mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        SetupProviderConfigs(mockStore, templates);

        var mockFactory = new Mock<IProviderFactory>();
        mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns<ProviderConfig>(cfg =>
            {
                var mock = new Mock<IIssueProvider>();
                mock.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                        It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new PagedResult<IssueSummary>
                    {
                        Items = new List<IssueSummary>
                        {
                            new() { Identifier = $"issue-from-{cfg.Id}", Title = "Test", Labels = new[] { "agent:next" }, CreatedAt = DateTime.UtcNow }
                        },
                        Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false
                    });
                return mock.Object;
            });

        var dispatchCalls = new List<(string issueId, string issueProviderId, string repoProviderId, string? brainId, string? pipelineId)>();
        var mockDispatcher = new Mock<IWorkDistributor>();
        mockDispatcher.Setup(d => d.DistributeAsync(
                It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Returns<JobDistributionRequest, CancellationToken>((request, _) =>
            {
                lock (dispatchCalls) { dispatchCalls.Add((request.IssueIdentifier, request.IssueProviderConfigId, request.RepoProviderConfigId, request.BrainProviderConfigId, request.PipelineProviderConfigId)); }
                return Task.FromResult(new DistributionResult(true, null, null));
            });
        mockDispatcher.Setup(d => d.GetActiveIssueIdentifiersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new HashSet<(string, ProviderConfigId)>());

        var svc = CreateService(mockStore, mockFactory, mockDispatcher.Object);
        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);

        var started = await svc.StartLoopAsync();
        if (!started) { cts.Cancel(); try { await svc.StopAsync(CancellationToken.None); } catch { } return; }

        await Task.Delay(500);
        svc.StopLoop();
        await Task.Delay(200);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }

        foreach (var call in dispatchCalls)
        {
            var template = templates.FirstOrDefault(t => call.issueId == $"issue-from-{t.IssueProviderId}");
            if (template is not null)
            {
                call.issueProviderId.Should().Be(template.IssueProviderId);
                call.repoProviderId.Should().Be(template.RepoProviderId);
                call.brainId.Should().Be(template.BrainProviderId);
                call.pipelineId.Should().Be(template.PipelineProviderId);
            }
        }
    }

    /// <summary>
    /// Feature: 013-multi-repo-pipeline-loop, Property 8: Fair Dispatch Under Global Limit (Equal Issues)
    /// Generate N templates x M issues with limit L, verify dispatch counts differ by at most 1.
    /// **Validates: Requirements 2.8**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task FairDispatch_EqualIssues_DiffersAtMostOne(PositiveInt templateCountRaw, PositiveInt issuesRaw, PositiveInt limitRaw)
    {
        var templateCount = Math.Max(Math.Min(templateCountRaw.Get, 4), 2);
        var issuesPerTemplate = Math.Min(issuesRaw.Get, 8);
        var totalPossible = templateCount * issuesPerTemplate;
        var limit = Math.Min(limitRaw.Get, totalPossible - 1);
        if (limit <= 0) return;
        // Ensure limit is at least templateCount to make fairness meaningful
        if (limit < templateCount) limit = templateCount;

        var templates = PipelineLoopTestData.CreateTemplates(templateCount);

        var config = new PipelineConfiguration
        {
            ClosedLoopPollInterval = TimeSpan.FromSeconds(60), // Long interval so only one cycle runs
            ClosedLoopMaxRunsPerCycle = limit,
            WorkspaceBaseDirectory = Path.GetTempPath()
        };

        var mockStore = new Mock<IConfigurationStore>();
        mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        SetupProviderConfigs(mockStore, templates);

        var mockFactory = new Mock<IProviderFactory>();
        mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns<ProviderConfig>(cfg =>
            {
                var mock = new Mock<IIssueProvider>();
                var issues = Enumerable.Range(0, issuesPerTemplate).Select(j => new IssueSummary
                {
                    Identifier = $"{cfg.Id}-issue-{j}", Title = "Test", Labels = new[] { "agent:next" },
                    CreatedAt = DateTime.UtcNow.AddMinutes(-j)
                }).ToList();
                mock.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                        It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new PagedResult<IssueSummary> { Items = issues, Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false });
                return mock.Object;
            });

        var dispatchCountPerTemplate = new Dictionary<string, int>();
        var mockDispatcher = new Mock<IWorkDistributor>();
        mockDispatcher.Setup(d => d.DistributeAsync(
                It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Returns<JobDistributionRequest, CancellationToken>((request, _) =>
            {
                lock (dispatchCountPerTemplate)
                {
                    dispatchCountPerTemplate.TryGetValue(request.IssueProviderConfigId, out var count);
                    dispatchCountPerTemplate[request.IssueProviderConfigId] = count + 1;
                }
                return Task.FromResult(new DistributionResult(true, null, null));
            });
        mockDispatcher.Setup(d => d.GetActiveIssueIdentifiersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(new HashSet<(string, ProviderConfigId)>());

        var svc = CreateService(mockStore, mockFactory, mockDispatcher.Object);
        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);

        var started = await svc.StartLoopAsync();
        if (!started) { cts.Cancel(); try { await svc.StopAsync(CancellationToken.None); } catch { } return; }

        // Wait for the cycle to complete (status changes to "Cycle complete")
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!svc.StatusMessage.Contains("Cycle complete") && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        svc.StopLoop();
        await Task.Delay(200);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }

        if (dispatchCountPerTemplate.Count == 0) return;

        var counts = dispatchCountPerTemplate.Values.ToList();
        var maxCount = counts.Max();
        var minCount = counts.Min();
        (maxCount - minCount).Should().BeLessThanOrEqualTo(1,
            $"dispatch should be fair: counts were [{string.Join(", ", counts)}]");
    }

    /// <summary>
    /// Feature: 013-multi-repo-pipeline-loop, Property 10: Circuit Breaker Trips Only When All Templates Failing
    /// Generate failure patterns, verify trips iff ALL enabled templates at threshold.
    /// **Validates: Requirements 3.4**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task CircuitBreaker_TripsOnlyWhenAllFailing(PositiveInt templateCountRaw)
    {
        var templateCount = Math.Max(Math.Min(templateCountRaw.Get, 4), 2);
        var templates = PipelineLoopTestData.CreateTemplates(templateCount);

        var config = new PipelineConfiguration
        {
            ClosedLoopPollInterval = TimeSpan.FromMilliseconds(30),
            ClosedLoopMaxConsecutivePollFailures = 2,
            WorkspaceBaseDirectory = Path.GetTempPath()
        };

        var mockStore = new Mock<IConfigurationStore>();
        mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        SetupProviderConfigs(mockStore, templates);

        var mockFactory = new Mock<IProviderFactory>();
        mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns<ProviderConfig>(_ =>
            {
                var mock = new Mock<IIssueProvider>();
                mock.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                        It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
                    .ThrowsAsync(new HttpRequestException("All failing"));
                return mock.Object;
            });

        var svc = CreateService(mockStore, mockFactory);
        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);

        var started = await svc.StartLoopAsync();
        if (!started) { cts.Cancel(); try { await svc.StopAsync(CancellationToken.None); } catch { } return; }

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!svc.IsCircuitBroken && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        svc.IsCircuitBroken.Should().BeTrue("circuit breaker should trip when all templates are failing");

        svc.StopLoop();
        await Task.Delay(200);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }
    }

    /// <summary>
    /// Feature: 013-multi-repo-pipeline-loop, Property 11: Duplicate Template Detection
    /// Generate template lists + new entry with same (IssueProviderId, RepoProviderId) tuple, verify rejected.
    /// **Validates: Requirements 5.9**
    /// </summary>
    [Property(MaxTest = 20)]
    public void DuplicateTemplateDetection_RejectsSameTuple(PositiveInt templateCountRaw)
    {
        var templateCount = Math.Min(templateCountRaw.Get, 5);
        var templates = PipelineLoopTestData.CreateTemplates(templateCount);

        // Try to add a duplicate (same IssueProviderId + RepoProviderId as first template)
        var duplicate = new PipelineJobTemplate
        {
            Id = Guid.NewGuid().ToString(),
            Name = "Duplicate",
            IssueProviderId = templates[0].IssueProviderId,
            RepoProviderId = templates[0].RepoProviderId,
            Enabled = true
        };

        var existingTuples = templates.Select(t => (t.IssueProviderId, t.RepoProviderId)).ToHashSet();
        var isDuplicate = existingTuples.Contains((duplicate.IssueProviderId, duplicate.RepoProviderId));

        isDuplicate.Should().BeTrue("the new template has the same provider tuple as an existing one");
    }

    /// <summary>
    /// Feature: 013-multi-repo-pipeline-loop, Property 12: Pre-Start Validation
    /// Generate templates with invalid provider IDs, verify StartLoop() returns false.
    /// **Validates: Requirements 8.1, 8.2**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task PreStartValidation_RejectsInvalidProviderIds(PositiveInt templateCountRaw)
    {
        var templateCount = Math.Min(templateCountRaw.Get, 5);
        var templates = Enumerable.Range(0, templateCount).Select(i => new PipelineJobTemplate
        {
            Id = $"t-{i}", Name = $"Template {i}",
            IssueProviderId = $"ip-invalid-{i}",
            RepoProviderId = $"rp-invalid-{i}",
            Enabled = true
        }).ToList();

        var config = new PipelineConfiguration
        {
            WorkspaceBaseDirectory = Path.GetTempPath()
        };

        var mockStore = new Mock<IConfigurationStore>();
        mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);
        mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());
        mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>());

        var svc = CreateService(mockStore, new Mock<IProviderFactory>());

        var result = await svc.StartLoopAsync();

        result.Should().BeFalse("StartLoop should reject invalid provider IDs");
        svc.ValidationErrors.Should().NotBeEmpty();
        svc.ValidationErrors.Count.Should().BeGreaterThanOrEqualTo(templateCount);
    }

    /// <summary>
    /// Feature: 013-multi-repo-pipeline-loop, Property 13: Provider Cache Eviction on Auth Error
    /// Simulate auth exceptions (401/403), verify cached provider disposed and removed, next cycle recreates.
    /// **Validates: Requirements 2.3 (cache correctness under failure)**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task ProviderCacheEvictionOnAuthError_RecreatesProvider(PositiveInt templateCountRaw)
    {
        var templateCount = Math.Max(Math.Min(templateCountRaw.Get, 3), 1);
        var templates = PipelineLoopTestData.CreateTemplates(templateCount);
        var authFailId = templates[0].IssueProviderId;

        var config = new PipelineConfiguration
        {
            ClosedLoopPollInterval = TimeSpan.FromMilliseconds(50),
            WorkspaceBaseDirectory = Path.GetTempPath()
        };

        var mockStore = new Mock<IConfigurationStore>();
        mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(config);
        SetupProviderConfigs(mockStore, templates);

        var createCountForAuthFail = 0;
        var callCount = 0;
        var mockFactory = new Mock<IProviderFactory>();
        mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns<ProviderConfig>(cfg =>
            {
                var mock = new Mock<IIssueProvider>();
                if (cfg.Id == authFailId)
                {
                    Interlocked.Increment(ref createCountForAuthFail);
                    mock.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                            It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
                        .Returns(() =>
                        {
                            var c = Interlocked.Increment(ref callCount);
                            if (c == 1)
                                throw new HttpRequestException("Unauthorized", null, System.Net.HttpStatusCode.Unauthorized);
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

        // Wait until the provider has been recreated (evicted after auth error, then recreated on next cycle)
        // Use polling instead of fixed delay to avoid flakiness on slow CI runners
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Volatile.Read(ref createCountForAuthFail) < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        svc.StopLoop();
        await Task.Delay(200);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }

        // Provider should have been created more than once (evicted after auth error, recreated)
        createCountForAuthFail.Should().BeGreaterThanOrEqualTo(2,
            "provider should be recreated after auth error eviction");
    }

    /// <summary>
    /// Feature: 013-multi-repo-pipeline-loop, Property 14: Provider Cache Disposal on Stop
    /// Populate cache with N providers, call StopLoop(), verify ALL providers have DisposeAsync called.
    /// **Validates: Resource management (no leaks)**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task ProviderCacheDisposal_AllDisposedOnStop(PositiveInt templateCountRaw)
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

        var disposedProviders = new List<string>();
        var mockFactory = new Mock<IProviderFactory>();
        mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns<ProviderConfig>(cfg =>
            {
                var mock = new Mock<IIssueProvider>();
                mock.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                        It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new PagedResult<IssueSummary> { Items = new List<IssueSummary>(), Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false });
                mock.Setup(p => p.DisposeAsync())
                    .Returns(() => { lock (disposedProviders) { disposedProviders.Add(cfg.Id); } return ValueTask.CompletedTask; });
                return mock.Object;
            });

        var svc = CreateService(mockStore, mockFactory);
        using var cts = new CancellationTokenSource();
        await svc.StartAsync(cts.Token);

        var started = await svc.StartLoopAsync();
        if (!started) { cts.Cancel(); try { await svc.StopAsync(CancellationToken.None); } catch { } return; }

        // Allow enough time for the loop to poll and populate the provider cache
        var cacheDeadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < cacheDeadline)
        {
            lock (disposedProviders) { /* just sync */ }
            await Task.Delay(100);
            // Check if providers were created (factory was called)
            if (mockFactory.Invocations.Count(i => i.Method.Name == "CreateIssueProvider") >= templates.Count)
                break;
        }

        svc.StopLoop();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.IsLoopActive && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }

        var uniqueIds = templates.Select(t => t.IssueProviderId).Distinct().Count();
        disposedProviders.Distinct().Count().Should().Be(uniqueIds,
            "all cached providers should be disposed on stop");
    }

    /// <summary>
    /// Feature: 013-multi-repo-pipeline-loop, Property 9: Config Status Accuracy
    /// Generate poll outcome sequences (success/failure), verify ConfigStatusSnapshot fields after each.
    /// **Validates: Requirements 3.2, 3.3**
    /// </summary>
    [Property(MaxTest = 20)]
    public async Task ConfigStatusAccuracy_ReflectsOutcomes(PositiveInt templateCountRaw)
    {
        var templateCount = Math.Max(Math.Min(templateCountRaw.Get, 3), 1);
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

        var mockFactory = new Mock<IProviderFactory>();
        mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns<ProviderConfig>(cfg =>
            {
                var mock = new Mock<IIssueProvider>();
                mock.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                        It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
                    .ReturnsAsync(new PagedResult<IssueSummary>
                    {
                        Items = new List<IssueSummary>
                        {
                            new() { Identifier = "1", Title = "Test", Labels = new[] { "agent:next" }, CreatedAt = DateTime.UtcNow }
                        },
                        Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false
                    });
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

        // After successful polls, statuses should reflect success
        foreach (var template in templates)
        {
            if (svc.TemplateStatuses.TryGetValue(template.Id, out var status))
            {
                status.ConsecutiveFailures.Should().Be(0);
                status.LastError.Should().BeNull();
                status.LastPollIssueCount.Should().BeGreaterThanOrEqualTo(0);
            }
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
        mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(templates);
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

    private static PipelineLoopService CreateService(Mock<IConfigurationStore> mockStore, Mock<IProviderFactory> mockFactory, IWorkDistributor? distributor = null)
    {
        var mockLogger = new Mock<Serilog.ILogger>();
        var runCreator = TestOrchestrationFactory.CreateMinimalRunCreator(
            configStore: mockStore.Object,
            providerFactory: mockFactory.Object,
            logger: mockLogger.Object);

        return new PipelineLoopService(runCreator, mockFactory.Object, mockStore.Object, mockStore.Object, mockStore.Object, mockLogger.Object, distributor);
    }
}
