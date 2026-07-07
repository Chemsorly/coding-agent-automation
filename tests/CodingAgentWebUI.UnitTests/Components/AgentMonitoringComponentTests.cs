using Bunit;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Health;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.TestUtilities;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;
using Moq;
using Serilog;

namespace CodingAgentWebUI.UnitTests.Components;

public class AgentMonitoringComponentTests : BunitContext
{
    private readonly PipelineOrchestrationService _pipelineService;
    private readonly Mock<IActiveRunQueryService> _mockActiveRunQuery = new();

    public AgentMonitoringComponentTests()
    {
        var mockLogger = new Mock<ILogger>();
        var mockStore = new Mock<IConfigurationStore>();
        mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());

        var mockFactory = new Mock<IProviderFactory>();
        var mockValidator = new Mock<IQualityGateValidator>();
        var mockHistory = new Mock<IPipelineRunHistoryService>();
        mockHistory.Setup(h => h.GetRunHistory()).Returns(Array.Empty<PipelineRunSummary>());

        _pipelineService = TestOrchestrationFactory.CreateMinimal(
            configStore: mockStore.Object,
            providerFactory: mockFactory.Object,
            historyService: mockHistory.Object);

        var registry = new AgentRegistryService(mockLogger.Object);

        _mockActiveRunQuery.Setup(s => s.GetActiveRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ActiveRunSummary>());

        Services.AddSingleton(registry);
        Services.AddSingleton<IAgentRegistryService>(registry);
        Services.AddSingleton(_pipelineService);
        Services.AddSingleton(new JobDispatcherService(registry, mockLogger.Object));
        Services.AddSingleton(new OrchestratorRunService(mockLogger.Object));
        Services.AddSingleton(mockStore.Object);
        Services.AddSingleton(mockHistory.Object);
        Services.AddSingleton(new Mock<IHubContext<AgentHub, IAgentHubClient>>().Object);
        Services.AddSingleton(new Mock<IJSRuntime>().Object);
        Services.AddSingleton(Mock.Of<ILabelSwapper>());
        Services.AddSingleton(Mock.Of<IConsolidationService>(s =>
            s.GetRunHistoryAsync(It.IsAny<CancellationToken>()) == Task.FromResult<IReadOnlyList<ConsolidationRun>>(Array.Empty<ConsolidationRun>())));
        Services.AddSingleton<IActiveRunQueryService>(_mockActiveRunQuery.Object);
        Services.AddSingleton(Mock.Of<IWorkDistributor>());
        Services.AddSingleton<IPendingWorkQuery>(new LegacyPendingWorkQuery(
            Services.BuildServiceProvider().GetRequiredService<JobDispatcherService>()));

