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
/// bUnit component tests for the Feedback section in the run detail modal.
/// Validates Requirements 5.1, 5.2, 5.3, 5.4.
/// </summary>
public class FeedbackSectionComponentTests : BunitContext
{
    private void RegisterDefaults(IReadOnlyList<PipelineRunSummary>? history = null)
    {
        // Spec 045: IPipelineApiConfigClient replaces IConfigurationStore
        var mockConfigClient = new Mock<IPipelineApiConfigClient>();
        mockConfigClient.Setup(c => c.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        mockConfigClient.Setup(c => c.GetAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        mockConfigClient.Setup(c => c.GetQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        mockConfigClient.Setup(c => c.GetProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());

        var mockLogger = new Mock<ILogger>();
        var mockFactory = new Mock<IProviderFactory>();
        var mockValidator = new Mock<IQualityGateValidator>();

        var registry = new AgentRegistryService(mockLogger.Object);

        Services.AddSingleton(registry);
        Services.AddSingleton<IAgentRegistryService>(registry);
        Services.AddSingleton(new JobDeduplicationGuardService(registry, mockLogger.Object));
        Services.AddSingleton<IPipelineApiConfigClient>(mockConfigClient.Object);
        // IConfigurationStore is still needed by HistoryRunDetailModal (not yet migrated to API client).
        var mockStore = new Mock<IConfigurationStore>();
        mockStore.Setup(s => s.LoadPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        mockStore.Setup(s => s.LoadAgentProfilesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<AgentProfile>());
        mockStore.Setup(s => s.LoadQualityGateConfigsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<QualityGateConfiguration>());
        mockStore.Setup(s => s.LoadProviderConfigsAsync(It.IsAny<ProviderKind>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<ProviderConfig>());
        Services.AddSingleton<IConfigurationStore>(mockStore.Object);
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

    private static PipelineRunSummary CreateSummaryWithFeedback(RunFeedback? feedback)
    {
        return new PipelineRunSummary
        {
            RunId = "test-run-1",
            IssueIdentifier = "99",
            IssueTitle = "Test Issue",
            FinalStep = PipelineStep.Completed,
            StartedAt = DateTime.UtcNow.AddMinutes(-30),
            CompletedAt = DateTime.UtcNow,
            Feedback = feedback
        };
    }

    private static RunFeedback CreateFullFeedback()
    {
        return new RunFeedback
        {
            Outcome = FeedbackOutcome.Failure,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback
            {
                Category = "mcp tool timeout",
                StuckReason = "The MCP server was unreachable after 3 retries",
                MissingContext = ["src/Config.cs", "docs/setup.md"],
                MissingCapabilities = ["database access", "network diagnostics"],
                PromptIssues = ["contradictory instructions about error handling"],
                Suggestions = ["add retry logic to MCP calls", "provide fallback config"]
            },
            Issue = new IssueFeedback
            {
                Category = "missing component",
                Description = "The referenced UserService class does not exist in the repository",
                AffectedFiles = ["src/Services/UserService.cs", "src/Controllers/UserController.cs"],
                HumanActionNeeded = "Create the UserService class or update the issue to reference the correct service"
            }
        };
    }

    /// <summary>
    /// Requirement 5.1: Feedback section renders when Feedback is non-null.
    /// </summary>
    [Fact]
    public void FeedbackSection_Renders_WhenFeedbackIsNonNull()
    {
        var history = new List<PipelineRunSummary> { CreateSummaryWithFeedback(CreateFullFeedback()) };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        // Click the row to open the history modal
        cut.Find(".monitoring-table:last-of-type tbody tr.monitoring-row-clickable").Click();

        cut.WaitForAssertion(() =>
        {
            var feedbackSections = cut.FindAll(".feedback-section");
            Assert.NotEmpty(feedbackSections);
        });
    }

    /// <summary>
    /// Requirement 5.4: Feedback section hidden when Feedback is null.
    /// </summary>
    [Fact]
    public void FeedbackSection_Hidden_WhenFeedbackIsNull()
    {
        var history = new List<PipelineRunSummary> { CreateSummaryWithFeedback(null) };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        // Click the row to open the history modal
        cut.Find(".monitoring-table:last-of-type tbody tr.monitoring-row-clickable").Click();

        var feedbackSections = cut.FindAll(".feedback-section");
        Assert.Empty(feedbackSections);
    }

    /// <summary>
    /// Requirement 5.2: Harness feedback fields display correctly — Category badge.
    /// </summary>
    [Fact]
    public void HarnessFeedback_DisplaysCategoryBadge()
    {
        var history = new List<PipelineRunSummary> { CreateSummaryWithFeedback(CreateFullFeedback()) };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        cut.Find(".monitoring-table:last-of-type tbody tr.monitoring-row-clickable").Click();

        var badge = cut.Find(".feedback-category-badge");
        Assert.Contains("mcp tool timeout", badge.TextContent);
    }

    /// <summary>
    /// Requirement 5.2: Harness feedback fields display correctly — StuckReason.
    /// </summary>
    [Fact]
    public void HarnessFeedback_DisplaysStuckReason()
    {
        var history = new List<PipelineRunSummary> { CreateSummaryWithFeedback(CreateFullFeedback()) };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        cut.Find(".monitoring-table:last-of-type tbody tr.monitoring-row-clickable").Click();

        var stuckReason = cut.Find(".feedback-stuck-reason");
        Assert.Contains("The MCP server was unreachable after 3 retries", stuckReason.TextContent);
    }

    /// <summary>
    /// Requirement 5.2: List fields render as bullet points (ul/li elements).
    /// </summary>
    [Fact]
    public void HarnessFeedback_ListsRenderAsBulletPoints()
    {
        var history = new List<PipelineRunSummary> { CreateSummaryWithFeedback(CreateFullFeedback()) };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        cut.Find(".monitoring-table:last-of-type tbody tr.monitoring-row-clickable").Click();

        cut.WaitForAssertion(() =>
        {
            var listSections = cut.FindAll(".feedback-list-section");
            Assert.True(listSections.Count >= 4, "Expected at least 4 list sections (MissingContext, MissingCapabilities, PromptIssues, Suggestions)");

            // Verify each list section contains ul > li elements
            foreach (var section in listSections)
            {
                var listItems = section.QuerySelectorAll("ul li");
                Assert.NotEmpty(listItems);
            }
        });
    }

    /// <summary>
    /// Requirement 5.2: MissingContext items display correctly.
    /// </summary>
    [Fact]
    public void HarnessFeedback_DisplaysMissingContextItems()
    {
        var history = new List<PipelineRunSummary> { CreateSummaryWithFeedback(CreateFullFeedback()) };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        cut.Find(".monitoring-table:last-of-type tbody tr.monitoring-row-clickable").Click();

        var markup = cut.Markup;
        Assert.Contains("src/Config.cs", markup);
        Assert.Contains("docs/setup.md", markup);
    }

    /// <summary>
    /// Requirement 5.2: MissingCapabilities items display correctly.
    /// </summary>
    [Fact]
    public void HarnessFeedback_DisplaysMissingCapabilitiesItems()
    {
        var history = new List<PipelineRunSummary> { CreateSummaryWithFeedback(CreateFullFeedback()) };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        cut.Find(".monitoring-table:last-of-type tbody tr.monitoring-row-clickable").Click();

        var markup = cut.Markup;
        Assert.Contains("database access", markup);
        Assert.Contains("network diagnostics", markup);
    }

    /// <summary>
    /// Requirement 5.3: Issue feedback section hidden when Issue is null.
    /// </summary>
    [Fact]
    public void IssueFeedback_Hidden_WhenIssueIsNull()
    {
        var feedbackWithoutIssue = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback
            {
                Category = "slow build",
                Suggestions = ["cache dependencies"]
            },
            Issue = null
        };
        var history = new List<PipelineRunSummary> { CreateSummaryWithFeedback(feedbackWithoutIssue) };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        cut.Find(".monitoring-table:last-of-type tbody tr.monitoring-row-clickable").Click();

        cut.WaitForAssertion(() =>
        {
            // Feedback section should exist
            Assert.NotEmpty(cut.FindAll(".feedback-section"));

            // But only one subsection (Harness), not two
            var subsections = cut.FindAll(".feedback-subsection");
            Assert.Single(subsections);
            Assert.Contains("Harness Feedback", subsections[0].TextContent);
        });
    }

    /// <summary>
    /// Requirement 5.3: Issue feedback displays Description.
    /// </summary>
    [Fact]
    public void IssueFeedback_DisplaysDescription()
    {
        var history = new List<PipelineRunSummary> { CreateSummaryWithFeedback(CreateFullFeedback()) };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        cut.Find(".monitoring-table:last-of-type tbody tr.monitoring-row-clickable").Click();

        var description = cut.Find(".feedback-description");
        Assert.Contains("The referenced UserService class does not exist", description.TextContent);
    }

    /// <summary>
    /// Requirement 5.3: Issue feedback displays AffectedFiles as a list.
    /// </summary>
    [Fact]
    public void IssueFeedback_DisplaysAffectedFilesAsList()
    {
        var history = new List<PipelineRunSummary> { CreateSummaryWithFeedback(CreateFullFeedback()) };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        cut.Find(".monitoring-table:last-of-type tbody tr.monitoring-row-clickable").Click();

        var markup = cut.Markup;
        Assert.Contains("src/Services/UserService.cs", markup);
        Assert.Contains("src/Controllers/UserController.cs", markup);
    }

    /// <summary>
    /// Requirement 5.3: Issue feedback displays HumanActionNeeded.
    /// </summary>
    [Fact]
    public void IssueFeedback_DisplaysHumanActionNeeded()
    {
        var history = new List<PipelineRunSummary> { CreateSummaryWithFeedback(CreateFullFeedback()) };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        cut.Find(".monitoring-table:last-of-type tbody tr.monitoring-row-clickable").Click();

        var actionNeeded = cut.Find(".feedback-action-needed");
        Assert.Contains("Create the UserService class or update the issue", actionNeeded.TextContent);
    }

    /// <summary>
    /// Requirement 5.3: Issue feedback displays Category badge.
    /// </summary>
    [Fact]
    public void IssueFeedback_DisplaysCategoryBadge()
    {
        var history = new List<PipelineRunSummary> { CreateSummaryWithFeedback(CreateFullFeedback()) };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        cut.InvokeAsync(() => cut.Find(".monitoring-table:last-of-type tbody tr.monitoring-row-clickable").Click());

        cut.WaitForAssertion(() =>
        {
            var badges = cut.FindAll(".feedback-category-badge");
            // Should have two badges: one for harness, one for issue
            Assert.Equal(2, badges.Count);
            Assert.Contains("missing component", badges[1].TextContent);
        });
    }

    /// <summary>
    /// Requirement 5.2: Harness feedback with empty lists does not render list sections.
    /// </summary>
    [Fact]
    public void HarnessFeedback_EmptyLists_DoNotRenderListSections()
    {
        var minimalFeedback = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback
            {
                Category = "clean run"
                // All lists default to empty
            },
            Issue = null
        };
        var history = new List<PipelineRunSummary> { CreateSummaryWithFeedback(minimalFeedback) };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        cut.Find(".monitoring-table:last-of-type tbody tr.monitoring-row-clickable").Click();

        cut.WaitForAssertion(() =>
        {
            // Feedback section exists
            Assert.NotEmpty(cut.FindAll(".feedback-section"));

            // No list sections rendered (all lists are empty)
            var listSections = cut.FindAll(".feedback-list-section");
            Assert.Empty(listSections);
        });
    }

    /// <summary>
    /// Requirement 5.2: Harness feedback without StuckReason does not render stuck reason div.
    /// </summary>
    [Fact]
    public void HarnessFeedback_NoStuckReason_DoesNotRenderStuckReasonDiv()
    {
        var feedbackNoStuck = new RunFeedback
        {
            Outcome = FeedbackOutcome.Success,
            CollectedAtUtc = DateTime.UtcNow,
            Harness = new HarnessFeedback
            {
                Category = "clean run",
                StuckReason = null
            },
            Issue = null
        };
        var history = new List<PipelineRunSummary> { CreateSummaryWithFeedback(feedbackNoStuck) };
        RegisterDefaults(history);
        var cut = Render<AgentMonitoring>();

        cut.Find(".monitoring-table:last-of-type tbody tr.monitoring-row-clickable").Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".feedback-stuck-reason")));
    }
}
