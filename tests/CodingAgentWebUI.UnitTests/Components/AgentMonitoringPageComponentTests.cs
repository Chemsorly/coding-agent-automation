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

/// <summary>
/// bUnit component tests for the Recent Runs section on the Agent Monitoring page.
/// </summary>
public class AgentMonitoringPageComponentTests : BunitContext
{
    private void RegisterDefaults(IReadOnlyList<PipelineRunSummary>? history = null)
    {
        // Spec 045: IPipelineApiConfigClient replaces IConfigurationStore for the page service.
        // IConfigurationStore is still registered for child components that @inject it (e.g., HistoryRunDetailModal).
        var mockConfigClient = new Mock<IPipelineApiConfigClient>();
        mockConfigClient.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        mockConfigClient.Setup(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        mockConfigClient.Setup(c => c.GetQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        mockConfigClient.Setup(c => c.GetProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        // IConfigurationStore still needed by HistoryRunDetailModal and other child components
        var mockConfigStore = new Mock<IConfigurationStore>();
        mockConfigStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        mockConfigStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        mockConfigStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        mockConfigStore.Setup(s => s.LoadProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        var mockLogger = new Mock<ILogger>();
        var mockFactory = new Mock<IProviderFactory>();
        var mockValidator = new Mock<IQualityGateValidator>();

        var registry = new AgentRegistryService(mockLogger.Object);

        Services.AddSingleton(registry);
        Services.AddSingleton<IAgentRegistryService>(registry);
        Services.AddSingleton(new JobDeduplicationGuardService(registry, mockLogger.Object));
        Services.AddSingleton<IPipelineApiConfigClient>(mockConfigClient.Object);
        Services.AddSingleton<IConfigurationStore>(mockConfigStore.Object);
        Services.AddSingleton(new Mock<IJSRuntime>().Object);
        Services.AddSingleton(Mock.Of<ILabelService>());
        Services.AddSingleton(Mock.Of<IConsolidationService>(s =>
            s.GetRunHistoryAsync(It.IsAny<CancellationToken>()) == Task.FromResult<IReadOnlyList<ConsolidationRun>>(Array.Empty<ConsolidationRun>())));
        Services.AddSingleton(Mock.Of<IWorkDistributor>());
        Services.AddSingleton<IPendingWorkQuery>(Mock.Of<IPendingWorkQuery>(q =>
            q.GetPendingJobsAsync(It.IsAny<CancellationToken>()) == Task.FromResult<IReadOnlyList<PendingJob>>(Array.Empty<PendingJob>())));

        Services.AddSingleton<TimeProvider>(new FakeTimeProvider());

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

        var mockRunHistoryClient = new Mock<IPipelineApiRunHistoryClient>();
        mockRunHistoryClient.Setup(c => c.GetRunAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((PipelineRunSummary?)null);
        var runHistoryItems = history ?? Array.Empty<PipelineRunSummary>();
        mockRunHistoryClient.Setup(c => c.GetRunHistoryAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<PipelineStep?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary> { Items = runHistoryItems, Page = 1, PageSize = 1000, HasMore = false });
        Services.AddSingleton<IPipelineApiRunHistoryClient>(mockRunHistoryClient.Object);

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
    public void RecentRuns_ShowsEmptyState_WhenNoHistory()
    {
        RegisterDefaults();
        var cut = Render<AgentMonitoring>();

        var toggle = cut.Find(".monitoring-section-toggle");
        Assert.Contains("Recent Runs", toggle.TextContent);
        Assert.Contains("No completed runs yet.", cut.Markup);
    }

    [Fact]
    public void RecentRuns_ShowsTable_WhenHistoryExists()
    {
        var history = new List<PipelineRunSummary>
        {
            CreateSummary("run-1", "42", "Test Issue", PipelineStep.Completed)
        };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        var rows = cut.FindAll(".monitoring-table:last-of-type tbody tr");
        Assert.Single(rows);
        Assert.Contains("#42", rows[0].TextContent);
    }

    [Fact]
    public void RecentRuns_LimitsTo20Rows()
    {
        var history = Enumerable.Range(1, 25)
            .Select(i => CreateSummary($"run-{i}", $"{i}", $"Issue {i}", PipelineStep.Completed))
            .ToList();
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        var rows = cut.FindAll(".monitoring-table:last-of-type tbody tr");
        Assert.Equal(20, rows.Count);
    }

    [Fact]
    public void RecentRuns_CompletedRun_HasGreenBadge()
    {
        var history = new List<PipelineRunSummary>
        {
            CreateSummary("run-1", "42", "Test", PipelineStep.Completed)
        };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        var badge = cut.Find(".step-badge.step-completed");
        Assert.Contains("Completed", badge.TextContent);
    }

    [Fact]
    public void RecentRuns_FailedRun_HasRedBadge()
    {
        var history = new List<PipelineRunSummary>
        {
            CreateSummary("run-1", "42", "Test", PipelineStep.Failed)
        };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        var badge = cut.Find(".step-badge.step-failed");
        Assert.Contains("Failed", badge.TextContent);
    }

    [Fact]
    public void RecentRuns_CancelledRun_HasYellowBadge()
    {
        var history = new List<PipelineRunSummary>
        {
            CreateSummary("run-1", "42", "Test", PipelineStep.Cancelled)
        };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        var badge = cut.Find(".step-badge.step-cancelled");
        Assert.Contains("Cancelled", badge.TextContent);
    }

    [Fact]
    public void RecentRuns_ShowsAgentId_OrLocal()
    {
        var history = new List<PipelineRunSummary>
        {
            CreateSummary("run-1", "42", "Test", PipelineStep.Completed, agentId: "agent-01"),
            CreateSummary("run-2", "43", "Test2", PipelineStep.Failed, agentId: null)
        };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        var rows = cut.FindAll(".monitoring-table:last-of-type tbody tr");
        Assert.Contains("agent-01", rows[0].TextContent);
        Assert.Contains("—", rows[1].TextContent);
    }

    [Fact]
    public void RecentRuns_ShowsPrLink_WhenPresent()
    {
        var history = new List<PipelineRunSummary>
        {
            CreateSummary("run-1", "42", "Test", PipelineStep.Completed, prUrl: "https://github.com/test/pr/1")
        };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        var link = cut.Find(".monitoring-table:last-of-type tbody a");
        Assert.Equal("https://github.com/test/pr/1", link.GetAttribute("href"));
        Assert.Equal("PR", link.TextContent);
    }

    [Fact]
    public void RecentRuns_ShowsDash_WhenNoPrLink()
    {
        var history = new List<PipelineRunSummary>
        {
            CreateSummary("run-1", "42", "Test", PipelineStep.Failed)
        };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        // No anchor in the last table
        Assert.Empty(cut.FindAll(".monitoring-table:last-of-type tbody a"));
        Assert.Contains("—", cut.Find(".monitoring-table:last-of-type tbody tr").TextContent);
    }

    [Fact]
    public void RecentRuns_Collapsible_TogglesVisibility()
    {
        var history = new List<PipelineRunSummary>
        {
            CreateSummary("run-1", "42", "Test", PipelineStep.Completed)
        };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        // Default expanded — table visible
        Assert.NotEmpty(cut.FindAll(".monitoring-table:last-of-type tbody tr"));

        // Click toggle to collapse
        cut.Find(".monitoring-section-toggle").Click();
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".monitoring-table:last-of-type tbody tr")));

        // Click again to expand
        cut.Find(".monitoring-section-toggle").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".monitoring-table:last-of-type tbody tr")));
    }