        // InfrastructureHealthService — Legacy mode defaults (no DB, no Redis)
        var emptyConfig = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        Services.AddSingleton(new InfrastructureHealthService(
            new ServiceCollection().BuildServiceProvider(), emptyConfig));
        // TODO: Tests that use FakeTimeProvider add a second registration that shadows this one (last-wins DI behavior); consider a more explicit replacement pattern
        Services.AddSingleton(TimeProvider.System);
    }

    [Fact]
    public void Renders_EmptyState_WhenNoActiveRuns()
    {
        var cut = Render<AgentMonitoring>();

        Assert.Contains("No active pipeline runs.", cut.Markup);
    }

    [Fact]
    public void Renders_EmptyState_WhenNoAgents()
    {
        var cut = Render<AgentMonitoring>();

        Assert.Contains("No agents registered.", cut.Markup);
    }

    [Fact]
    public void Renders_EmptyState_WhenNoQueuedJobs()
    {
        var cut = Render<AgentMonitoring>();

        Assert.Contains("No pending jobs in queue.", cut.Markup);
    }

    [Fact]
    public void Renders_AllThreeSections()
    {
        var cut = Render<AgentMonitoring>();

        Assert.Contains("Active Runs", cut.Markup);
        Assert.Contains("Registered Agents", cut.Markup);
        Assert.Contains("Job Queue", cut.Markup);
    }

    [Fact]
    public void ActiveRunsTable_DisplaysFullIssueTitle_WithoutTruncation()
    {
        var longTitle = "[ARC-07b] State machine property tests for pipeline step transitions";
        SetActiveRunSummary(CreateRunSummary(longTitle));

        var cut = Render<AgentMonitoring>();

        // The full title should appear in the markup (not server-side truncated)
        Assert.Contains(longTitle, cut.Markup);
    }

    [Fact]
    public void ActiveRunsTable_HasTitleAttributes_ForTooltips()
    {
        SetActiveRunSummary(CreateRunSummary("Test Title"));

        var cut = Render<AgentMonitoring>();

        var tdsWithTitle = cut.FindAll("td[title]");
        Assert.NotEmpty(tdsWithTitle);

        // Issue cell should have title with full text
        var issueTd = tdsWithTitle.FirstOrDefault(td => td.GetAttribute("title")?.Contains("Test Title") == true);
        Assert.NotNull(issueTd);
    }

    [Fact]
    public void ActiveRunsTable_HasSevenColumns()
    {
        SetActiveRunSummary(CreateRunSummary("Title"));

        var cut = Render<AgentMonitoring>();

        var headerCells = cut.FindAll(".monitoring-table thead th");
        Assert.Equal(8, headerCells.Count);
    }

    [Fact]
    public void ActiveRunsTable_RunIdCell_HasTitleWithFullId()
    {
        var summary = CreateRunSummary("Title");
        SetActiveRunSummary(summary);

        var cut = Render<AgentMonitoring>();

        var monoTds = cut.FindAll("td.monitoring-mono[title]");
        Assert.NotEmpty(monoTds);

        // The first mono td should have the full run ID as title
        Assert.Equal(summary.RunId, monoTds[0].GetAttribute("title"));
    }

    [Fact]
    public void ActiveRunsTable_ExcludesRuns_WithNullAgentId()
    {
        var unassigned = CreateRunSummary("Unassigned Issue") with { AgentId = null, RunId = "unassigned-run-id-0000-0000-000000000001" };
        var assigned = CreateRunSummary("Assigned Issue") with { AgentId = "agent-1", RunId = "assigned-run-id-00000-0000-000000000002" };

        _mockActiveRunQuery.Setup(s => s.GetActiveRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { unassigned, assigned });

        var cut = Render<AgentMonitoring>();

        Assert.Contains("Assigned Issue", cut.Markup);
        Assert.DoesNotContain("Unassigned Issue", cut.Markup);
        Assert.Contains("Active Runs (1)", cut.Markup);
    }

    [Fact]
    public void ActiveRunsTable_ExcludesRuns_WithEmptyAgentId()
    {
        var emptyAgent = CreateRunSummary("Empty Agent Issue") with { AgentId = "" };

        _mockActiveRunQuery.Setup(s => s.GetActiveRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { emptyAgent });

        var cut = Render<AgentMonitoring>();

        Assert.DoesNotContain("Empty Agent Issue", cut.Markup);
        Assert.Contains("No active pipeline runs.", cut.Markup);
    }

    [Fact]
    public void RemoveFromQueue_Button_RemovesJobAndUpdatesUI()
    {
        // Arrange: enqueue a job
        var dispatcher = Services.GetRequiredService<JobDispatcherService>();
        dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "org/repo#42",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "test"
        });

        var cut = Render<AgentMonitoring>();

        // Verify job appears in the queue
        Assert.Contains("org/repo#42", cut.Markup);
        Assert.Contains("Job Queue (1)", cut.Markup);

        // Act: click the Remove button
        var removeBtn = cut.FindAll("button")
            .First(b => b.TextContent.Contains("Remove"));
        removeBtn.Click();

        // Assert: job is removed from UI
        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("org/repo#42", cut.Markup);
            Assert.Contains("No pending jobs in queue.", cut.Markup);
        });
    }

    [Fact]
    public void RemoveFromQueue_Button_RemovesCorrectJob_WhenMultipleQueued()
    {
        // Arrange: enqueue two jobs
        var dispatcher = Services.GetRequiredService<JobDispatcherService>();
        dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "org/repo#10",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "loop"
        });
        dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "org/repo#20",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "loop"
        });

        var cut = Render<AgentMonitoring>();
        Assert.Contains("Job Queue (2)", cut.Markup);

        // Act: click the Remove button for the first job
        var removeButtons = cut.FindAll("button.btn-cancel-small")
            .Where(b => b.TextContent.Trim() == "Remove")
            .ToList();
        removeButtons[0].Click();

        // Assert: first job removed, second remains
        cut.WaitForAssertion(() =>
        {
            Assert.DoesNotContain("org/repo#10", cut.Markup);
            Assert.Contains("org/repo#20", cut.Markup);
            Assert.Contains("Job Queue (1)", cut.Markup);
        });
    }

    private static PipelineRun CreateRun(string issueTitle) => new()
    {
        RunId = "abcd1234-5678-9012-3456-789012345678",
        IssueIdentifier = "194",
        IssueTitle = issueTitle,
        CurrentStep = PipelineStep.GeneratingCode,
        StartedAt = DateTime.UtcNow.AddMinutes(-5),
        IssueProviderConfigId = "ip-1",
        RepoProviderConfigId = "rp-1"
    };

    private void SetActiveRun(PipelineRun run)
    {
        var prop = typeof(PipelineOrchestrationService).GetProperty("ActiveRun")!;
        prop.SetValue(_pipelineService, run);
    }

    private static ActiveRunSummary CreateRunSummary(string issueTitle) => new()
    {
        RunId = "abcd1234-5678-9012-3456-789012345678",
        IssueIdentifier = "194",
        IssueTitle = issueTitle,
        RunType = PipelineRunType.Implementation,
        AgentId = "agent-dotnet-1",
        StartedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        ProjectName = null,
        CurrentStep = PipelineStep.GeneratingCode
    };

    private void SetActiveRunSummary(ActiveRunSummary summary)
    {
        _mockActiveRunQuery.Setup(s => s.GetActiveRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { summary });
    }

    [Fact]
    public void Renders_FreshnessIndicator_InHeader()
    {
        var cut = Render<AgentMonitoring>();

        var header = cut.Find(".agent-header");
        var indicator = header.QuerySelector(".freshness-indicator");
        Assert.NotNull(indicator);
        Assert.Contains("Last updated:", indicator.TextContent);
        Assert.Contains("Refreshing every 2s", indicator.TextContent);
    }

    [Fact]
    public void FreshnessIndicator_NoWarning_WhenFresh()
    {
        var cut = Render<AgentMonitoring>();

        var indicator = cut.Find(".freshness-indicator");
        Assert.DoesNotContain("freshness-warning", indicator.ClassName);
    }

    [Fact]
    public async Task FreshnessIndicator_ShowsWarning_WhenStale()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Services.AddSingleton<TimeProvider>(fakeTime);

        var cut = Render<AgentMonitoring>();

        // Advance fake clock past 30s staleness threshold.
        // _lastSuccessfulRefresh was set at init (fake time T=0), so Clock.GetUtcNow() - _lastSuccessfulRefresh > 30s.
        // _lastRefreshFailed remains false — this tests the pure clock-based staleness path.
        // TODO: Race condition — the real System.Threading.Timer (1s initial, 2s interval) could fire between Advance and assertion, resetting _lastSuccessfulRefresh to T+31 and making staleness 0s. Consider disposing the timer or mocking RefreshDataAsync to prevent successful refresh after init.
        fakeTime.Advance(TimeSpan.FromSeconds(31));

        // Force a re-render so the component re-evaluates the staleness expression
        await cut.InvokeAsync(() =>
        {
            // TODO: Using reflection to call StateHasChanged is brittle; consider bUnit's cut.Render() if available in future versions
            var method = typeof(Microsoft.AspNetCore.Components.ComponentBase)
                .GetMethod("StateHasChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            method.Invoke(cut.Instance, null);
        });

        var indicator = cut.Find(".freshness-indicator");
        Assert.Contains("freshness-warning", indicator.ClassName);
        // Verify it's the clock-based staleness, not refresh failure
        Assert.Contains("(stale)", cut.Markup);
        Assert.DoesNotContain("(refresh failed)", cut.Markup);
    }

    [Fact]
    public async Task FreshnessIndicator_ShowsRefreshFailed_WhenExceptionOccurs()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Services.AddSingleton<TimeProvider>(fakeTime);

        var cut = Render<AgentMonitoring>();

        // After init succeeds, change mock to throw on subsequent timer-triggered calls
        _mockActiveRunQuery.Setup(s => s.GetActiveRunsAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection lost"));

        // TODO: Task.Delay in unit tests is non-deterministic and slow; consider a deterministic timer trigger mechanism
        // Wait for real timer to fire (fires after 1s initially) — it will throw and set _lastRefreshFailed
        await Task.Delay(TimeSpan.FromSeconds(3));

        // Force a re-render so the component re-evaluates the staleness expression
        await cut.InvokeAsync(() =>
        {
            // TODO: Using reflection to call StateHasChanged is brittle; consider bUnit's cut.Render() if available in future versions
            var method = typeof(Microsoft.AspNetCore.Components.ComponentBase)
                .GetMethod("StateHasChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            method.Invoke(cut.Instance, null);
        });

        var indicator = cut.Find(".freshness-indicator");
        Assert.Contains("freshness-warning", indicator.ClassName);
        Assert.Contains("(refresh failed)", cut.Markup);
    }
}
