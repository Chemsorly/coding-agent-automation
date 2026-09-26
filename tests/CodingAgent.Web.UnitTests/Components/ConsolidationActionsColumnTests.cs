using AwesomeAssertions;
using Bunit;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Interfaces;
using CodingAgent.Pipeline.Models;
using CodingAgent.AgentGateway;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// Tests for Consolidation Run History Actions column visibility (issue #2939).
/// The Actions column must only render when at least one run is cancellable.
/// </summary>
public class ConsolidationActionsColumnTests : BunitContext
{
    private readonly Mock<IConsolidationService> _mockService = new();
    private readonly Mock<IPipelineApiConfigClient> _mockConfigClient = new();
    private readonly ConsolidationBadgeService _badgeService = new();

    public ConsolidationActionsColumnTests()
    {
        _mockConfigClient.Setup(s => s.GetPipelineConfigAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PipelineConfiguration());
        _mockConfigClient.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());
        _mockConfigClient.Setup(s => s.GetAllTemplatesAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineJobTemplate>());
        _mockService.Setup(s => s.GetHarnessSuggestionsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync((HarnessSuggestions?)null);
        _mockService.Setup(s => s.GetLastRunAsync(
            It.IsAny<ConsolidationRunType>(), It.IsAny<TemplateId?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((ConsolidationRun?)null);

        Services.AddSingleton<IConsolidationService>(_mockService.Object);
        Services.AddSingleton(_mockConfigClient.Object);
        Services.AddSingleton(_badgeService);
        // Required by Consolidation.razor after issue #3027 (cancel via PostStatus)
        Services.AddSingleton(new Mock<IPipelineApiWorkItemClient>().Object);
    }

    private static ConsolidationRun MakeRun(ConsolidationRunStatus status, string id = "run-1", string? workItemId = null) => new()
    {
        RunId = id,
        Type = ConsolidationRunType.BrainConsolidation,
        StartedAtUtc = DateTime.UtcNow.AddMinutes(-5),
        Status = status,
        WorkItemId = workItemId,
    };

    [Fact]
    public void Consolidation_HidesActionsColumn_WhenNoRunIsCancellable()
    {
        _mockService.Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun>
            {
                MakeRun(ConsolidationRunStatus.Succeeded, "r1"),
                MakeRun(ConsolidationRunStatus.Failed, "r2"),
                MakeRun(ConsolidationRunStatus.Cancelled, "r3"),
            });

        var cut = Render<Consolidation>();

        var headers = cut.FindAll(".monitoring-table thead th")
            .Select(e => e.TextContent.Trim())
            .ToList();
        headers.Should().NotContain("Actions",
            "the Actions column must be hidden when no run is in a cancellable state");
    }

    [Fact]
    public void Consolidation_ShowsActionsColumn_WhenSomeRunIsPending()
    {
        _mockService.Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun>
            {
                MakeRun(ConsolidationRunStatus.Succeeded, "r1"),
                // WorkItemId must be set for the cancel button to appear (issue #3027)
                MakeRun(ConsolidationRunStatus.Pending, "r2", workItemId: Guid.NewGuid().ToString()),
            });

        var cut = Render<Consolidation>();

        var headers = cut.FindAll(".monitoring-table thead th")
            .Select(e => e.TextContent.Trim())
            .ToList();
        headers.Should().Contain("Actions",
            "the Actions column must be visible when at least one run is Pending with a WorkItemId");
    }

    [Fact]
    public void Consolidation_ShowsCancelButton_OnlyForPendingRuns()
    {
        _mockService.Setup(s => s.GetRunHistoryAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ConsolidationRun>
            {
                MakeRun(ConsolidationRunStatus.Succeeded, "r1"),
                // WorkItemId must be set for the cancel button to appear (issue #3027)
                MakeRun(ConsolidationRunStatus.Pending, "r2", workItemId: Guid.NewGuid().ToString()),
            });

        var cut = Render<Consolidation>();

        var cancelButtons = cut.FindAll(".btn-cancel-run");
        cancelButtons.Should().HaveCount(1, "only the Pending run with a WorkItemId must have a Cancel button");
    }
}
