using Bunit;
using CodingAgentWebUI.Api.Client;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Orchestration.Dispatch;
using CodingAgentWebUI.Orchestration.Registry;
using CodingAgentWebUI.Pipeline.Interfaces;
using CodingAgentWebUI.Pipeline.Models;
using CodingAgentWebUI.Services;
using CodingAgentWebUI.TestUtilities;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Microsoft.JSInterop;
using Moq;
using Serilog;

namespace CodingAgentWebUI.UnitTests.Components;

public class AgentMonitoringComponentTests : BunitContext
{
    private readonly Mock<IPipelineApiRunHistoryClient> _mockRunHistoryClient = new();

    public AgentMonitoringComponentTests()
    {
        var mockLogger = new Mock<ILogger>();
        var mockFactory = new Mock<IProviderFactory>();
        var mockValidator = new Mock<IQualityGateValidator>();

        var registry = new AgentRegistryService(mockLogger.Object);

        // Spec 045: use IPipelineApiConfigClient instead of IConfigurationStore
        var mockConfigClient = new Mock<IPipelineApiConfigClient>();
        mockConfigClient.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        mockConfigClient.Setup(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        mockConfigClient.Setup(c => c.GetQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        mockConfigClient.Setup(c => c.GetProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        // IConfigurationStore is still needed by child razor components that @inject it directly
        // (e.g., HistoryRunDetailModal, ProviderSelectionPanel). These are not yet migrated.
        var mockStore = new Mock<IConfigurationStore>();
        mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        mockStore.Setup(s => s.LoadProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        // Default: no history (no active runs derived from it)
        _mockRunHistoryClient.Setup(c => c.GetRunAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PipelineRunSummary?)null);
        _mockRunHistoryClient.Setup(c => c.GetRunHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary> { Items = Array.Empty<PipelineRunSummary>(), Page = 1, PageSize = 1000, HasMore = false });

        Services.AddSingleton(registry);
        Services.AddSingleton<IAgentRegistryService>(registry);
        Services.AddSingleton(new JobDeduplicationGuardService(registry, mockLogger.Object));
        Services.AddSingleton<IPipelineApiConfigClient>(mockConfigClient.Object);
        Services.AddSingleton<IConfigurationStore>(mockStore.Object);
        Services.AddSingleton(new Mock<IJSRuntime>().Object);
        Services.AddSingleton(Mock.Of<ILabelService>());
        Services.AddSingleton(Mock.Of<IConsolidationService>(s =>
            s.GetRunHistoryAsync(It.IsAny<CancellationToken>()) == Task.FromResult<IReadOnlyList<ConsolidationRun>>(Array.Empty<ConsolidationRun>())));
        var _mockWorkDistributor = new Mock<IWorkDistributor>();
        _mockWorkDistributor.Setup(w => w.CancelJobAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        Services.AddSingleton(_mockWorkDistributor.Object);
        Services.AddSingleton(_mockWorkDistributor); // expose Mock for test verification

        // Spec 045: IAgentHubConnection and IPipelineApiRunHistoryClient now injected by AgentMonitoring.
        // Registered as Singleton (not Scoped) to prevent DI from calling Dispose() on the mock proxy
        // which only implements IAsyncDisposable — the Scoped lifetime would cause bunit to fail on teardown.
        var mockHubConnection = new Mock<IAgentHubConnection>();
        mockHubConnection.SetupGet(h => h.State).Returns(HubConnectionState.Disconnected);
        mockHubConnection.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);
        mockHubConnection.Setup(h => h.On<string, IReadOnlyList<string>>(It.IsAny<string>(), It.IsAny<Action<string, IReadOnlyList<string>>>()))
            .Returns(Mock.Of<IDisposable>());
        mockHubConnection.Setup(h => h.On<string, PipelineStep, DateTimeOffset>(It.IsAny<string>(), It.IsAny<Action<string, PipelineStep, DateTimeOffset>>()))
            .Returns(Mock.Of<IDisposable>());
        mockHubConnection.Setup(h => h.On<string, JobCompletionPayload>(It.IsAny<string>(), It.IsAny<Action<string, JobCompletionPayload>>()))
            .Returns(Mock.Of<IDisposable>());
        Services.AddSingleton<IAgentHubConnection>(mockHubConnection.Object);
        Services.AddSingleton<IPipelineApiRunHistoryClient>(_mockRunHistoryClient.Object);

        // Use a shared mutable list so individual tests can populate queued jobs
        // directly without needing the now-deleted EnqueueJob/GetQueuedJobs methods.
        var pendingJobsList = new List<PendingJob>();
        var mockPendingQuery = new Mock<IPendingWorkQuery>();
        mockPendingQuery.Setup(q => q.GetPendingJobsAsync(It.IsAny<CancellationToken>()))
            .Returns(() => Task.FromResult<IReadOnlyList<PendingJob>>(pendingJobsList.ToList()));
        Services.AddSingleton<IPendingWorkQuery>(mockPendingQuery.Object);
        // Expose the list for test use via a container tag
        Services.AddSingleton(pendingJobsList);

        Services.AddSingleton<TimeProvider>(new FakeTimeProvider());

        // Register AgentMonitoringPageServiceDependencies so DI can auto-construct AgentMonitoringPageService.
        // Spec 045: IActiveRunQueryService removed — active runs derived from IPipelineApiRunHistoryClient.
        Services.AddScoped(sp => new AgentMonitoringPageServiceDependencies(
            sp.GetRequiredService<IAgentRegistryService>(),
            sp.GetRequiredService<JobDeduplicationGuardService>(),
            sp.GetRequiredService<IPipelineApiConfigClient>(),
            sp.GetRequiredService<IConsolidationService>(),
            sp.GetRequiredService<IPendingWorkQuery>(),
            sp.GetRequiredService<IWorkDistributor>(),
            sp.GetRequiredService<IPipelineApiRunHistoryClient>()));

        // Page service — resolved via DI with all dependencies above
        Services.AddScoped<AgentMonitoringPageService>();
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

        // Scope to the first monitoring-table (active runs section)
        var activeRunsTable = cut.Find(".monitoring-table");
        var headerCells = activeRunsTable.QuerySelectorAll("thead th");
        Assert.Equal(9, headerCells.Length);
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

        // Seed both as non-terminal history entries; the service filters out null-AgentId runs
        _mockRunHistoryClient.Setup(c => c.GetRunHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = new[]
                {
                    MapToRunSummary(unassigned),
                    MapToRunSummary(assigned)
                },
                Page = 1, PageSize = 1000, HasMore = false
            });

        var cut = Render<AgentMonitoring>();

        // Active runs table (first monitoring-table) shows only runs with AgentId
        var activeRunsSection = cut.Find(".monitoring-table");
        Assert.Contains("Assigned Issue", activeRunsSection.InnerHtml);
        Assert.DoesNotContain("Unassigned Issue", activeRunsSection.InnerHtml);
        // Active Runs count header should show only 1 (the assigned run)
        Assert.Contains("Active Runs (1)", cut.Markup);
    }

    [Fact]
    public void ActiveRunsTable_ExcludesRuns_WithEmptyAgentId()
    {
        var emptyAgent = CreateRunSummary("Empty Agent Issue") with { AgentId = null };

        _mockRunHistoryClient.Setup(c => c.GetRunHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = new[] { MapToRunSummary(emptyAgent) },
                Page = 1, PageSize = 1000, HasMore = false
            });

        var cut = Render<AgentMonitoring>();

        // Active runs section should show empty state (null AgentId excluded)
        Assert.Contains("No active pipeline runs.", cut.Markup);
        Assert.Contains("Active Runs (0)", cut.Markup);
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

        var cells = cut.FindAll("td");
        Assert.Contains(cells, td => td.TextContent.Trim() == "—");
    }

    [Fact]
    public void JobQueue_ProjectColumn_RendersNameWhenSet()
    {
        var jobs = Services.GetRequiredService<List<PendingJob>>();
        jobs.Add(new PendingJob
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
        var jobs = Services.GetRequiredService<List<PendingJob>>();
        jobs.Add(new PendingJob
        {
            IssueIdentifier = "org/repo#99",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "test",
            Project = null
        });

        var cut = Render<AgentMonitoring>();

        // The job row should contain an em dash for the project cell
        Assert.Contains("org/repo#99", cut.Markup);
        var cells = cut.FindAll("td");
        Assert.Contains(cells, td => td.TextContent.Trim() == "—");
    }

    [Fact]
    public async Task RemoveFromQueue_Button_CallsCancelJob()
    {
        // Arrange: add a job with WorkItemId via the shared pending jobs list
        var jobs = Services.GetRequiredService<List<PendingJob>>();
        jobs.Add(new PendingJob
        {
            IssueIdentifier = "org/repo#42",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            WorkItemId = "wi-42",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "test"
        });

        var mockWorkDistributor = Services.GetRequiredService<Mock<IWorkDistributor>>();

        var cut = Render<AgentMonitoring>();

        // Verify job appears in the queue
        cut.WaitForAssertion(() => Assert.Contains("org/repo#42", cut.Markup));

        // Act: click the Remove button
        await cut.InvokeAsync(() =>
        {
            var removeBtn = cut.FindAll("button").First(b => b.TextContent.Contains("Remove"));
            removeBtn.Click();
        });

        // Assert: CancelJobAsync was called with the WorkItemId
        mockWorkDistributor.Verify(
            w => w.CancelJobAsync(It.Is<JobId>(j => j.Value == "wi-42"), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task RemoveFromQueue_Button_CallsCancelJob_ForFirstJob_WhenMultipleQueued()
    {
        // Arrange: add two jobs
        var jobs = Services.GetRequiredService<List<PendingJob>>();
        jobs.Add(new PendingJob
        {
            IssueIdentifier = "org/repo#10",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            WorkItemId = "wi-10",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "loop"
        });
        jobs.Add(new PendingJob
        {
            IssueIdentifier = "org/repo#20",
            IssueProviderId = "ip-1",
            RepoProviderId = "rp-1",
            WorkItemId = "wi-20",
            EnqueuedAt = DateTimeOffset.UtcNow,
            InitiatedBy = "loop"
        });

        var mockWorkDistributor = Services.GetRequiredService<Mock<IWorkDistributor>>();

        var cut = Render<AgentMonitoring>();
        cut.WaitForAssertion(() => Assert.Contains("Job Queue (2)", cut.Markup));

        // Act: click the Remove button for the first job
        await cut.InvokeAsync(() =>
        {
            var removeButtons = cut.FindAll("button.btn-cancel-small")
                .Where(b => b.TextContent.Trim() == "Remove")
                .ToList();
            removeButtons[0].Click();
        });

        // Assert: CancelJobAsync was called for one of the jobs
        mockWorkDistributor.Verify(
            w => w.CancelJobAsync(It.IsAny<JobId>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task RemoveFromQueue_DbMode_CallsWorkDistributorCancelJobAsync()
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

        // Act: click the Remove button (wrap in InvokeAsync to avoid render-tree race)
        await cut.InvokeAsync(() =>
        {
            var removeBtn = cut.FindAll("button")
                .First(b => b.TextContent.Contains("Remove"));
            removeBtn.Click();
        });

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

    /// <summary>
    /// Helper that creates a PipelineRunSummary representing an active (non-terminal) run
    /// from an ActiveRunSummary. Used to seed the run history client in Spec 045 tests.
    /// </summary>
    private static PipelineRunSummary MapToRunSummary(ActiveRunSummary summary) => new()
    {
        RunId = summary.RunId,
        IssueIdentifier = summary.IssueIdentifier,
        IssueTitle = summary.IssueTitle,
        RunType = summary.RunType,
        AgentId = summary.AgentId?.Value,
        StartedAt = summary.StartedAt.UtcDateTime,
        StartedAtOffset = summary.StartedAt,
        ProjectName = summary.ProjectName,
        FinalStep = summary.CurrentStep   // non-terminal step → treated as active
    };

    private void SetActiveRunSummary(ActiveRunSummary summary)
    {
        // Spec 045: active runs are derived from run history by filtering non-terminal steps.
        // Seed the run history client with a matching PipelineRunSummary so the service picks it up.
        _mockRunHistoryClient.Setup(c => c.GetRunHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = new[]
                {
                    new PipelineRunSummary
                    {
                        RunId = summary.RunId,
                        IssueIdentifier = summary.IssueIdentifier,
                        IssueTitle = summary.IssueTitle,
                        RunType = summary.RunType,
                        AgentId = summary.AgentId?.Value,
                        StartedAt = summary.StartedAt.UtcDateTime,
                        StartedAtOffset = summary.StartedAt,
                        ProjectName = summary.ProjectName,
                        FinalStep = summary.CurrentStep   // non-terminal step → appears as active
                    }
                },
                Page = 1,
                PageSize = 1000,
                HasMore = false
            });
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

    [Fact]
    public async Task CancelButton_ConnectedAgent_CallsCancelJobViaWorkDistributor()
    {
        // Spec 044 (degraded mode): cancel routes through IWorkDistributor — no hub context.
        var mockWorkDistributor = new Mock<IWorkDistributor>();
        mockWorkDistributor
            .Setup(w => w.CancelJobAsync("run-connected-1", It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        Services.AddSingleton<IWorkDistributor>(mockWorkDistributor.Object);

        // Seed an active run via run history (non-terminal step, AgentId set)
        _mockRunHistoryClient.Setup(c => c.GetRunHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = new[]
                {
                    new PipelineRunSummary
                    {
                        RunId = "run-connected-1",
                        IssueIdentifier = "org/repo#100",
                        IssueTitle = "Test Issue",
                        RunType = PipelineRunType.Implementation,
                        AgentId = "agent-1",
                        StartedAt = DateTime.UtcNow.AddMinutes(-2),
                        StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-2),
                        ProjectName = null,
                        FinalStep = PipelineStep.GeneratingCode   // non-terminal → active
                    }
                },
                Page = 1, PageSize = 1000, HasMore = false
            });

        var cut = Render<AgentMonitoring>();

        // Act: click the Cancel button in the active runs table
        var cancelBtn = cut.FindAll("button.btn-cancel-small")
            .First(b => b.TextContent.Trim() == "Cancel");
        await cut.InvokeAsync(() => cancelBtn.Click());

        // Assert: cancel was routed through IWorkDistributor (degraded mode)
        mockWorkDistributor.Verify(
            w => w.CancelJobAsync("run-connected-1", It.IsAny<CancellationToken>()),
            Times.Once,
            "In Spec 044 degraded mode, cancel routes through IWorkDistributor");
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

        // Set _lastSuccessfulRefresh directly to 31s in the past so Clock.GetUtcNow() - _lastSuccessfulRefresh > 30s.
        // We do NOT call fakeTime.Advance() because that would also fire the component's ITimer (1s due time),
        // triggering RefreshTick which would reset _lastSuccessfulRefresh and defeat the test.
        await cut.InvokeAsync(() =>
        {
            var field = cut.Instance.GetType()
                .GetField("_lastSuccessfulRefresh", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            field.SetValue(cut.Instance, fakeTime.GetUtcNow().Subtract(TimeSpan.FromSeconds(31)));

            var stateChanged = typeof(Microsoft.AspNetCore.Components.ComponentBase)
                .GetMethod("StateHasChanged", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            stateChanged.Invoke(cut.Instance, null);
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

        // After init succeeds, make subsequent refreshes throw
        _mockRunHistoryClient.Setup(c => c.GetRunHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection lost"));

        // Fire RefreshTick directly — no real timer needed.
        // RefreshTick is async void; invoking it via InvokeAsync ensures bUnit processes
        // the resulting StateHasChanged before we assert.
        await cut.InvokeAsync(() =>
        {
            var method = cut.Instance.GetType()
                .GetMethod("RefreshTick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            method.Invoke(cut.Instance, [null]);
        });

        // Wait for the async void RefreshTick to complete and re-render
        cut.WaitForAssertion(() =>
        {
            var indicator = cut.Find(".freshness-indicator");
            Assert.Contains("freshness-warning", indicator.ClassName);
        }, timeout: TimeSpan.FromSeconds(5));

        Assert.Contains("(refresh failed)", cut.Markup);
    }

    /// <summary>
    /// Verifies that when RefreshTick throws, the component automatically re-renders
    /// to show the staleness warning without external StateHasChanged calls.
    /// Unlike FreshnessIndicator_ShowsRefreshFailed_WhenExceptionOccurs (which manually
    /// triggers StateHasChanged via reflection), this test proves the fix works end-to-end.
    /// </summary>
    [Fact]
    public async Task FreshnessIndicator_RendersWarningAutomatically_WhenRefreshFails()
    {
        var fakeTime = new FakeTimeProvider(DateTimeOffset.UtcNow);
        Services.AddSingleton<TimeProvider>(fakeTime);

        var cut = Render<AgentMonitoring>();

        // After init succeeds, make subsequent refreshes throw
        _mockRunHistoryClient.Setup(c => c.GetRunHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("connection lost"));

        // Fire RefreshTick directly; its async void body calls StateHasChanged after the throw,
        // so WaitForAssertion below picks up the re-render without manual StateHasChanged.
        await cut.InvokeAsync(() =>
        {
            var method = cut.Instance.GetType()
                .GetMethod("RefreshTick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            method.Invoke(cut.Instance, [null]);
        });

        cut.WaitForAssertion(() =>
        {
            var indicator = cut.Find(".freshness-indicator");
            Assert.Contains("freshness-warning", indicator.ClassName);
            Assert.Contains("(refresh failed)", cut.Markup);
        }, timeout: TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Regression test: RefreshTick must poll consolidation run status so completed runs
    /// disappear from Active Runs without a full page reload.
    /// Before the fix, RefreshTick called RefreshDataAsync(includeConsolidation: false),
    /// leaving stale consolidation runs visible indefinitely.
    /// </summary>
    /// <summary>
    /// A run with FinalStep = PipelineStep.Completed (a terminal step) and a valid AgentId
    /// must NOT appear in the Active Runs table. Terminal-step runs belong in Recent Runs.
    /// Active Runs count header must show 0.
    /// </summary>
    [Fact]
    public void ActiveRunsTable_ExcludesRuns_WithTerminalFinalStep()
    {
        var completedRun = new PipelineRunSummary
        {
            RunId = "completed-run-0000-0000-000000000001",
            IssueIdentifier = "org/repo#200",
            IssueTitle = "Completed Issue",
            RunType = PipelineRunType.Implementation,
            AgentId = "agent-1",                        // AgentId set — would appear active if step were non-terminal
            StartedAt = DateTime.UtcNow.AddMinutes(-10),
            StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-10),
            ProjectName = null,
            FinalStep = PipelineStep.Completed           // terminal step → must NOT be treated as active
        };

        _mockRunHistoryClient.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = new[] { completedRun },
                Page = 1,
                PageSize = 1000,
                HasMore = false
            });

        var cut = Render<AgentMonitoring>();

        // Active Runs section must show zero count — completed runs are NOT active
        Assert.Contains("Active Runs (0)", cut.Markup);

        // The empty-state message confirms the active runs section has no items
        Assert.Contains("No active pipeline runs.", cut.Markup);

        // Find the ActiveRunsSection's parent settings-section — it uses monitoring-empty
        // div when empty (not a monitoring-table), so the absence of a table with this
        // issue title verifies the run is excluded from active runs.
        var monitoringEmpty = cut.Find(".monitoring-empty");
        Assert.Equal("No active pipeline runs.", monitoringEmpty.TextContent.Trim());
    }

    [Fact]
    public async Task RefreshTick_RemovesCompletedConsolidationRuns_FromActiveDisplay()
    {
        // Arrange: register a consolidation service mock that initially returns a Running run
        var consolidationMock = new Mock<IConsolidationService>();
        var runningRun = new ConsolidationRun
        {
            RunId = Guid.NewGuid().ToString(),
            Type = ConsolidationRunType.RefactoringDetection,
            TemplateId = "tmpl-1",
            TemplateName = "TestTemplate",
            StartedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-5),
            Status = ConsolidationRunStatus.Running
        };

        consolidationMock.Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun> { runningRun });

        // Override the default Mock.Of registration with our controllable mock
        Services.AddSingleton<IConsolidationService>(consolidationMock.Object);

        var cut = Render<AgentMonitoring>();

        // Assert: the running consolidation run appears in active runs on initial load
        Assert.Contains("consolidation", cut.Markup.ToLowerInvariant());
        Assert.Contains(runningRun.RunId[..8], cut.Markup);

        // Act: simulate run completion — mock now returns Succeeded status
        runningRun.Status = ConsolidationRunStatus.Succeeded;
        runningRun.CompletedAtUtc = DateTimeOffset.UtcNow;

        // Fire RefreshTick directly instead of waiting for the real timer.
        // RefreshTick calls RefreshDataAsync(includeConsolidation: true), which polls the mock
        // and updates the consolidation run list. InvokeAsync drains the render queue before returning.
        await cut.InvokeAsync(() =>
        {
            var method = cut.Instance.GetType()
                .GetMethod("RefreshTick", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            method.Invoke(cut.Instance, [null]);
        });

        // Wait for the async void RefreshTick body to complete and the component to re-render
        cut.WaitForAssertion(
            () => Assert.DoesNotContain(runningRun.RunId[..8], cut.Markup),
            timeout: TimeSpan.FromSeconds(5));

        Assert.True(!cut.Markup.Contains(runningRun.RunId[..8]),
            "Completed consolidation run should disappear from Active Runs after RefreshTick polls — " +
            "indicates RefreshTick includes consolidation state in its polling cycle.");
    }
}
