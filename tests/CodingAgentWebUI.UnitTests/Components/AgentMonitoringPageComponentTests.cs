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
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;
using Moq;
using Serilog;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for the Recent Runs section on the Agent Monitoring page.
/// </summary>
public class AgentMonitoringPageComponentTests : BunitContext
{
    private readonly Mock<IPipelineRunHistoryService> _mockHistoryService = new();
    private readonly Mock<IConfigurationStore> _mockStore = new();

    private void RegisterDefaults(IReadOnlyList<PipelineRunSummary>? history = null)
    {
        _mockHistoryService.Setup(h => h.GetRunHistory())
            .Returns(history ?? Array.Empty<PipelineRunSummary>());

        _mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        _mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());

        var mockLogger = new Mock<ILogger>();
        var mockFactory = new Mock<IProviderFactory>();
        var mockValidator = new Mock<IQualityGateValidator>();

        var pipelineService = new PipelineOrchestrationService(
            _mockStore.Object, mockFactory.Object, new IssueDescriptionParser(),
            new AgentPhaseExecutor(mockLogger.Object),
            new QualityGateExecutor(mockValidator.Object, new PullRequestOrchestrator(mockLogger.Object), new CiLogWriter(mockLogger.Object), new FeedbackService(mockLogger.Object), mockLogger.Object),
            mockLogger.Object,
            brainUpdateService: new Mock<IBrainUpdateService>().Object,
            historyService: _mockHistoryService.Object);

        var registry = new AgentRegistryService(mockLogger.Object);

        Services.AddSingleton(pipelineService);
        Services.AddSingleton(registry);
        Services.AddSingleton(new JobDispatcherService(registry, mockLogger.Object));
        Services.AddSingleton(new OrchestratorRunService(mockLogger.Object));
        Services.AddSingleton(_mockStore.Object);
        Services.AddSingleton(_mockHistoryService.Object);
        Services.AddSingleton(new Mock<IHubContext<AgentHub, IAgentHubClient>>().Object);
        Services.AddSingleton(new Mock<IJSRuntime>().Object);
        Services.AddSingleton(new ConsolidationQueueService(mockLogger.Object));
        Services.AddSingleton(Mock.Of<IConsolidationService>(s =>
            s.GetRunHistoryAsync(It.IsAny<CancellationToken>()) == Task.FromResult<IReadOnlyList<ConsolidationRun>>(Array.Empty<ConsolidationRun>())));
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
        Assert.Contains("local", rows[1].TextContent);
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
