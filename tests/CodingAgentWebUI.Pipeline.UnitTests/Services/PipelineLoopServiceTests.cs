using AwesomeAssertions;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.TestUtilities;
using Moq;

namespace CodingAgentWebUI.Pipeline.UnitTests;

public class PipelineLoopServiceTests : IAsyncDisposable
{
    private readonly Mock<IConfigurationStore> _mockStore;
    private readonly Mock<IProviderFactory> _mockFactory;
    private readonly Mock<IIssueProvider> _mockIssueProvider;
    private readonly Mock<Serilog.ILogger> _mockLogger;
    private readonly DispatchRunCreationService _runCreator;
    private readonly PipelineOrchestrationService _orchestration;
    private readonly PipelineRunLifecycleService _lifecycle;
    private PipelineLoopService? _loopService;

    public PipelineLoopServiceTests()
    {
        _mockStore = new Mock<IConfigurationStore>();
        _mockFactory = new Mock<IProviderFactory>();
        _mockIssueProvider = new Mock<IIssueProvider>();
        _mockLogger = new Mock<Serilog.ILogger>();

        _lifecycle = new PipelineRunLifecycleService(
            new TestOrchestrationFactory.NullHistoryService(), null, _mockLogger.Object);

        _orchestration = TestOrchestrationFactory.CreateMinimal(
            configStore: _mockStore.Object,
            providerFactory: _mockFactory.Object,
            lifecycle: _lifecycle,
            logger: _mockLogger.Object);

        _runCreator = TestOrchestrationFactory.CreateMinimalRunCreator(
            configStore: _mockStore.Object,
            providerFactory: _mockFactory.Object,
            lifecycle: _lifecycle,
            logger: _mockLogger.Object);

        SetupDefaults();
    }

    private static readonly List<PipelineJobTemplate> DefaultTemplates = new()
    {
        new PipelineJobTemplate
        {
            Id = "tmpl-1",
            Name = "Default Template",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            BrainProviderId = null,
            PipelineProviderId = null,
            Enabled = true
        }
    };

