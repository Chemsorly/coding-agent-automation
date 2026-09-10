using AwesomeAssertions;
using Bunit;
using CodingAgent.Api.Client;
using CodingAgent.Pipeline.Models;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// Verifies the rendered content of the Overview page stat cards.
/// </summary>
public class OverviewComponentTests : BunitContext
{
    private static PagedResult<PipelineRunSummary> EmptyHistory() => new()
    {
        Items = new List<PipelineRunSummary>(),
        Page = 1,
        PageSize = 100,
        HasMore = false
    };

    private static AgentEntryDto MakeConnectedAgent(string id = "agent-1") => new()
    {
        AgentId = new AgentId(id),
        ConnectionId = $"conn-{id}",
        Hostname = $"host-{id}",
        Labels = new List<string>(),
        Status = AgentStatus.Idle,
        RegisteredAt = DateTimeOffset.UtcNow
    };

    /// <summary>
    /// Registers the full set of services that Overview.razor requires so that
    /// <see cref="Render{Overview}"/> does not throw a DI exception.
    /// </summary>
    private void RegisterOverviewServices(
        Mock<IPipelineApiAgentClient> mockAgents,
        Mock<IPipelineApiRunHistoryClient>? mockRunHistory = null,
        Mock<IPipelineApiWorkItemClient>? mockWorkItems = null)
    {
        mockRunHistory ??= BuildEmptyRunHistoryMock();
        mockWorkItems ??= BuildEmptyWorkItemsMock();

        Services.AddSingleton(mockRunHistory.Object);
        Services.AddSingleton(mockAgents.Object);
        Services.AddSingleton(mockWorkItems.Object);
        Services.AddSingleton<ILoopStatusService>(Mock.Of<ILoopStatusService>());
        Services.AddSingleton(new CockpitState());
    }

    private static Mock<IPipelineApiRunHistoryClient> BuildEmptyRunHistoryMock()
    {
        var mock = new Mock<IPipelineApiRunHistoryClient>();
        mock.Setup(c => c.GetRunHistoryAsync(
                It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool>(), It.IsAny<bool>(),
                It.IsAny<PipelineStep?>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(EmptyHistory());
        return mock;
    }

    private static Mock<IPipelineApiWorkItemClient> BuildEmptyWorkItemsMock()
    {
        var mock = new Mock<IPipelineApiWorkItemClient>();
        mock.Setup(c => c.GetPendingAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<PendingWorkItemDto>());
        mock.Setup(c => c.GetActiveAsync(It.IsAny<int>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<ActiveWorkItemDto>());
        return mock;
    }

    // ── Agents stat card ───────────────────────────────────────────────────

    /// <summary>
    /// Regression guard for issue #2448: the Agents stat card must show only the connected
    /// agent count with no "/" denominator. Previously the card rendered "1/1" for an
    /// ephemeral agent because _agentsTotal (always equal to _agentsOnline) was appended.
    /// </summary>
    [Fact]
    public void AgentsStatCard_WhenOneAgentConnected_ShowsOnlyOnlineCountWithoutDenominator()
    {
        var mockAgents = new Mock<IPipelineApiAgentClient>();
        mockAgents.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentEntryDto> { MakeConnectedAgent() });

        RegisterOverviewServices(mockAgents);

        var cut = Render<Overview>();

        // Find the Agents stat card value span
        var statCards = cut.FindAll(".cockpit-stat");
        var agentsCard = statCards.FirstOrDefault(card =>
        {
            var label = card.QuerySelector(".cockpit-stat-l");
            return label?.TextContent.Trim() == "Agents";
        });

        agentsCard.Should().NotBeNull("the Agents stat card must be present in the Overview stat strip");

        var valueSpan = agentsCard!.QuerySelector(".cockpit-stat-v");
        valueSpan.Should().NotBeNull("the Agents stat card must have a .cockpit-stat-v element");

        var valueText = valueSpan!.TextContent.Trim();

        // The card must show "1" — the connected count — and must not contain a "/" denominator
        valueText.Should().Be("1", "the Agents stat card must show only the online count");
        valueText.Should().NotContain("/", "the Agents stat card must not show a denominator");
    }

    // TODO: Add a test verifying that a Disconnected agent is excluded from the displayed count.
    // Scenario: one AgentStatus.Idle agent + one AgentStatus.Disconnected agent → card must show "1".
    // Without this test, reverting _agentsOnline counting logic back to agents.Count would leave
    // both existing tests green, so the behavioral contract of the fix is not fully guarded.

    /// <summary>
    /// With zero agents registered, the card should show "0" (not "0/0").
    /// </summary>
    [Fact]
    public void AgentsStatCard_WhenNoAgentsRegistered_ShowsZeroWithoutDenominator()
    {
        var mockAgents = new Mock<IPipelineApiAgentClient>();
        mockAgents.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentEntryDto>());

        RegisterOverviewServices(mockAgents);

        var cut = Render<Overview>();

        var statCards = cut.FindAll(".cockpit-stat");
        var agentsCard = statCards.FirstOrDefault(card =>
        {
            var label = card.QuerySelector(".cockpit-stat-l");
            return label?.TextContent.Trim() == "Agents";
        });

        agentsCard.Should().NotBeNull("the Agents stat card must be present");

        var valueSpan = agentsCard!.QuerySelector(".cockpit-stat-v");
        var valueText = valueSpan!.TextContent.Trim();

        valueText.Should().Be("0", "with no agents the card must show '0'");
        valueText.Should().NotContain("/", "the Agents stat card must not show a denominator");
    }
}
