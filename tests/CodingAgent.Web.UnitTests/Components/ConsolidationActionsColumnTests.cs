using AwesomeAssertions;
using Bunit;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.AgentGateway;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// Tests for Consolidation Run History Actions column visibility (issue #2939).
/// After issue #3028, the page reads from IPipelineApiRunHistoryClient (not IConsolidationService).
/// A run is cancellable when WorkItemId.HasValue &amp;&amp; CompletedAtOffset == null.
/// </summary>
public class ConsolidationActionsColumnTests : BunitContext
{
    private readonly Mock<IConsolidationService> _mockService = new(MockBehavior.Strict);
    private readonly Mock<IPipelineApiConfigClient> _mockConfigClient = new();
    private readonly Mock<IPipelineApiRunHistoryClient> _mockRunHistoryClient = new();
    private readonly Mock<IAgentHubConnection> _mockHubConnection = new();
    private readonly ConsolidationBadgeService _badgeService = new();

    public ConsolidationActionsColumnTests()
    {
        _mockConfigClient.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockConfigClient.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());
        _mockConfigClient.Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>());

        // Strict mock: only GetHarnessSuggestionsAsync is allowed (GetRunHistoryAsync and GetLastRunAsync must not be called)
        _mockService.Setup(s => s.GetHarnessSuggestionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessSuggestions?)null);

        // Hub mock: return a no-op disposable for event subscription
        _mockHubConnection
            .Setup(h => h.On<string, JobCompletionPayload>(It.IsAny<string>(), It.IsAny<Action<string, JobCompletionPayload>>()))
            .Returns(Mock.Of<IDisposable>());
        _mockHubConnection.Setup(h => h.DisposeAsync()).Returns(ValueTask.CompletedTask);

        // Default run history: empty
        _mockRunHistoryClient
            .Setup(s => s.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(),
                It.IsAny<PipelineRunType?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = Array.Empty<PipelineRunSummary>().ToList(), Page = 1, PageSize = 200, HasMore = false
            });

        Services.AddSingleton<IConsolidationService>(_mockService.Object);
        Services.AddSingleton(_mockConfigClient.Object);
        Services.AddSingleton<IPipelineApiRunHistoryClient>(_mockRunHistoryClient.Object);
        Services.AddSingleton<IAgentHubConnection>(_mockHubConnection.Object);
        Services.AddSingleton(_badgeService);
        Services.AddSingleton(new Mock<IPipelineApiWorkItemClient>().Object);
    }

    /// <summary>
    /// Creates a terminal (completed) PipelineRunSummary — not cancellable.
    /// </summary>
    private static PipelineRunSummary MakeTerminalRun(PipelineStep finalStep, string? runId = null) => new()
    {
        RunId = runId ?? Guid.NewGuid().ToString(),
        IssueIdentifier = "BrainConsolidation:t1",
        IssueTitle = "BrainConsolidation",
        RunType = PipelineRunType.Consolidation,
        ConsolidationType = ConsolidationRunType.BrainConsolidation,
        ConsolidationTemplateId = "t1",
        FinalStep = finalStep,
        StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-10),
        CompletedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-1),
        InitiatedBy = ConsolidationConstants.InitiatedBy
    };

    /// <summary>
    /// Creates an active (non-terminal) PipelineRunSummary with a WorkItemId — cancellable.
    /// </summary>
    private static PipelineRunSummary MakeActiveRun(Guid? workItemId, string? runId = null) => new()
    {
        RunId = runId ?? Guid.NewGuid().ToString(),
        IssueIdentifier = "BrainConsolidation:t1",
        IssueTitle = "BrainConsolidation",
        RunType = PipelineRunType.Consolidation,
        ConsolidationType = ConsolidationRunType.BrainConsolidation,
        ConsolidationTemplateId = "t1",
        FinalStep = PipelineStep.Created,
        StartedAtOffset = DateTimeOffset.UtcNow.AddMinutes(-2),
        CompletedAtOffset = null,  // null = active/in-flight
        WorkItemId = workItemId,
        InitiatedBy = ConsolidationConstants.InitiatedBy
    };

    private void SetupRunHistory(IReadOnlyList<PipelineRunSummary> runs)
    {
        _mockRunHistoryClient
            .Setup(s => s.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<DateTimeOffset?>(),
                It.IsAny<PipelineRunType?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<PipelineRunSummary>
            {
                Items = runs.ToList(), Page = 1, PageSize = 200, HasMore = false
            });
    }

    [Fact]
    public void Consolidation_HidesActionsColumn_WhenNoRunIsCancellable()
    {
        SetupRunHistory(new List<PipelineRunSummary>
        {
            MakeTerminalRun(PipelineStep.Completed),
            MakeTerminalRun(PipelineStep.Failed),
            MakeTerminalRun(PipelineStep.Cancelled),
        });

        var cut = Render<Consolidation>();

        var headers = cut.FindAll(".monitoring-table thead th")
            .Select(e => e.TextContent.Trim())
            .ToList();
        headers.Should().NotContain("Actions",
            "the Actions column must be hidden when no run is in a cancellable state");
    }

    [Fact]
    public void Consolidation_ShowsActionsColumn_WhenSomeRunIsActiveWithWorkItemId()
    {
        // A run is cancellable when CompletedAtOffset == null AND WorkItemId.HasValue
        SetupRunHistory(new List<PipelineRunSummary>
        {
            MakeTerminalRun(PipelineStep.Completed),
            MakeActiveRun(workItemId: Guid.NewGuid()),
        });

        var cut = Render<Consolidation>();

        var headers = cut.FindAll(".monitoring-table thead th")
            .Select(e => e.TextContent.Trim())
            .ToList();
        headers.Should().Contain("Actions",
            "the Actions column must be visible when at least one active run has a WorkItemId");
    }

    [Fact]
    public void Consolidation_HidesActionsColumn_WhenActiveRunHasNoWorkItemId()
    {
        // Active but no WorkItemId — not cancellable
        SetupRunHistory(new List<PipelineRunSummary>
        {
            MakeActiveRun(workItemId: null),
        });

        var cut = Render<Consolidation>();

        var headers = cut.FindAll(".monitoring-table thead th")
            .Select(e => e.TextContent.Trim())
            .ToList();
        headers.Should().NotContain("Actions",
            "the Actions column must be hidden when active runs have no WorkItemId");
    }

    [Fact]
    public void Consolidation_ShowsCancelButton_OnlyForActiveRunsWithWorkItemId()
    {
        SetupRunHistory(new List<PipelineRunSummary>
        {
            MakeTerminalRun(PipelineStep.Completed),
            MakeActiveRun(workItemId: Guid.NewGuid()),
        });

        var cut = Render<Consolidation>();

        var cancelButtons = cut.FindAll(".btn-cancel-run");
        cancelButtons.Should().HaveCount(1, "only the active run with a WorkItemId must have a Cancel button");
    }
}