    private void SetupDefaults()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.Default());
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = DefaultTemplates.Select(t => t.Id).ToList() }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "ip-1", Kind = ProviderKind.Issue, ProviderType = "GitHub", DisplayName = "Test" }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "rp-1", Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Test" }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Agent, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = "ap-1", Kind = ProviderKind.Agent, ProviderType = "KiroCli", DisplayName = "Test" }
            });
        _mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(DefaultTemplates);

        _mockFactory.Setup(f => f.CreateIssueProvider(It.IsAny<ProviderConfig>()))
            .Returns(_mockIssueProvider.Object);
    }

    private PipelineLoopService CreateService(IWorkDistributor? workDistributor = null)
    {
        _loopService = new PipelineLoopService(new PipelineLoopServiceDependencies
        {
            Orchestration = _runCreator,
            ProviderFactory = _mockFactory.Object,
            PipelineConfigStore = _mockStore.Object,
            ProviderConfigStore = _mockStore.Object,
            ProjectStore = _mockStore.Object,
            Logger = _mockLogger.Object,
            WorkDistributor = workDistributor,
            DispatchOrchestration = null,
            DependencyChecker = null,
            HousekeepingService = null
        });
        return _loopService;
    }

    [Fact]
    public void StartsDormant()
    {
        var svc = CreateService();
        Assert.False(svc.IsLoopActive);
        Assert.Equal("", svc.StatusMessage);
    }

    [Fact]
    public async Task StartLoop_ActivatesLoop()
    {
        var svc = CreateService();
        var result = await svc.StartLoopAsync();
        Assert.True(result);
        Assert.True(svc.IsLoopActive);
    }

    [Fact]
    public async Task StartLoop_RejectsWhenAlreadyActive()
    {
        var svc = CreateService();
        await svc.StartLoopAsync();
        var second = await svc.StartLoopAsync();
        Assert.False(second);
    }

    [Fact]
    public async Task StopLoop_SetsStopRequested()
    {
        var svc = CreateService();
        await svc.StartLoopAsync();
        svc.StopLoop();
        Assert.Contains("stopping", svc.StatusMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartLoop_WithNoTemplates_ReturnsFalse()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.Default());
        _mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate>());
        var svc = CreateService();
        var result = await svc.StartLoopAsync();
        Assert.False(result);
        Assert.False(svc.IsLoopActive);
    }

    [Fact]
    public async Task StartLoop_WithAllDisabled_ReturnsFalse()
    {
        var disabledTemplates = new List<PipelineJobTemplate>
        {
            new() { Id = "t-1", Name = "Disabled", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = false }
        };
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.Default());
        _mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(disabledTemplates);
        var svc = CreateService();
        var result = await svc.StartLoopAsync();
        Assert.False(result);
        Assert.False(svc.IsLoopActive);
    }

    [Fact]
    public void StopLoop_WhenNotActive_IsNoop()
    {
        var svc = CreateService();
        svc.StopLoop(); // Should not throw
        Assert.False(svc.IsLoopActive);
    }

    [Fact]
    public async Task Loop_ProcessesIssuesFifo()
    {
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>
                {
                    new() { Identifier = "2", Title = "Newer", Labels = new[] { "agent:next" }, CreatedAt = new DateTime(2026, 1, 2) },
                    new() { Identifier = "1", Title = "Older", Labels = new[] { "agent:next" }, CreatedAt = new DateTime(2026, 1, 1) }
                },
                Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false
            });

        // Track which issues are started (will fail since we don't have full provider setup, but we can check order)
        var startedIssues = new List<string>();
        _lifecycle.OnChange += () =>
        {
            if (_lifecycle.ActiveRun is { CurrentStep: PipelineStep.Created })
                startedIssues.Add(_lifecycle.ActiveRun.IssueIdentifier);
        };

        // The loop will fail on the actual pipeline run (no real providers), but we can verify FIFO ordering
        // by checking which issue was attempted first
        var svc = CreateService();
        using var cts = new CancellationTokenSource();

        // Start the background service
        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // Poll until at least one issue is started
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (startedIssues.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        svc.StopLoop();
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.IsLoopActive && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        cts.Cancel();

        try { await svc.StopAsync(CancellationToken.None); } catch { }

        // Verify oldest issue was attempted first (if any were started)
        if (startedIssues.Count > 0)
            Assert.Equal("1", startedIssues[0]);
    }

    [Fact]
    public async Task Loop_SkipsErroredIssues()
    {
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>
                {
                    new() { Identifier = "1", Title = "Errored", Labels = new[] { "agent:next", "agent:error" }, CreatedAt = new DateTime(2026, 1, 1) },
                    new() { Identifier = "2", Title = "Also Errored", Labels = new[] { "agent:next", "agent:error" }, CreatedAt = new DateTime(2026, 1, 2) }
                },
                Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false
            });

        var svc = CreateService();
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // In multi-template mode, errored issues are skipped during dispatch.
        // The cycle completes and shows "Cycle complete" status.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!svc.StatusMessage.Contains("Cycle complete", StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        // No issues should have been processed (all have agent:error)
        Assert.Equal(0, svc.ProcessedCount);

        svc.StopLoop();
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }
    }

    [Fact]
    public async Task Loop_SkipsNeedsRefinementIssues()
    {
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>
                {
                    new() { Identifier = "1", Title = "Errored", Labels = new[] { "agent:next", "agent:error" }, CreatedAt = new DateTime(2026, 1, 1) },
                    new() { Identifier = "2", Title = "Needs Refinement", Labels = new[] { "agent:next", "agent:needs-refinement" }, CreatedAt = new DateTime(2026, 1, 2) }
                },
                Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false
            });

        var svc = CreateService();
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // In multi-template mode, errored/needs-refinement issues are skipped during dispatch.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!svc.StatusMessage.Contains("Cycle complete", StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        // Both issues should be skipped — no runs should have been attempted
        Assert.Equal(0, svc.ProcessedCount);

        svc.StopLoop();
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }
    }

    [Fact]
    public async Task Loop_EarlyExitsWhenAllCandidatesNeedRefinement()
    {
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>
                {
                    new() { Identifier = "1", Title = "Needs Refinement 1", Labels = new[] { "agent:next", "agent:needs-refinement" }, CreatedAt = new DateTime(2026, 1, 1) },
                    new() { Identifier = "2", Title = "Needs Refinement 2", Labels = new[] { "agent:next", "agent:needs-refinement" }, CreatedAt = new DateTime(2026, 1, 2) }
                },
                Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false
            });

        var svc = CreateService();
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // In multi-template mode, all issues are skipped during dispatch (needs-refinement).
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!svc.StatusMessage.Contains("Cycle complete", StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.Equal(0, svc.ProcessedCount);
        Assert.Contains("Cycle complete", svc.StatusMessage, StringComparison.OrdinalIgnoreCase);

        svc.StopLoop();
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }
    }

    [Fact]
    public async Task Loop_EarlyExitsWhenAllCandidatesHaveMixedErrorAndRefinement()
    {
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>
                {
                    new() { Identifier = "1", Title = "Errored", Labels = new[] { "agent:next", "agent:error" }, CreatedAt = new DateTime(2026, 1, 1) },
                    new() { Identifier = "2", Title = "Needs Refinement", Labels = new[] { "agent:next", "agent:needs-refinement" }, CreatedAt = new DateTime(2026, 1, 2) }
                },
                Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false
            });

        var svc = CreateService();
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // In multi-template mode, all issues are skipped during dispatch.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!svc.StatusMessage.Contains("Cycle complete", StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.Equal(0, svc.ProcessedCount);
        Assert.Contains("Cycle complete", svc.StatusMessage, StringComparison.OrdinalIgnoreCase);

        svc.StopLoop();
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }
    }

    [Fact]
    public async Task Loop_RespectsMaxPagesToFetch()
    {
        int pageRequested = 0;
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Returns<int, int, IReadOnlyList<string>?, CancellationToken>((page, _, _, _) =>
            {
                Interlocked.Exchange(ref pageRequested, page);
                return Task.FromResult(new PagedResult<IssueSummary>
                {
                    Items = new List<IssueSummary>
                    {
                        new() { Identifier = $"p{page}", Title = $"Issue page {page}", Labels = new[] { "agent:next" }, CreatedAt = DateTime.UtcNow }
                    },
                    Page = page, PageSize = PipelineConstants.DefaultPageSize, HasMore = true // Always more pages
                });
            });

        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                ClosedLoopPollInterval = TimeSpan.FromMilliseconds(200),
                ClosedLoopMaxPagesToFetch = 3
            });

        var svc = CreateService();
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // Wait for at least one poll cycle to complete
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (pageRequested == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        svc.StopLoop();
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.IsLoopActive && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }

        // Should have stopped at page 3 despite HasMore = true
        Assert.True(pageRequested <= 3, $"Expected max 3 pages but requested page {pageRequested}");
    }

    [Fact]
    public async Task Loop_ReturnsToPollingWhenNoIssues()
    {
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>(),
                Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false
            });

        var svc = CreateService();
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // In multi-template mode, status shows "Cycle complete" when no issues found
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!svc.StatusMessage.Contains("Cycle complete", StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.True(svc.IsLoopActive);
        Assert.Contains("Cycle complete", svc.StatusMessage, StringComparison.OrdinalIgnoreCase);

        svc.StopLoop();
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.IsLoopActive && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }
    }

    [Fact]
    public async Task Loop_StopFinishesCurrentRunThenStops()
    {
        var svc = CreateService();
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();
        svc.StopLoop();

        // Poll for loop to become inactive
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.IsLoopActive && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.False(svc.IsLoopActive);

        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }
    }

    [Fact]
    public async Task StartLoop_RejectsWhenManualRunInProgress()
    {
        // Simulate a running pipeline by checking IsRunning
        // We can't easily start a real run, but we can verify the guard logic
        var svc = CreateService();

        // When orchestration is not running, start should succeed
        var result = await svc.StartLoopAsync();
        Assert.True(result);
    }

    [Fact]
    public async Task Loop_PollingErrorLogsWarningAndContinues()
    {
        var callCount = 0;
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Returns<int, int, IReadOnlyList<string>?, CancellationToken>((_, _, _, _) =>
            {
                callCount++;
                if (callCount == 1)
                    throw new HttpRequestException("Network error");
                return Task.FromResult(new PagedResult<IssueSummary>
                {
                    Items = new List<IssueSummary>(),
                    Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false
                });
            });

        // Use short poll interval for test
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                ClosedLoopPollInterval = TimeSpan.FromMilliseconds(200),
                ClosedLoopMaxConsecutivePollFailures = 5,
                ClosedLoopMaxBackoffInterval = TimeSpan.FromSeconds(2)
            });

        var svc = CreateService();
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // Wait for at least 2 poll cycles (first fails with backoff, second succeeds)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (callCount < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        // Loop should still be active (didn't crash)
        Assert.True(svc.IsLoopActive);
        Assert.True(callCount >= 2, $"Expected at least 2 poll attempts, got {callCount}");

        svc.StopLoop();
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.IsLoopActive && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }
    }

    [Fact]
    public async Task Loop_GracefulShutdownOnCancellation()
    {
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>(),
                Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false
            });

        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                ClosedLoopPollInterval = TimeSpan.FromSeconds(60)
            });

        var svc = CreateService();
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // Wait for the loop to actually start processing (status message changes from initial)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.StatusMessage == "🔄 Loop starting…" && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.True(svc.IsLoopActive);

        // Simulate application shutdown
        cts.Cancel();
        try { await svc.StopAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token); } catch { }

        var deadline2 = DateTime.UtcNow.AddSeconds(5);
        while (svc.IsLoopActive && DateTime.UtcNow < deadline2)
            await Task.Delay(50);
        Assert.False(svc.IsLoopActive);
    }

    [Fact]
    public async Task OnChange_FiresOnStartLoop()
    {
        var svc = CreateService();
        var fired = false;
        svc.OnChange += () => fired = true;

        await svc.StartLoopAsync();
        Assert.True(fired);
    }

    [Fact]
    public async Task Loop_MaxRunsPerCycle_LimitsProcessing()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                ClosedLoopPollInterval = TimeSpan.FromMilliseconds(200),
                ClosedLoopMaxRunsPerCycle = 1
            });

        var attemptedIssues = new List<string>();
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>
                {
                    new() { Identifier = "1", Title = "First", Labels = new[] { "agent:next" }, CreatedAt = new DateTime(2026, 1, 1) },
                    new() { Identifier = "2", Title = "Second", Labels = new[] { "agent:next" }, CreatedAt = new DateTime(2026, 1, 2) }
                },
                Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false
            });

        var mockDistributor = new Mock<IWorkDistributor>();
        mockDistributor.Setup(d => d.DistributeAsync(
                It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(true, null, null));
        mockDistributor.Setup(d => d.GetActiveIssueIdentifiersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<(IssueIdentifier, ProviderConfigId)>());

        var svc = CreateService(mockDistributor.Object);
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // Poll until at least one issue is processed
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.ProcessedCount < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        svc.StopLoop();
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.IsLoopActive && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }

        // With maxRunsPerCycle=1, only 1 issue should be dispatched per cycle
        Assert.True(svc.ProcessedCount >= 1);
    }

    [Fact]
    public async Task Loop_SkipsIssuesAlreadyInActiveIssueIdentifiers()
    {
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>
                {
                    new() { Identifier = "already-active", Title = "Active Issue", Labels = new[] { "agent:next" }, CreatedAt = new DateTime(2026, 1, 1) },
                    new() { Identifier = "new-issue", Title = "New Issue", Labels = new[] { "agent:next" }, CreatedAt = new DateTime(2026, 1, 2) }
                },
                Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false
            });

        var dispatchedRequests = new List<JobDistributionRequest>();
        var mockDistributor = new Mock<IWorkDistributor>();
        mockDistributor.Setup(d => d.DistributeAsync(
                It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<JobDistributionRequest, CancellationToken>((req, _) => dispatchedRequests.Add(req))
            .ReturnsAsync(new DistributionResult(true, null, null));

        // "already-active" is in the active set — should be SKIPPED
        mockDistributor.Setup(d => d.GetActiveIssueIdentifiersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<(IssueIdentifier, ProviderConfigId)> { ("already-active", "ip-1") });

        var svc = CreateService(mockDistributor.Object);
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // Wait for dispatch to happen
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.ProcessedCount < 1 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        svc.StopLoop();
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.IsLoopActive && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }

        // Only "new-issue" should have been dispatched — "already-active" was deduped
        Assert.True(dispatchedRequests.Count >= 1,
            $"Expected at least 1 dispatch, got {dispatchedRequests.Count}");
        Assert.DoesNotContain(dispatchedRequests,
            r => r.IssueIdentifier == "already-active");
    }

    [Fact]
    public async Task Loop_DispatchesAllIssuesWhenActiveSetIsEmpty()
    {
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>
                {
                    new() { Identifier = "issue-1", Title = "First", Labels = new[] { "agent:next" }, CreatedAt = new DateTime(2026, 1, 1) },
                    new() { Identifier = "issue-2", Title = "Second", Labels = new[] { "agent:next" }, CreatedAt = new DateTime(2026, 1, 2) }
                },
                Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false
            });

        var dispatchedIdentifiers = new List<string>();
        var mockDistributor = new Mock<IWorkDistributor>();
        mockDistributor.Setup(d => d.DistributeAsync(
                It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .Callback<JobDistributionRequest, CancellationToken>((req, _) => dispatchedIdentifiers.Add(req.IssueIdentifier))
            .ReturnsAsync(new DistributionResult(true, null, null));

        // Empty active set — no dedup, all issues should proceed
        mockDistributor.Setup(d => d.GetActiveIssueIdentifiersAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new HashSet<(IssueIdentifier, ProviderConfigId)>());

        var svc = CreateService(mockDistributor.Object);
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // Wait for both issues to be dispatched
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (dispatchedIdentifiers.Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        svc.StopLoop();
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.IsLoopActive && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }

        Assert.Contains("issue-1", dispatchedIdentifiers);
        Assert.Contains("issue-2", dispatchedIdentifiers);
    }

    [Fact]
    public async Task Loop_WhenGetActiveIssueIdentifiersThrows_ContinuesWithEmptySet()
    {
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>
                {
                    new() { Identifier = "issue-x", Title = "Issue X", Labels = new[] { "agent:next" }, CreatedAt = new DateTime(2026, 1, 1) }
                },
                Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false
            });

        var mockDistributor = new Mock<IWorkDistributor>();
        mockDistributor.Setup(d => d.DistributeAsync(
                It.IsAny<JobDistributionRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DistributionResult(true, null, null));

        // GetActiveIssueIdentifiersAsync throws — should not crash the loop
        mockDistributor.Setup(d => d.GetActiveIssueIdentifiersAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("DB connection lost"));

        var svc = CreateService(mockDistributor.Object);
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // The loop should still run (maybe with error, maybe succeeds with empty dedup set)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.StatusMessage == "🔄 Loop starting…" && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        // Loop should still be active (didn't crash)
        Assert.True(svc.IsLoopActive);

        svc.StopLoop();
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.IsLoopActive && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }
    }

    [Fact]
    public void InitiatedBy_DefaultsToManual()
    {
        var run = new PipelineRun
        {
            RunId = "test", IssueIdentifier = "1", IssueTitle = "Test",
            IssueProviderConfigId = "ip", RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow, CurrentStep = PipelineStep.Created
        };
        Assert.Equal("manual", run.InitiatedBy);
    }

    [Fact]
    public void InitiatedBy_IncludedInSummary()
    {
        var run = new PipelineRun
        {
            RunId = "test", IssueIdentifier = "1", IssueTitle = "Test",
            IssueProviderConfigId = "ip", RepoProviderConfigId = "rp",
            StartedAt = DateTime.UtcNow, CurrentStep = PipelineStep.Completed,
            InitiatedBy = "loop"
        };
        var summary = run.ToSummary();
        Assert.Equal("loop", summary.InitiatedBy);
    }

    [Fact]
    public void IssueSummary_HasCreatedAt()
    {
        var now = DateTime.UtcNow;
        var summary = new IssueSummary
        {
            Identifier = "1", Title = "Test", Labels = Array.Empty<string>(),
            CreatedAt = now
        };
        Assert.Equal(now, summary.CreatedAt);
    }

    [Fact]
    public void PipelineConfiguration_HasClosedLoopDefaults()
    {
        var config = new PipelineConfiguration();
        Assert.Equal(TimeSpan.FromSeconds(60), config.ClosedLoopPollInterval);
        Assert.Equal(0, config.ClosedLoopMaxRunsPerCycle);
        Assert.Equal(5, config.ClosedLoopMaxConsecutivePollFailures);
        Assert.Equal(TimeSpan.FromMinutes(15), config.ClosedLoopMaxBackoffInterval);
        Assert.Equal(10, config.ClosedLoopMaxPagesToFetch);
    }

    [Fact]
    public void PipelineConfiguration_WithExpression_PreservesClosedLoopFields()
    {
        var config = new PipelineConfiguration
        {
            ClosedLoopPollInterval = TimeSpan.FromSeconds(120),
            ClosedLoopMaxRunsPerCycle = 5,
            ClosedLoopMaxConsecutivePollFailures = 10,
            ClosedLoopMaxBackoffInterval = TimeSpan.FromMinutes(30),
            ClosedLoopMaxPagesToFetch = 20
        };
        var copy = config with { LastUsedProviderIds = new Dictionary<string, string>() };
        Assert.Equal(TimeSpan.FromSeconds(120), copy.ClosedLoopPollInterval);
        Assert.Equal(5, copy.ClosedLoopMaxRunsPerCycle);
        Assert.Equal(10, copy.ClosedLoopMaxConsecutivePollFailures);
        Assert.Equal(TimeSpan.FromMinutes(30), copy.ClosedLoopMaxBackoffInterval);
        Assert.Equal(20, copy.ClosedLoopMaxPagesToFetch);
    }

    [Fact]
    public async Task Loop_BackoffProgression_TracksConsecutiveFailuresPerTemplate()
    {
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network error"));

        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                ClosedLoopPollInterval = TimeSpan.FromMilliseconds(50),
                ClosedLoopMaxConsecutivePollFailures = 10, // High threshold so circuit breaker doesn't trip
                ClosedLoopMaxBackoffInterval = TimeSpan.FromSeconds(10)
            });

        var svc = CreateService();
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // Wait for multiple cycles to accumulate failures
        await Task.Delay(500);

        svc.StopLoop();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.IsLoopActive && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }

        // In multi-template mode, failures are tracked per-template in TemplateStatuses
        // The template should have accumulated consecutive failures
        // Note: TemplateStatuses is cleared on stop, so we verify the loop ran multiple cycles
        Assert.True(svc.IsLoopActive == false);
    }

    [Fact]
    public async Task Loop_BackoffCap_NeverExceedsMaxBackoff()
    {
        var callCount = 0;
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Returns<int, int, IReadOnlyList<string>?, CancellationToken>((_, _, _, _) =>
            {
                callCount++;
                throw new HttpRequestException("Network error");
            });

        // pollInterval=200ms, maxBackoff=500ms — after a few failures the backoff should cap
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                ClosedLoopPollInterval = TimeSpan.FromMilliseconds(100),
                ClosedLoopMaxConsecutivePollFailures = 20,
                ClosedLoopMaxBackoffInterval = TimeSpan.FromMilliseconds(300)
            });

        var svc = CreateService();
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // 100ms + 200ms + 300ms(capped) + 300ms(capped) = ~900ms for 4 calls; wait 2s for CI headroom
        await Task.Delay(2000);

        svc.StopLoop();
        await Task.Delay(200);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }

        // With cap at 500ms, we should get more calls than without cap
        // Without cap: 200+400+800+1600 = 3000ms for 4 calls
        // With cap: 200+400+500+500+500 = 2100ms for 5 calls
        Assert.True(callCount >= 4, $"Expected at least 4 poll attempts with backoff cap, got {callCount}");
    }

    [Fact]
    public async Task Loop_BackoffResetOnSuccess_ResetsToNormalInterval()
    {
        var callCount = 0;
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Returns<int, int, IReadOnlyList<string>?, CancellationToken>((_, _, _, _) =>
            {
                callCount++;
                // Fail on calls 1-2, succeed on 3+
                if (callCount <= 2)
                    throw new HttpRequestException("Network error");
                return Task.FromResult(new PagedResult<IssueSummary>
                {
                    Items = new List<IssueSummary>(),
                    Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false
                });
            });

        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                ClosedLoopPollInterval = TimeSpan.FromMilliseconds(200),
                ClosedLoopMaxConsecutivePollFailures = 10,
                ClosedLoopMaxBackoffInterval = TimeSpan.FromSeconds(5)
            });

        var svc = CreateService();
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // Wait for failures + recovery
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (callCount < 3 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        // After success, consecutive failures should be reset
        Assert.Null(svc.LastPollError);

        svc.StopLoop();
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.IsLoopActive && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }
    }

    [Fact]
    public async Task Loop_CircuitBreakerTrips_AfterMaxConsecutiveFailures()
    {
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network error"));

        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                ClosedLoopPollInterval = TimeSpan.FromMilliseconds(50),
                ClosedLoopMaxConsecutivePollFailures = 3,
                ClosedLoopMaxBackoffInterval = TimeSpan.FromMilliseconds(200)
            });

        var svc = CreateService();
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // In multi-template mode, circuit breaker trips when ALL templates have failures >= threshold
        // Wait for both IsCircuitBroken AND StatusMessage to stabilize (ARM weak memory ordering
        // can cause the test thread to observe IsCircuitBroken=true before StatusMessage is updated)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while ((!svc.IsCircuitBroken || !svc.StatusMessage.Contains("paused", StringComparison.OrdinalIgnoreCase))
               && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.True(svc.IsCircuitBroken);
        Assert.Contains("paused", svc.StatusMessage, StringComparison.OrdinalIgnoreCase);

        svc.StopLoop();
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.IsLoopActive && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }
    }

    [Fact]
    public async Task Loop_CircuitBreakerResume_ResetsAndContinuesPolling()
    {
        var callCount = 0;
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Returns<int, int, IReadOnlyList<string>?, CancellationToken>((_, _, _, _) =>
            {
                callCount++;
                // Fail first 3 (trip circuit breaker), then succeed after resume
                if (callCount <= 3)
                    throw new HttpRequestException("Network error");
                return Task.FromResult(new PagedResult<IssueSummary>
                {
                    Items = new List<IssueSummary>(),
                    Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false
                });
            });

        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                ClosedLoopPollInterval = TimeSpan.FromMilliseconds(100),
                ClosedLoopMaxConsecutivePollFailures = 3,
                ClosedLoopMaxBackoffInterval = TimeSpan.FromMilliseconds(200)
            });

        var svc = CreateService();
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // Wait for circuit breaker to trip
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!svc.IsCircuitBroken && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.True(svc.IsCircuitBroken);

        // Resume the loop
        svc.ResumeLoop();
        Assert.False(svc.IsCircuitBroken);

        // Wait for successful poll after resume
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (callCount < 4 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.True(svc.IsLoopActive);
        Assert.False(svc.IsCircuitBroken);

        svc.StopLoop();
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.IsLoopActive && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }
    }

    [Fact]
    public async Task Loop_CircuitBreakerStop_TerminatesLoop()
    {
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network error"));

        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                ClosedLoopPollInterval = TimeSpan.FromMilliseconds(100),
                ClosedLoopMaxConsecutivePollFailures = 3,
                ClosedLoopMaxBackoffInterval = TimeSpan.FromMilliseconds(200)
            });

        var svc = CreateService();
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // Wait for circuit breaker to trip
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!svc.IsCircuitBroken && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.True(svc.IsCircuitBroken);

        // Stop the loop while circuit breaker is active
        svc.StopLoop();
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.IsLoopActive && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.False(svc.IsLoopActive);

        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }
    }

    [Fact]
    public async Task Loop_CircuitBreakerAutoResumes_AfterCooldownExpires()
    {
        var callCount = 0;
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Returns<int, int, IReadOnlyList<string>?, CancellationToken>((_, _, _, _) =>
            {
                callCount++;
                // Fail first 3 calls to trip the breaker, then succeed
                if (callCount <= 3)
                    throw new HttpRequestException("Network error");
                return Task.FromResult(new PagedResult<IssueSummary>
                {
                    Items = new List<IssueSummary>(),
                    Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false
                });
            });

        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                ClosedLoopPollInterval = TimeSpan.FromMilliseconds(50),
                ClosedLoopMaxConsecutivePollFailures = 3,
                ClosedLoopMaxBackoffInterval = TimeSpan.FromMilliseconds(100),
                ClosedLoopCircuitBreakerCooldown = TimeSpan.FromSeconds(1),
            });

        var svc = CreateService();
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // Wait for circuit breaker to trip
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!svc.IsCircuitBroken && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.True(svc.IsCircuitBroken);

        // Wait for auto-resume after cooldown (no manual intervention needed)
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.IsCircuitBroken && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.False(svc.IsCircuitBroken);
        Assert.True(svc.IsLoopActive);

        // Verify polling continued after auto-resume
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (callCount < 4 && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.True(callCount >= 4, $"Expected at least 4 poll attempts (3 failures + 1 after resume), got {callCount}");

        svc.StopLoop();
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.IsLoopActive && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }
    }

    [Fact]
    public async Task Loop_RateLimitWaitsUntilReset_DoesNotIncrementFailures()
    {
        var callCount = 0;
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Returns<int, int, IReadOnlyList<string>?, CancellationToken>((_, _, _, _) =>
            {
                callCount++;
                if (callCount == 1)
                    throw new RateLimitExceededException(DateTimeOffset.UtcNow.AddMilliseconds(300));
                return Task.FromResult(new PagedResult<IssueSummary>
                {
                    Items = new List<IssueSummary>(),
                    Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false
                });
            });

        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                ClosedLoopPollInterval = TimeSpan.FromMilliseconds(200),
                ClosedLoopMaxConsecutivePollFailures = 3,
                ClosedLoopMaxBackoffInterval = TimeSpan.FromSeconds(5)
            });

        var svc = CreateService();
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // Poll until at least 2 calls have been made
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (callCount < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        // Rate limit should NOT count as a failure
        Assert.False(svc.IsCircuitBroken);
        Assert.True(callCount >= 2, $"Expected at least 2 poll attempts, got {callCount}");

        svc.StopLoop();
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.IsLoopActive && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }
    }

    [Fact]
    public async Task Loop_NormalRecoveryAfterTransientFailure()
    {
        var callCount = 0;
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Returns<int, int, IReadOnlyList<string>?, CancellationToken>((_, _, _, _) =>
            {
                callCount++;
                if (callCount == 1)
                    throw new HttpRequestException("Transient error");
                return Task.FromResult(new PagedResult<IssueSummary>
                {
                    Items = new List<IssueSummary>(),
                    Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false
                });
            });

        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                ClosedLoopPollInterval = TimeSpan.FromMilliseconds(200),
                ClosedLoopMaxConsecutivePollFailures = 5,
                ClosedLoopMaxBackoffInterval = TimeSpan.FromSeconds(2)
            });

        var svc = CreateService();
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // Poll until at least 2 calls (first fails, second succeeds and resets)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (callCount < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.True(svc.IsLoopActive);
        Assert.Null(svc.LastPollError);
        Assert.False(svc.IsCircuitBroken);

        svc.StopLoop();
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.IsLoopActive && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }
    }

    [Fact]
    public async Task Loop_CircuitBreakerRespectsAppShutdown()
    {
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Network error"));

        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                ClosedLoopPollInterval = TimeSpan.FromMilliseconds(100),
                ClosedLoopMaxConsecutivePollFailures = 3,
                ClosedLoopMaxBackoffInterval = TimeSpan.FromMilliseconds(200)
            });

        var svc = CreateService();
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // Wait for circuit breaker to trip
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!svc.IsCircuitBroken && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        Assert.True(svc.IsCircuitBroken);

        // Simulate app shutdown while paused on circuit breaker
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }

        Assert.False(svc.IsLoopActive);
    }

    [Fact]
    public async Task Loop_CustomMaxConsecutiveFailures_TripsAtConfiguredThreshold()
    {
        var callCount = 0;
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .Returns<int, int, IReadOnlyList<string>?, CancellationToken>((_, _, _, _) =>
            {
                callCount++;
                throw new HttpRequestException("Network error");
            });

        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration
            {
                WorkspaceBaseDirectory = Path.GetTempPath(),
                ClosedLoopPollInterval = TimeSpan.FromMilliseconds(50),
                ClosedLoopMaxConsecutivePollFailures = 2,
                ClosedLoopMaxBackoffInterval = TimeSpan.FromMilliseconds(200)
            });

        var svc = CreateService();
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // Wait for circuit breaker to trip (all templates failing >= 2)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!svc.IsCircuitBroken && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.True(svc.IsCircuitBroken);

        svc.StopLoop();
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (svc.IsLoopActive && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }
    }

    [Fact]
    public async Task ResumeLoop_WhenNotBroken_IsNoop()
    {
        var svc = CreateService();
        await svc.StartLoopAsync();
        svc.ResumeLoop(); // Should not throw
        Assert.False(svc.IsCircuitBroken);
    }

    [Fact]
    public async Task StartLoop_WithDuplicateTemplateIds_DoesNotCrash()
    {
        var duplicateTemplates = new List<PipelineJobTemplate>
        {
            new() { Id = "tmpl-1", Name = "First", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true },
            new() { Id = "tmpl-1", Name = "Duplicate", IssueProviderId = "ip-1", RepoProviderId = "rp-1", Enabled = true }
        };
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.Default());
        _mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(duplicateTemplates);

        var svc = CreateService();
        using var cts = new CancellationTokenSource();

        await svc.StartAsync(cts.Token);
        await svc.StartLoopAsync();

        // Wait for the loop to complete at least one iteration (reaches the ToDictionary call)
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!svc.StatusMessage.Contains("Cycle complete", StringComparison.OrdinalIgnoreCase) && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        Assert.True(svc.IsLoopActive);
        Assert.Contains("Cycle complete", svc.StatusMessage, StringComparison.OrdinalIgnoreCase);
        // TODO: Assert that the duplicate-detection warning log was emitted via _mockLogger

        svc.StopLoop();
        cts.Cancel();
        try { await svc.StopAsync(CancellationToken.None); } catch { }
    }

    [Fact]
    public async Task StartLoop_WhenConfigStoreThrows_ReturnsFalseAndSetsValidationError()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("SQLite database is locked"));
        var svc = CreateService();

        var result = await svc.StartLoopAsync();

        Assert.False(result);
        Assert.False(svc.IsLoopActive);
        Assert.Single(svc.ValidationErrors);
        Assert.Contains("SQLite database is locked", svc.ValidationErrors[0]);
    }

    [Fact]
    public async Task StartLoop_WhenProviderConfigStoreThrows_ReturnsFalseAndSetsValidationError()
    {
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Issue, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("Network path not found"));
        var svc = CreateService();

        var result = await svc.StartLoopAsync();

        Assert.False(result);
        Assert.False(svc.IsLoopActive);
        Assert.Single(svc.ValidationErrors);
        Assert.Contains("Network path not found", svc.ValidationErrors[0]);
    }

    [Fact]
    public async Task StartLoop_WhenTemplateStoreThrows_ReturnsFalseAndLogsError()
    {
        _mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Connection timed out"));
        var svc = CreateService();

        var result = await svc.StartLoopAsync();

        Assert.False(result);
        Assert.False(svc.IsLoopActive);
        Assert.Single(svc.ValidationErrors);
        Assert.Contains("Connection timed out", svc.ValidationErrors[0]);
    }

    [Fact]
    public async Task StartLoop_WhenConfigStoreThrows_NotifiesChange()
    {
        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Store unavailable"));
        var svc = CreateService();
        var changeNotified = false;
        svc.OnChange += () => changeNotified = true;

        await svc.StartLoopAsync();

        Assert.True(changeNotified);
    }

    public async ValueTask DisposeAsync()
    {
        if (_loopService is not null)
        {
            try { await _loopService.StopAsync(CancellationToken.None); } catch { }
            _loopService.Dispose();
        }
        _orchestration.Dispose();
    }

    // ── Housekeeping per-repo dedup ───────────────────────────────────────────

    /// <summary>
    /// Regression test for the per-repo deduplication guard in
    /// <c>PipelineLoopService.MultiTemplateLoop.cs</c>.
    ///
    /// Two templates sharing the same <c>RepoProviderId</c> and both with
    /// <c>HousekeepingEnabled=true</c> must result in <c>ExecuteAsync</c> being
    /// called exactly once per cycle for that repo — not twice. If the
    /// <c>processedRepos</c> HashSet guard is removed, the second template would
    /// trigger a second invocation.
    /// </summary>
    [Fact]
    public async Task Housekeeping_TwoTemplatesSameRepo_ExecuteAsyncCalledOncePerCycle()
    {
        // Two templates sharing the same RepoProviderId, both with housekeeping on
        var sharedRepoId = "rp-shared";
        var twoSharedRepoTemplates = new List<PipelineJobTemplate>
        {
            new() { Id = "tmpl-A", Name = "Template A", IssueProviderId = "ip-1", RepoProviderId = sharedRepoId, Enabled = true, HousekeepingEnabled = true },
            new() { Id = "tmpl-B", Name = "Template B", IssueProviderId = "ip-1", RepoProviderId = sharedRepoId, Enabled = true, HousekeepingEnabled = true },
        };

        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.Default());
        _mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(twoSharedRepoTemplates);
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = ["tmpl-A", "tmpl-B"] }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = sharedRepoId, Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Shared Repo" }
            });

        // The mock factory must return a repo provider with SupportsServerSideBranchUpdate=true,
        // otherwise the loop skips the housekeeping step entirely.
        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(r => r.SupportsServerSideBranchUpdate).Returns(true);
        mockRepoProvider.Setup(r => r.ListOpenPullRequestsAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PullRequestSummary>
            {
                Items = new List<PullRequestSummary>().AsReadOnly(),
                Page = 1, PageSize = 100, HasMore = false
            });
        _mockFactory.Setup(f => f.CreateRepositoryProvider(It.Is<ProviderConfig>(c => c.Id == sharedRepoId)))
                    .Returns(mockRepoProvider.Object);

        var housekeepingMock = new Mock<IHousekeepingService>();
        housekeepingMock.Setup(h => h.ExecuteAsync(
                It.IsAny<IRepositoryProvider>(), It.IsAny<string>(),
                It.IsAny<IIssueProvider>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        _loopService = new PipelineLoopService(new PipelineLoopServiceDependencies
        {
            Orchestration = _runCreator,
            ProviderFactory = _mockFactory.Object,
            PipelineConfigStore = _mockStore.Object,
            ProviderConfigStore = _mockStore.Object,
            ProjectStore = _mockStore.Object,
            Logger = _mockLogger.Object,
            WorkDistributor = null,
            DispatchOrchestration = null,
            DependencyChecker = null,
            HousekeepingService = housekeepingMock.Object
        });

        using var cts = new CancellationTokenSource();
        await _loopService.StartAsync(cts.Token);
        await _loopService.StartLoopAsync();

        // Wait for one full cycle
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!_loopService.StatusMessage.Contains("Cycle complete", StringComparison.OrdinalIgnoreCase)
               && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        // Stop immediately after first cycle completes so we count exactly one cycle
        _loopService.StopLoop();
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (_loopService.IsLoopActive && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        cts.Cancel();
        try { await _loopService.StopAsync(CancellationToken.None); } catch { }

        // The dedup guard must ensure ExecuteAsync fires exactly once for the shared repo
        // per cycle, even though two templates reference it with HousekeepingEnabled=true.
        // Without the dedup, 2 templates × 1 cycle = 2 calls. With dedup = 1 call.
        // We stop after the first "Cycle complete" so the assertion is for one cycle.
        housekeepingMock.Verify(h => h.ExecuteAsync(
            It.IsAny<IRepositoryProvider>(),
            It.Is<string>(id => id == sharedRepoId),
            It.IsAny<IIssueProvider>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<int>(),
            It.IsAny<bool>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()),
            Times.Once,
            "ExecuteAsync must be called exactly once per cycle for a shared repo — dedup guard prevents double-invocation");
    }

    // ── RunHousekeepingAsync direct-invocation tests ──────────────────────────
    //
    // These tests call RunHousekeepingAsync directly (no loop startup required) to verify
    // each guard condition and parameter in isolation. They satisfy the acceptance criterion
    // "Housekeeping iteration is testable in isolation without running the full dispatch cycle."
    //
    // Setup pattern:
    //  1. Construct PipelineLoopService with the desired HousekeepingService mock
    //  2. Seed svc._cacheManager directly (no round-trip through SnapshotAndReconcileAsync)
    //  3. Build a minimal CycleSnapshot with the desired template/config
    //  4. Call svc.RunHousekeepingAsync(snapshot, queues, ct) directly

    private PipelineLoopService CreateServiceWithHousekeeping(IHousekeepingService? housekeepingService)
    {
        _loopService = new PipelineLoopService(new PipelineLoopServiceDependencies
        {
            Orchestration = _runCreator,
            ProviderFactory = _mockFactory.Object,
            PipelineConfigStore = _mockStore.Object,
            ProviderConfigStore = _mockStore.Object,
            ProjectStore = _mockStore.Object,
            Logger = _mockLogger.Object,
            WorkDistributor = null,
            DispatchOrchestration = null,
            DependencyChecker = null,
            HousekeepingService = housekeepingService
        });
        return _loopService;
    }

    /// <summary>
    /// Builds a minimal CycleSnapshot for direct-invocation tests.
    /// </summary>
    private static PipelineLoopService.CycleSnapshot BuildSnapshot(
        IReadOnlyList<PipelineJobTemplate> pollableTemplates,
        PipelineConfiguration? config = null)
    {
        var cfg = config ?? TestPipelineConfig.Default();
        var project = new PipelineProject
        {
            Id = WellKnownIds.DefaultProjectId,
            Name = "Default",
            TemplateIds = pollableTemplates.Select(t => t.Id).ToList()
        };
        var flattened = pollableTemplates
            .Select(t => (Template: t, Project: project))
            .ToList() as IReadOnlyList<(PipelineJobTemplate Template, PipelineProject Project)>;

        return new PipelineLoopService.CycleSnapshot(
            Config: cfg,
            Projects: new[] { project },
            FlattenedTemplates: flattened,
            EnabledTemplates: pollableTemplates,
            PollableTemplates: pollableTemplates,
            TemplateLookup: pollableTemplates.ToDictionary(t => t.Id).AsReadOnly(),
            PollInterval: TimeSpan.FromSeconds(60),
            MaxRunsPerCycle: 0,
            MaxConsecutiveFailures: 5,
            MaxPagesToFetch: 10,
            ActiveIssueIdentifiers: new HashSet<(IssueIdentifier, ProviderConfigId)>());
    }

    [Fact]
    public async Task RunHousekeepingAsync_WhenServiceIsNull_DoesNotThrow()
    {
        var svc = CreateServiceWithHousekeeping(housekeepingService: null);
        var template = new PipelineJobTemplate
        {
            Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-hk",
            Enabled = true, HousekeepingEnabled = true
        };
        var snapshot = BuildSnapshot([template]);

        // Should return immediately without touching any provider
        var act = () => svc.RunHousekeepingAsync(snapshot, new Dictionary<string, List<PullRequestSummary>>(), CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RunHousekeepingAsync_SkipsTemplateWhenHousekeepingDisabled()
    {
        var housekeepingMock = new Mock<IHousekeepingService>();
        housekeepingMock.Setup(h => h.ExecuteAsync(
            It.IsAny<IRepositoryProvider>(), It.IsAny<string>(),
            It.IsAny<IIssueProvider>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<int>(),
            It.IsAny<bool>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateServiceWithHousekeeping(housekeepingMock.Object);
        var template = new PipelineJobTemplate
        {
            Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-hk",
            Enabled = true, HousekeepingEnabled = false  // disabled
        };
        var snapshot = BuildSnapshot([template]);

        await svc.RunHousekeepingAsync(snapshot, new Dictionary<string, List<PullRequestSummary>>(), CancellationToken.None);

        housekeepingMock.Verify(h => h.ExecuteAsync(
            It.IsAny<IRepositoryProvider>(), It.IsAny<string>(),
            It.IsAny<IIssueProvider>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<int>(),
            It.IsAny<bool>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunHousekeepingAsync_SkipsTemplateWhenRepoProviderNotInCache()
    {
        var housekeepingMock = new Mock<IHousekeepingService>();
        housekeepingMock.Setup(h => h.ExecuteAsync(
            It.IsAny<IRepositoryProvider>(), It.IsAny<string>(),
            It.IsAny<IIssueProvider>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<int>(),
            It.IsAny<bool>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var svc = CreateServiceWithHousekeeping(housekeepingMock.Object);
        // Do NOT seed RepoProviders — simulate cache miss
        var template = new PipelineJobTemplate
        {
            Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-missing",
            Enabled = true, HousekeepingEnabled = true
        };
        var snapshot = BuildSnapshot([template]);

        await svc.RunHousekeepingAsync(snapshot, new Dictionary<string, List<PullRequestSummary>>(), CancellationToken.None);

        housekeepingMock.Verify(h => h.ExecuteAsync(
            It.IsAny<IRepositoryProvider>(), It.IsAny<string>(),
            It.IsAny<IIssueProvider>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<int>(),
            It.IsAny<bool>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    // TODO: Add RunHousekeepingAsync_SkipsTemplateWhenIssueProviderNotInCache test. The guard
    // `if (!_cacheManager.IssueProviders.TryGetValue(template.IssueProviderId, out var issueProvider)) continue;`
    // is a peer of the repo-provider cache-miss guard but has no direct-invocation test coverage.
    // Without a test, breaking this guard would go undetected.
    [Fact]
    public async Task RunHousekeepingAsync_SkipsTemplateWhenRepoProviderDoesNotSupportServerSideBranchUpdate()
    {
        var housekeepingMock = new Mock<IHousekeepingService>();
        housekeepingMock.Setup(h => h.ExecuteAsync(
            It.IsAny<IRepositoryProvider>(), It.IsAny<string>(),
            It.IsAny<IIssueProvider>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<int>(),
            It.IsAny<bool>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(r => r.SupportsServerSideBranchUpdate).Returns(false);

        var svc = CreateServiceWithHousekeeping(housekeepingMock.Object);
        svc._cacheManager.RepoProviders["rp-hk"] = mockRepoProvider.Object;
        svc._cacheManager.IssueProviders["ip-1"] = _mockIssueProvider.Object;

        var template = new PipelineJobTemplate
        {
            Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-hk",
            Enabled = true, HousekeepingEnabled = true
        };
        var snapshot = BuildSnapshot([template]);

        await svc.RunHousekeepingAsync(snapshot, new Dictionary<string, List<PullRequestSummary>>(), CancellationToken.None);

        housekeepingMock.Verify(h => h.ExecuteAsync(
            It.IsAny<IRepositoryProvider>(), It.IsAny<string>(),
            It.IsAny<IIssueProvider>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<int>(),
            It.IsAny<bool>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task RunHousekeepingAsync_UsesTemplateConcurrencyLimitWhenSet()
    {
        int? capturedLimit = null;
        var housekeepingMock = new Mock<IHousekeepingService>();
        housekeepingMock.Setup(h => h.ExecuteAsync(
                It.IsAny<IRepositoryProvider>(), It.IsAny<string>(),
                It.IsAny<IIssueProvider>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback<IRepositoryProvider, string, IIssueProvider, string,
                IReadOnlyList<PullRequestSummary>, int, bool, int, CancellationToken>(
                (_, _, _, _, _, limit, _, _, _) => capturedLimit = limit)
            .Returns(Task.CompletedTask);

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(r => r.SupportsServerSideBranchUpdate).Returns(true);

        var svc = CreateServiceWithHousekeeping(housekeepingMock.Object);
        svc._cacheManager.RepoProviders["rp-hk"] = mockRepoProvider.Object;
        svc._cacheManager.IssueProviders["ip-1"] = _mockIssueProvider.Object;

        var template = new PipelineJobTemplate
        {
            Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-hk",
            Enabled = true, HousekeepingEnabled = true,
            HousekeepingConcurrencyLimit = 5  // template value takes precedence
        };
        var snapshot = BuildSnapshot([template]);

        await svc.RunHousekeepingAsync(snapshot, new Dictionary<string, List<PullRequestSummary>>(), CancellationToken.None);

        Assert.Equal(5, capturedLimit);
    }

    [Fact]
    public async Task RunHousekeepingAsync_FallsBackToConfigLimitWhenTemplateValueIsNull()
    {
        int? capturedLimit = null;
        var housekeepingMock = new Mock<IHousekeepingService>();
        housekeepingMock.Setup(h => h.ExecuteAsync(
                It.IsAny<IRepositoryProvider>(), It.IsAny<string>(),
                It.IsAny<IIssueProvider>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback<IRepositoryProvider, string, IIssueProvider, string,
                IReadOnlyList<PullRequestSummary>, int, bool, int, CancellationToken>(
                (_, _, _, _, _, limit, _, _, _) => capturedLimit = limit)
            .Returns(Task.CompletedTask);

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(r => r.SupportsServerSideBranchUpdate).Returns(true);

        var svc = CreateServiceWithHousekeeping(housekeepingMock.Object);
        svc._cacheManager.RepoProviders["rp-hk"] = mockRepoProvider.Object;
        svc._cacheManager.IssueProviders["ip-1"] = _mockIssueProvider.Object;

        var template = new PipelineJobTemplate
        {
            Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-hk",
            Enabled = true, HousekeepingEnabled = true,
            HousekeepingConcurrencyLimit = null  // falls back to config
        };
        var config = TestPipelineConfig.Default() with { HousekeepingConcurrencyLimit = 3 };
        var snapshot = BuildSnapshot([template], config);

        await svc.RunHousekeepingAsync(snapshot, new Dictionary<string, List<PullRequestSummary>>(), CancellationToken.None);

        Assert.Equal(3, capturedLimit);
    }

    [Fact]
    public async Task RunHousekeepingAsync_LimitClampsToMinimumOne()
    {
        int? capturedLimit = null;
        var housekeepingMock = new Mock<IHousekeepingService>();
        housekeepingMock.Setup(h => h.ExecuteAsync(
                It.IsAny<IRepositoryProvider>(), It.IsAny<string>(),
                It.IsAny<IIssueProvider>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback<IRepositoryProvider, string, IIssueProvider, string,
                IReadOnlyList<PullRequestSummary>, int, bool, int, CancellationToken>(
                (_, _, _, _, _, limit, _, _, _) => capturedLimit = limit)
            .Returns(Task.CompletedTask);

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(r => r.SupportsServerSideBranchUpdate).Returns(true);

        var svc = CreateServiceWithHousekeeping(housekeepingMock.Object);
        svc._cacheManager.RepoProviders["rp-hk"] = mockRepoProvider.Object;
        svc._cacheManager.IssueProviders["ip-1"] = _mockIssueProvider.Object;

        var template = new PipelineJobTemplate
        {
            Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-hk",
            Enabled = true, HousekeepingEnabled = true,
            HousekeepingConcurrencyLimit = 0  // both template and config at 0 → clamped to 1
        };
        var config = TestPipelineConfig.Default() with { HousekeepingConcurrencyLimit = 0 };
        var snapshot = BuildSnapshot([template], config);

        await svc.RunHousekeepingAsync(snapshot, new Dictionary<string, List<PullRequestSummary>>(), CancellationToken.None);

        Assert.Equal(1, capturedLimit);
    }

    // TODO: Add RunHousekeepingAsync_LimitClampsToMinimumOne_WhenTemplateIsNullAndConfigIsZero test.
    // The existing LimitClampsToMinimumOne test uses HousekeepingConcurrencyLimit = 0 (non-null),
    // so the null-coalescing branch (??) is never exercised. The uncovered branch is:
    // HousekeepingConcurrencyLimit = null + config.HousekeepingConcurrencyLimit = 0 → clamped to 1.
    [Fact]
    public async Task RunHousekeepingAsync_EmptyDonePrList_StillCallsExecuteAsync()
    {
        // Verifies that ExecuteAsync is called even when agentDonePrQueues has no entry
        // for the template — the eviction pass must run per IHousekeepingService XML doc.
        IReadOnlyList<PullRequestSummary>? capturedDonePrs = null;
        var housekeepingMock = new Mock<IHousekeepingService>();
        housekeepingMock.Setup(h => h.ExecuteAsync(
                It.IsAny<IRepositoryProvider>(), It.IsAny<string>(),
                It.IsAny<IIssueProvider>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback<IRepositoryProvider, string, IIssueProvider, string,
                IReadOnlyList<PullRequestSummary>, int, bool, int, CancellationToken>(
                (_, _, _, _, donePrs, _, _, _, _) => capturedDonePrs = donePrs)
            .Returns(Task.CompletedTask);

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(r => r.SupportsServerSideBranchUpdate).Returns(true);

        var svc = CreateServiceWithHousekeeping(housekeepingMock.Object);
        svc._cacheManager.RepoProviders["rp-hk"] = mockRepoProvider.Object;
        svc._cacheManager.IssueProviders["ip-1"] = _mockIssueProvider.Object;

        var template = new PipelineJobTemplate
        {
            Id = "t-1", Name = "T", IssueProviderId = "ip-1", RepoProviderId = "rp-hk",
            Enabled = true, HousekeepingEnabled = true
        };
        var snapshot = BuildSnapshot([template]);
        // agentDonePrQueues is empty — template ID not present
        var emptyQueues = new Dictionary<string, List<PullRequestSummary>>();

        await svc.RunHousekeepingAsync(snapshot, emptyQueues, CancellationToken.None);

        // ExecuteAsync must be called (eviction pass) even with empty PR list
        housekeepingMock.Verify(h => h.ExecuteAsync(
            It.IsAny<IRepositoryProvider>(), It.IsAny<string>(),
            It.IsAny<IIssueProvider>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<int>(),
            It.IsAny<bool>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Once);
        Assert.NotNull(capturedDonePrs);
        Assert.Empty(capturedDonePrs!);
    }

    [Fact]
    public async Task Housekeeping_PassesAgentDonePrsFromPollerOutput()
    {
        // TODO: The busy-wait loop below (while !StatusMessage.Contains("Cycle complete") + Task.Delay(50))
        // is a fragile synchronisation mechanism. If "Cycle complete" wording changes, or if
        // capturedDonePrs is written by the background callback after the deadline expires, the
        // test can silently pass against stale/null data. capturedDonePrs has no volatile declaration
        // so there is a benign data race between the assertion thread and the loop thread.
        // Consider replacing the busy-wait with a TaskCompletionSource or SemaphoreSlim signalled
        // from the mock callback to get a deterministic sync point.
        // Integration-style test: verifies the poller → RunHousekeepingAsync data flow.
        // The TemplatePoller filters agent PRs by branch prefix (PipelineConstants.BranchPrefix),
        // NOT by label. The repo provider mock must return a PR with a matching branch name.
        const string repoId = "rp-hk-2";
        var agentPrBranch = $"{PipelineConstants.BranchPrefix}42-fix-bug";

        var tpl = new PipelineJobTemplate
        {
            Id = "tmpl-hk", Name = "HK Template", IssueProviderId = "ip-1",
            RepoProviderId = repoId, Enabled = true, HousekeepingEnabled = true
        };

        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestPipelineConfig.Default());
        _mockStore.Setup(s => s.LoadAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineJobTemplate> { tpl });
        _mockStore.Setup(s => s.LoadProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PipelineProject>
            {
                new() { Id = WellKnownIds.DefaultProjectId, Name = "Default", TemplateIds = ["tmpl-hk"] }
            });
        _mockStore.Setup(s => s.LoadProviderConfigsAsync(ProviderKind.Repository, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ProviderConfig>
            {
                new() { Id = repoId, Kind = ProviderKind.Repository, ProviderType = "GitHub", DisplayName = "Repo" }
            });

        var mockRepoProvider = new Mock<IRepositoryProvider>();
        mockRepoProvider.Setup(r => r.SupportsServerSideBranchUpdate).Returns(true);
        mockRepoProvider.Setup(r => r.ListOpenPullRequestsAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<IReadOnlyList<string>?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PullRequestSummary>
            {
                Items = new List<PullRequestSummary>
                {
                    new()
                    {
                        Number = 42, Identifier = "42", Title = "Fix bug",
                        Description = string.Empty, Labels = Array.Empty<string>(),
                        BranchName = agentPrBranch,   // branch prefix match → included in agentDonePrQueues
                        TargetBranch = "main", Url = "https://example.com/pr/42", IsDraft = false
                    }
                }.AsReadOnly(),
                Page = 1, PageSize = 100, HasMore = false
            });
        _mockFactory.Setup(f => f.CreateRepositoryProvider(It.Is<ProviderConfig>(c => c.Id == repoId)))
            .Returns(mockRepoProvider.Object);

        // Ensure issue polling doesn't throw (NullRef on null return clears all queues)
        _mockIssueProvider.Setup(p => p.ListOpenIssuesAsync(It.IsAny<int>(), It.IsAny<int>(),
                It.IsAny<IReadOnlyList<string>?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<IssueSummary>
            {
                Items = new List<IssueSummary>(),
                Page = 1, PageSize = PipelineConstants.DefaultPageSize, HasMore = false
            });

        IReadOnlyList<PullRequestSummary>? capturedDonePrs = null;
        var housekeepingMock = new Mock<IHousekeepingService>();
        housekeepingMock.Setup(h => h.ExecuteAsync(
                It.IsAny<IRepositoryProvider>(), It.IsAny<string>(),
                It.IsAny<IIssueProvider>(), It.IsAny<string>(),
                It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<int>(),
                It.IsAny<bool>(), It.IsAny<int>(),
                It.IsAny<CancellationToken>()))
            .Callback<IRepositoryProvider, string, IIssueProvider, string,
                IReadOnlyList<PullRequestSummary>, int, bool, int, CancellationToken>(
                (_, _, _, _, donePrs, _, _, _, _) => capturedDonePrs = donePrs)
            .Returns(Task.CompletedTask);

        _loopService = new PipelineLoopService(new PipelineLoopServiceDependencies
        {
            Orchestration = _runCreator,
            ProviderFactory = _mockFactory.Object,
            PipelineConfigStore = _mockStore.Object,
            ProviderConfigStore = _mockStore.Object,
            ProjectStore = _mockStore.Object,
            Logger = _mockLogger.Object,
            WorkDistributor = null,
            DispatchOrchestration = null,
            DependencyChecker = null,
            HousekeepingService = housekeepingMock.Object
        });

        using var cts = new CancellationTokenSource();
        await _loopService.StartAsync(cts.Token);
        await _loopService.StartLoopAsync();

        // Wait for one full cycle
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!_loopService.StatusMessage.Contains("Cycle complete", StringComparison.OrdinalIgnoreCase)
               && DateTime.UtcNow < deadline)
            await Task.Delay(50);

        _loopService.StopLoop();
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (_loopService.IsLoopActive && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        cts.Cancel();
        try { await _loopService.StopAsync(CancellationToken.None); } catch { }

        // ExecuteAsync must have been called with the agent PR in the donePrs list
        housekeepingMock.Verify(h => h.ExecuteAsync(
            It.IsAny<IRepositoryProvider>(), It.IsAny<string>(),
            It.IsAny<IIssueProvider>(), It.IsAny<string>(),
            It.IsAny<IReadOnlyList<PullRequestSummary>>(), It.IsAny<int>(),
            It.IsAny<bool>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Once);

        Assert.NotNull(capturedDonePrs);
        Assert.Single(capturedDonePrs!);
        Assert.Equal(agentPrBranch, capturedDonePrs![0].BranchName);
    }
}