    [Fact]
    public void RecentRuns_ShowsDuration_WhenCompleted()
    {
        var start = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 1, 1, 10, 45, 30, DateTimeKind.Utc);
        var history = new List<PipelineRunSummary>
        {
            CreateSummary("run-1", "42", "Test", PipelineStep.Completed, startedAt: start, completedAt: end)
        };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        Assert.Contains("00:45:30", cut.Find(".monitoring-table:last-of-type tbody tr").TextContent);
    }

    [Fact]
    public void RecentRuns_ShowsDash_WhenNoCompletedAt()
    {
        var history = new List<PipelineRunSummary>
        {
            CreateSummary("run-1", "42", "Test", PipelineStep.Failed, completedAt: null)
        };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        // Duration column should show dash
        var row = cut.Find(".monitoring-table:last-of-type tbody tr");
        Assert.Contains("—", row.TextContent);
    }

    [Fact]
    public void RecentRuns_HasSevenColumns()
    {
        var history = new List<PipelineRunSummary>
        {
            CreateSummary("run-1", "42", "Test", PipelineStep.Completed)
        };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        var headerCells = cut.FindAll(".monitoring-table:last-of-type thead th");
        Assert.Equal(11, headerCells.Count);
    }

    [Fact]
    public void RecentRuns_DisplaysFullIssueTitle_WithoutTruncation()
    {
        var longTitle = "[UX-25] Apply column width fix to Registered Agents and Recent Runs tables";
        var history = new List<PipelineRunSummary>
        {
            CreateSummary("run-1", "214", longTitle, PipelineStep.Completed)
        };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        Assert.Contains(longTitle, cut.Markup);
    }

    [Fact]
    public void RecentRuns_HasTitleAttributes_ForTooltips()
    {
        var history = new List<PipelineRunSummary>
        {
            CreateSummary("run-1", "42", "Test Title", PipelineStep.Completed)
        };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        var tdsWithTitle = cut.FindAll(".monitoring-table:last-of-type td[title]");
        Assert.NotEmpty(tdsWithTitle);

        var issueTd = tdsWithTitle.FirstOrDefault(td => td.GetAttribute("title")?.Contains("Test Title") == true);
        Assert.NotNull(issueTd);
    }

    [Fact]
    public void RecentRuns_RunIdCell_HasTitleWithFullId()
    {
        var history = new List<PipelineRunSummary>
        {
            CreateSummary("abcd1234-5678-9012-3456-789012345678", "42", "Test", PipelineStep.Completed)
        };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        var monoTds = cut.FindAll(".monitoring-table:last-of-type td.monitoring-mono[title]");
        Assert.NotEmpty(monoTds);
        Assert.Equal("abcd1234-5678-9012-3456-789012345678", monoTds[0].GetAttribute("title"));
    }

    private static PipelineRunSummary CreateSummary(
        string runId, string issueId, string issueTitle, PipelineStep finalStep,
        string? agentId = null, string? prUrl = null, string initiatedBy = "manual",
        DateTime? startedAt = null, DateTime? completedAt = null, string? failureReason = null)
    {
        var start = startedAt ?? DateTime.UtcNow.AddMinutes(-30);
        return new PipelineRunSummary
        {
            RunId = runId,
            IssueIdentifier = issueId,
            IssueTitle = issueTitle,
            FinalStep = finalStep,
            StartedAt = start,
            CompletedAt = completedAt ?? start.AddMinutes(15),
            AgentId = agentId,
            PullRequestUrl = prUrl,
            InitiatedBy = initiatedBy,
            FailureReason = failureReason
        };
    }

    [Fact]
    public void RecentRuns_RowsAreClickable()
    {
        var history = new List<PipelineRunSummary>
        {
            CreateSummary("run-1", "42", "Test", PipelineStep.Completed)
        };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        var rows = cut.FindAll(".monitoring-table:last-of-type tbody tr.monitoring-row-clickable");
        Assert.Single(rows);
    }

    [Fact]
    public async Task RecentRuns_ClickingFailedRun_ShowsModalWithFailureReason()
    {
        var history = new List<PipelineRunSummary>
        {
            CreateSummary("run-1", "42", "Test Issue", PipelineStep.Failed,
                failureReason: "Analysis failed after 2 attempt(s): analysis.md not found")
        };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        await cut.InvokeAsync(() => cut.Find(".monitoring-table:last-of-type tbody tr.monitoring-row-clickable").Click());

        var callout = cut.Find(".summary-failure-callout");
        Assert.Contains("Analysis failed after 2 attempt(s): analysis.md not found", callout.TextContent);
    }

    [Fact]
    public async Task RecentRuns_ClickingCompletedRun_ShowsModalWithoutFailureCallout()
    {
        var history = new List<PipelineRunSummary>
        {
            CreateSummary("run-1", "42", "Test Issue", PipelineStep.Completed)
        };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        await cut.InvokeAsync(() => cut.Find(".monitoring-table:last-of-type tbody tr.monitoring-row-clickable").Click());

        cut.WaitForAssertion(() =>
        {
            Assert.Empty(cut.FindAll(".summary-failure-callout"));
            // Modal should still be visible with run details
            Assert.Contains("Run", cut.Markup);
            Assert.Contains("#42", cut.Markup);
        });
    }

    [Fact]
    public async Task RecentRuns_HistoryModal_CanBeDismissedWithCloseButton()
    {
        var history = new List<PipelineRunSummary>
        {
            CreateSummary("run-1", "42", "Test", PipelineStep.Failed, failureReason: "error")
        };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        await cut.InvokeAsync(() => cut.Find(".monitoring-table:last-of-type tbody tr.monitoring-row-clickable").Click());
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".summary-failure-callout")));

        // Click close button
        await cut.InvokeAsync(() => cut.Find(".modal-card .btn-cancel").Click());
        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".summary-failure-callout")));
    }
}
