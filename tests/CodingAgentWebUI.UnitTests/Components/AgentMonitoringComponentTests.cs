using Bunit;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Hubs;
using CodingAgentWebUI.Orchestration;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Pipeline.Services;
using CodingAgentWebUI.TestUtilities;
using Microsoft.AspNetCore.SignalR;
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
        mockHistory.Setup(h => h.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineRunSummary>());

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
        Services.AddSingleton(Mock.Of<IRunLifecycleManager>());
        Services.AddSingleton<IPendingWorkQuery>(new LegacyPendingWorkQuery(
            Services.BuildServiceProvider().GetRequiredService<JobDispatcherService>()));

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
    public void ActiveRunsTable_HasExpectedColumnCount()
    {
        SetActiveRunSummary(CreateRunSummary("Title"));

        var cut = Render<AgentMonitoring>();

        var headerCells = cut.FindAll(".monitoring-table thead th");
        Assert.Equal(9, headerCells.Count);
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
    public void ActiveRunsTable_ProjectColumn_RendersNameWhenSet()
    {
        var summary = CreateRunSummary("Title") with { ProjectName = "MyProject" };
        SetActiveRunSummary(summary);

        var cut = Render<AgentMonitoring>();

        Assert.Contains("MyProject", cut.Markup);
    }

    [Fact]
    public void ActiveRunsTable_ProjectColumn_RendersDashWhenNull()
    {
        var summary = CreateRunSummary("Title") with { ProjectName = null };
        SetActiveRunSummary(summary);

        var cut = Render<AgentMonitoring>();

        // TODO: Assertion is not scoped to the project column cell position — would pass if any td on the page contains "—". Consider finding the 3rd td in the active runs table body row instead.
        // The project cell should render an em dash
        var cells = cut.FindAll("td");
        Assert.Contains(cells, td => td.TextContent.Trim() == "—");
    }

    [Fact]
    public void ActiveRunsTable_ProjectColumn_RendersDashWhenEmpty()
    {
        var summary = CreateRunSummary("Title") with { ProjectName = "" };
        SetActiveRunSummary(summary);

        var cut = Render<AgentMonitoring>();

        // TODO: Assertion is not scoped to the project column cell position — would pass if any td on the page contains "—". Consider finding the 3rd td in the active runs table body row instead.
        var cells = cut.FindAll("td");
        Assert.Contains(cells, td => td.TextContent.Trim() == "—");
    }

    // TODO: Missing test for Job Queue with Project = new PipelineProject { Name = "" } to verify empty-string handling matches Active Runs behavior.
    [Fact]
    public void JobQueue_ProjectColumn_RendersNameWhenSet()
    {
        var dispatcher = Services.GetRequiredService<JobDispatcherService>();
        dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "org/repo#99",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "test",
            Project = new PipelineProject { Id = "p1", Name = "TestProject" }
        });

        var cut = Render<AgentMonitoring>();

        Assert.Contains("TestProject", cut.Markup);
    }

    [Fact]
    public void JobQueue_ProjectColumn_RendersDashWhenNull()
    {
        var dispatcher = Services.GetRequiredService<JobDispatcherService>();
        dispatcher.EnqueueJob(new PendingJob
        {
            IssueIdentifier = "org/repo#99",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "test",
            Project = null
        });

        var cut = Render<AgentMonitoring>();

        // TODO: Assertion is not scoped to the project column cell position — would pass if any td on the page contains "—". Consider finding the 3rd td in the job queue table body row instead.
        // The job row should contain an em dash for the project cell
        Assert.Contains("org/repo#99", cut.Markup);
        var cells = cut.FindAll("td");
        Assert.Contains(cells, td => td.TextContent.Trim() == "—");
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

    [Fact]
    public void RemoveFromQueue_DbMode_CallsWorkDistributorCancelJobAsync()
    {
        // Arrange: use a mock IPendingWorkQuery that returns a job with WorkItemId (DB mode)
        var workItemId = Guid.NewGuid().ToString();
        var mockPendingQuery = new Mock<IPendingWorkQuery>();
        mockPendingQuery.Setup(q => q.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PendingJob>
            {
                new PendingJob
                {
                    WorkItemId = workItemId,
                    IssueIdentifier = "org/repo#55",
                    IssueProviderId = "ip-1",
                    RepoProviderId = "rp-1",
                    EnqueuedAt = DateTimeOffset.UtcNow,
                    InitiatedBy = "loop"
                }
            });

        var mockWorkDistributor = new Mock<IWorkDistributor>();
        mockWorkDistributor.Setup(w => w.CancelJobAsync(workItemId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        // Override the default registrations
        Services.AddSingleton<IPendingWorkQuery>(mockPendingQuery.Object);
        Services.AddSingleton<IWorkDistributor>(mockWorkDistributor.Object);

        var cut = Render<AgentMonitoring>();

        // Verify job appears
        cut.WaitForAssertion(() => Assert.Contains("org/repo#55", cut.Markup));

        // Act: click the Remove button
        var removeBtn = cut.FindAll("button")
            .First(b => b.TextContent.Contains("Remove"));
        removeBtn.Click();

        // Assert: WorkDistributor.CancelJobAsync was called with the WorkItemId
        cut.WaitForAssertion(() =>
        {
            mockWorkDistributor.Verify(
                w => w.CancelJobAsync(workItemId, It.IsAny<CancellationToken>()),
                Times.Once,
                "In DB/K8s mode, Remove should call WorkDistributor.CancelJobAsync with the WorkItemId");
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
    public async Task CancelButton_ConnectedAgent_CallsCancelRunAsyncAndSendsCancelJob()
    {
        // Arrange: set up mock IRunLifecycleManager and IHubContext to verify cancel behavior
        var mockLifecycleManager = new Mock<IRunLifecycleManager>();
        var mockHubContext = new Mock<IHubContext<AgentHub, IAgentHubClient>>();
        var mockClients = new Mock<IHubClients<IAgentHubClient>>();
        var mockClient = new Mock<IAgentHubClient>();

        mockHubContext.Setup(h => h.Clients).Returns(mockClients.Object);
        mockClients.Setup(c => c.Client("conn-agent-1")).Returns(mockClient.Object);
        mockClient.Setup(c => c.CancelJob("run-connected-1")).Returns(Task.CompletedTask);

        mockLifecycleManager
            .Setup(l => l.CancelRunAsync("run-connected-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync((PipelineRun?)null); // Return value not checked by component

        // Override DI registrations (last-wins in bUnit)
        Services.AddSingleton<IHubContext<AgentHub, IAgentHubClient>>(mockHubContext.Object);
        Services.AddSingleton<IRunLifecycleManager>(mockLifecycleManager.Object);

        // Create an OrchestratorRunService and use it both in DI and inside PipelineOrchestrationService
        var runService = new OrchestratorRunService(Mock.Of<ILogger>());
        Services.AddSingleton(runService);

        // Rebuild PipelineOrchestrationService with the same runService so GetAllActiveRuns() can find it
        var mockStore = new Mock<IConfigurationStore>();
        mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());

        var pipelineService = TestOrchestrationFactory.CreateMinimal(
            configStore: mockStore.Object,
            providerFactory: new Mock<IProviderFactory>().Object,
            runService: runService);
        Services.AddSingleton(pipelineService);

        // Add an in-memory active run with an assigned agent
        var run = new PipelineRun
        {
            RunId = "run-connected-1",
            AgentId = "agent-1",
            IssueIdentifier = "org/repo#100",
            IssueTitle = "Test Issue",
            CurrentStep = PipelineStep.GeneratingCode,
            StartedAt = DateTime.UtcNow.AddMinutes(-2),
            IssueProviderConfigId = "ip-1",
            RepoProviderConfigId = "rp-1"
        };
        runService.AddRun(run);

        // Register the agent in the registry (with matching connection ID)
        var registry = Services.GetRequiredService<AgentRegistryService>();
        registry.Register(new AgentRegistrationMessage
        {
            AgentId = "agent-1",
            Hostname = "test-host",
            Labels = new[] { "kiro" }
        }, "conn-agent-1");

        // Ensure the active run appears in the UI table
        _mockActiveRunQuery.Setup(s => s.GetActiveRunsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[]
            {
                new ActiveRunSummary
                {
                    RunId = "run-connected-1",
                    IssueIdentifier = "org/repo#100",
                    IssueTitle = "Test Issue",
                    RunType = PipelineRunType.Implementation,
                    AgentId = "agent-1",
                    StartedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
                    ProjectName = null,
                    CurrentStep = PipelineStep.GeneratingCode
                }
            });

        var cut = Render<AgentMonitoring>();

        // Act: click the Cancel button in the active runs table
        var cancelBtn = cut.FindAll("button.btn-cancel-small")
            .First(b => b.TextContent.Trim() == "Cancel");
        await cut.InvokeAsync(() => cancelBtn.Click());

        // Assert: CancelJob signal was sent to the agent
        mockClient.Verify(c => c.CancelJob("run-connected-1"), Times.Once,
            "CancelJob signal must be sent to the connected agent");

        // Assert: CancelRunAsync was called to immediately persist the cancelled state
        mockLifecycleManager.Verify(
            l => l.CancelRunAsync("run-connected-1", It.IsAny<CancellationToken>()),
            Times.Once,
            "CancelRunAsync must be called to immediately persist PipelineStep.Cancelled");
    }

    [Fact]
    public void Renders_FreshnessIndicator_InHeader()
    {
        var cut = Render<AgentMonitoring>();

        var header = cut.Find(".agent-header");
        var indicator = header.QuerySelector(".freshness-indicator");
        Assert.NotNull(indicator);
        Assert.Contains("Last updated:", indicator.TextContent);
        Assert.Contains("Refreshing every 5s", indicator.TextContent);
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

        // Wait for real timer to fire (fires after 1s initially) — it will throw and set _lastRefreshFailed.
        // The timer callback is async void so we poll until the flag is set, with a generous timeout for CI.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(250));

            // Check if the component has set _lastRefreshFailed via reflection
            var failedField = cut.Instance.GetType()
                .GetField("_lastRefreshFailed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            if (failedField is not null && (bool)failedField.GetValue(cut.Instance)!)
                break;
        }

        // Force a re-render so the component re-evaluates the staleness expression
        await cut.InvokeAsync(() =>
        {
            var method = typeof(Microsoft.AspNetCore.Components.ComponentBase)
                .GetMethod("StateHasChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            method.Invoke(cut.Instance, null);
        });

        var indicator = cut.Find(".freshness-indicator");
        Assert.Contains("freshness-warning", indicator.ClassName);
        Assert.Contains("(refresh failed)", cut.Markup);
    }
}
