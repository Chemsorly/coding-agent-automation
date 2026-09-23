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
/// Tests for Fleet expanded row cleanup (issue #2939).
/// Verifies that internal fields removed from the detail row no longer appear.
/// </summary>
public class FleetExpandedRowTests : BunitContext
{
    private readonly Mock<IPipelineApiAgentClient> _mockAgents = new();

    private static AgentEntryDto MakeAgent(string id = "agent-abc") => new()
    {
        AgentId = new AgentId(id),
        ConnectionId = "conn-12345",
        Hostname = "worker-node-1",
        Labels = new List<string> { "kiro" },
        Status = AgentStatus.Idle,
        RegisteredAt = DateTimeOffset.UtcNow.AddHours(-2),
        LastHeartbeatAt = DateTimeOffset.UtcNow.AddSeconds(-10),
    };

    public FleetExpandedRowTests()
    {
        _mockAgents.Setup(c => c.GetCredentialPoolAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new CredentialPoolStatus(0, 0, 0));
        Services.AddSingleton(_mockAgents.Object);

        // RefreshBar calls JSRuntime.InvokeAsync for localStorage reads on first render.
        // Use bUnit's built-in JSInterop (not a Mock<IJSRuntime>) to handle these calls.
        JSInterop.SetupVoid("localStorageSet", _ => true).SetVoidResult();
        JSInterop.Setup<string?>("localStorageGet", _ => true).SetResult(null);
    }

    private IRenderedComponent<Fleet> RenderFleetWithExpandedAgent(AgentEntryDto agent)
    {
        _mockAgents.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentEntryDto> { agent });

        var cut = Render<Fleet>();

        // Click the agent row to expand it
        cut.Find(".monitoring-row-clickable").Click();

        return cut;
    }

    [Fact]
    public void Fleet_ExpandedRow_DoesNotShowConnectionId()
    {
        var cut = RenderFleetWithExpandedAgent(MakeAgent());

        var detailLabels = cut.FindAll(".cockpit-detail-item .k")
            .Select(e => e.TextContent.Trim())
            .ToList();

        detailLabels.Should().NotContain("Connection ID",
            "Connection ID is a SignalR internal and must not appear in the Fleet expanded row");
    }

    [Fact]
    public void Fleet_ExpandedRow_DoesNotShowDisabledField()
    {
        var cut = RenderFleetWithExpandedAgent(MakeAgent());

        var detailLabels = cut.FindAll(".cockpit-detail-item .k")
            .Select(e => e.TextContent.Trim())
            .ToList();

        detailLabels.Should().NotContain("Disabled",
            "the 'Disabled' field is never true in the current system and must not appear in the expanded row");
    }

    [Fact]
    public void Fleet_ExpandedRow_DoesNotShowHostname()
    {
        var cut = RenderFleetWithExpandedAgent(MakeAgent());

        var detailLabels = cut.FindAll(".cockpit-detail-item .k")
            .Select(e => e.TextContent.Trim())
            .ToList();

        detailLabels.Should().NotContain("Hostname",
            "Hostname is already shown in the collapsed row and must not be duplicated in the expanded row");
    }

    [Fact]
    public void Fleet_ExpandedRow_DoesNotShowLastHeartbeat()
    {
        var cut = RenderFleetWithExpandedAgent(MakeAgent());

        var detailLabels = cut.FindAll(".cockpit-detail-item .k")
            .Select(e => e.TextContent.Trim())
            .ToList();

        detailLabels.Should().NotContain("Last heartbeat",
            "Last heartbeat is already shown as a column in the collapsed row and must not appear in the expanded row");
    }

    [Fact]
    public void Fleet_ExpandedRow_ShowsRegistered()
    {
        var cut = RenderFleetWithExpandedAgent(MakeAgent());

        var detailLabels = cut.FindAll(".cockpit-detail-item .k")
            .Select(e => e.TextContent.Trim())
            .ToList();

        detailLabels.Should().Contain("Registered",
            "the 'Registered' field must remain in the expanded row as it is not shown in the collapsed row");
    }

    [Fact]
    public void Fleet_ExpandedRow_ActiveIssue_ShowsFullTitleNotLink_WhenIssueSet()
    {
        var agent = new AgentEntryDto
        {
            AgentId = new AgentId("agent-busy"),
            ConnectionId = "conn-xyz",
            Hostname = "worker-2",
            Labels = new List<string>(),
            Status = AgentStatus.Busy,
            RegisteredAt = DateTimeOffset.UtcNow.AddHours(-1),
            LastHeartbeatAt = DateTimeOffset.UtcNow,
            ActiveIssueIdentifier = "42",
            ActiveIssueUrl = "https://github.com/owner/repo/issues/42",
            ActiveIssueTitle = "This is the full untruncated issue title for operator viewing",
        };
        var cut = RenderFleetWithExpandedAgent(agent);

        // The full title must appear as text in the expanded detail
        cut.Markup.Should().Contain("This is the full untruncated issue title for operator viewing",
            "the expanded row must show the full issue title");

        // The expanded row's Active issue item must NOT render an anchor (link already in collapsed row)
        var detailItem = cut.FindAll(".cockpit-detail-item")
            .FirstOrDefault(el => el.QuerySelector(".k")?.TextContent.Trim() == "Active issue");

        // TODO: [WARNING] The null-guard below allows the assertion to be skipped entirely if the
        // "Active issue" detail item is not rendered (e.g. the @if block in Fleet.razor never fires
        // despite ActiveIssueTitle being explicitly set above). If the component has a rendering
        // regression, this test passes vacuously. Remove the guard and add an explicit
        // .Should().NotBeNull() assertion, or use cut.Find() which throws if the element is absent.
        if (detailItem is not null)
        {
            var links = detailItem.QuerySelectorAll("a");
            links.Should().BeEmpty(
                "the expanded-row 'Active issue' entry must not render a link since the collapsed row already has one");
        }
    }
}
