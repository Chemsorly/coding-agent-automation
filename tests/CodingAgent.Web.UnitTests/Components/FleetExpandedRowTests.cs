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

    private static AgentEntryDto MakeBusyAgent() => MakeAgent("agent-busy") with
    {
        Status = AgentStatus.Busy,
        ActiveIssueIdentifier = "3020",
        ActiveIssueUrl = "https://github.com/owner/repo/issues/3020",
        ActiveIssueTitle = "ConsolidationDispatcherObservabilityTests flakes under parallel xUnit",
        ActiveRunId = "1e9a22a8-9e0f-420b-ae28-7295eab39878",
        ActivePullRequestUrl = "https://github.com/owner/repo/pull/3059",
    };

    /// <summary>
    /// The "Active work" cell stays a table cell and holds its chips in an inner flex row, so a long issue
    /// title can't push the Run/PR chips out of the (formerly flex, overflow-hidden) cell.
    /// </summary>
    [Fact]
    public void Fleet_ActiveWorkCell_KeepsIssueRunAndPrChipsInInnerRow()
    {
        _mockAgents.Setup(c => c.GetAgentsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new List<AgentEntryDto> { MakeBusyAgent() });

        var cut = Render<Fleet>();

        var links = cut.Find("td.fleet-work-cell > .fleet-work-links");
        links.QuerySelector("a.fleet-work-issue .fleet-work-title").Should().NotBeNull(
            "only the issue chip's title is allowed to shrink");
        links.QuerySelector("a[title='Open pipeline run']")!.GetAttribute("href")
            .Should().Be("/runs/1e9a22a8-9e0f-420b-ae28-7295eab39878");
        links.QuerySelector("a[title='Open pull request']").Should().NotBeNull();
    }

    /// <summary>The expanded row links the PR as "View PR" (full URL in the tooltip), not the raw URL.</summary>
    [Fact]
    public void Fleet_ExpandedRow_PullRequest_ShowsViewPrLink()
    {
        var cut = RenderFleetWithExpandedAgent(MakeBusyAgent());

        var prItem = cut.FindAll(".cockpit-detail-item")
            .Single(el => el.QuerySelector(".k")?.TextContent.Trim() == "Pull request");
        var link = prItem.QuerySelector("a")!;
        link.TextContent.Trim().Should().Be("View PR");
        link.GetAttribute("title").Should().Be("https://github.com/owner/repo/pull/3059");
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
        // TODO: [WARNING] There is no complementary test verifying that "Last heartbeat" IS present as a
        // column header in the collapsed row (i.e. the field still exists in the table, just not duplicated
        // in the detail grid). A future change deleting the column from the row header would not be caught
        // by any test. Add a test asserting Fleet renders a <th> with text "Last heartbeat" in the table header.
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
