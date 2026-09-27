using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using CodingAgent.Api.Client;
using CodingAgent.Web.Components.Pages;
using CodingAgent.Pipeline.Models;
using Moq;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit component tests for the SettingsTreeNav component.
/// Covers rendering of tree groups, expand/collapse behavior, and accessibility attributes.
/// Tree nodes are now NavLink elements (rendered as &lt;a&gt;) and group headers are &lt;button&gt; elements.
/// </summary>
public class SettingsTreeNavComponentTests : BunitContext
{
    public SettingsTreeNavComponentTests()
    {
        var mockConfigClient = new Mock<IPipelineApiConfigClient>();
        mockConfigClient.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(Array.Empty<PipelineProject>());
        Services.AddSingleton(mockConfigClient.Object);
    }

    [Fact]
    public void TreeNav_RendersAllGroups()
    {
        var cut = Render<SettingsTreeNav>();

        Assert.Contains("Providers", cut.Markup);
        Assert.Contains("Projects", cut.Markup);
        Assert.Contains("Global Defaults", cut.Markup);
        Assert.Contains("Label Routing", cut.Markup);
    }

    [Fact]
    public void TreeNav_RendersProviderNodes()
    {
        var cut = Render<SettingsTreeNav>();

        Assert.Contains("Issue", cut.Markup);
        Assert.Contains("Repository", cut.Markup);
        Assert.Contains("Agent", cut.Markup);
    }

    [Fact]
    public void TreeNav_RendersPipelineNodes()
    {
        var cut = Render<SettingsTreeNav>();

        Assert.Contains("General", cut.Markup);
        Assert.Contains("Pipeline Loop", cut.Markup);
        Assert.Contains("Implementation", cut.Markup);
        Assert.Contains("Review", cut.Markup);
        Assert.DoesNotContain("Quality Gates", cut.Markup);
        Assert.DoesNotContain("Security", cut.Markup);
    }

    [Fact]
    public void TreeNav_RendersLabelRoutingNodes()
    {
        var cut = Render<SettingsTreeNav>();

        Assert.Contains("Agent Profiles", cut.Markup);
        Assert.Contains("Quality Gate Configs", cut.Markup);
        Assert.Contains("Reviewer Configs", cut.Markup);
    }

    [Fact]
    public void TreeNav_GroupHeaders_AreButtons()
    {
        var cut = Render<SettingsTreeNav>();

        var headers = cut.FindAll("button.tree-group-header");
        Assert.Equal(5, headers.Count);
    }

    [Fact]
    public void TreeNav_GroupHeaders_HaveAriaExpanded()
    {
        var cut = Render<SettingsTreeNav>();

        var headers = cut.FindAll("button.tree-group-header");
        Assert.All(headers, h => Assert.NotNull(h.GetAttribute("aria-expanded")));
    }

    [Fact]
    public void TreeNav_GroupHeaders_ExpandedByDefault_AriaExpandedTrue()
    {
        var cut = Render<SettingsTreeNav>();

        var headers = cut.FindAll("button.tree-group-header");
        Assert.All(headers, h => Assert.Equal("true", h.GetAttribute("aria-expanded")));
    }

    [Fact]
    public void TreeNav_TreeNodes_AreAnchorElements()
    {
        // NavLink renders as <a> — verify tree nodes are links, not divs
        var cut = Render<SettingsTreeNav>();

        var nodes = cut.FindAll("a.tree-node");
        Assert.NotEmpty(nodes);
    }

    [Fact]
    public void TreeNav_TreeNodes_HaveCorrectHrefs()
    {
        var cut = Render<SettingsTreeNav>();

        var issueNode = cut.FindAll("a.tree-node").FirstOrDefault(n => n.TextContent.Trim() == "Issue");
        Assert.NotNull(issueNode);
        var href = issueNode!.GetAttribute("href");
        Assert.NotNull(href);
        Assert.Contains($"section={SettingsNodes.ProvidersIssue}", href);
    }

    [Fact]
    public void TreeNav_AllPipelineNodes_HaveCorrectHrefs()
    {
        var cut = Render<SettingsTreeNav>();

        var generalNode = cut.FindAll("a.tree-node").FirstOrDefault(n => n.TextContent.Trim() == "General");
        Assert.NotNull(generalNode);
        var href = generalNode!.GetAttribute("href");
        Assert.NotNull(href);
        Assert.Contains($"section={SettingsNodes.PipelineGeneral}", href);
    }

    [Fact]
    public void TreeNav_SelectedNode_IsTheOnlyActiveNode()
    {
        var cut = Render<SettingsTreeNav>(p => p.Add(t => t.SelectedNode, SettingsNodes.PipelineLoop));

        var active = Assert.Single(cut.FindAll("a.tree-node.active"));
        Assert.Equal("Pipeline Loop", active.TextContent.Trim());
        Assert.Equal("page", active.GetAttribute("aria-current"));
        Assert.All(cut.FindAll("a.tree-node:not(.active)"), n => Assert.Null(n.GetAttribute("aria-current")));
    }

    [Fact]
    public void TreeNav_NoSelectedNode_HasNoActiveNode()
    {
        var cut = Render<SettingsTreeNav>();

        Assert.Empty(cut.FindAll("a.tree-node.active"));
    }

    [Fact]
    public void TreeNav_SelectedProjectNode_IsActiveAndLinksToProject()
    {
        var mockConfigClient = new Mock<IPipelineApiConfigClient>();
        mockConfigClient.Setup(s => s.GetProjectsAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { new PipelineProject { Id = "p-1", Name = "Alpha" } });
        Services.AddSingleton(mockConfigClient.Object);
        var nodeId = $"{SettingsNodes.ProjectDetail}:p-1";

        var cut = Render<SettingsTreeNav>(p => p.Add(t => t.SelectedNode, nodeId));

        var active = Assert.Single(cut.FindAll("a.tree-node.active"));
        Assert.Equal("Alpha", active.TextContent.Trim());
        Assert.Equal($"settings?section={Uri.EscapeDataString(nodeId)}", active.GetAttribute("href"));
    }

    [Fact]
    public async Task TreeNav_ToggleProviders_CollapsesGroup()
    {
        var cut = Render<SettingsTreeNav>();

        // Initially expanded — should show "Issue" node
        Assert.Contains("Issue", cut.Markup);

        // Click the "Providers" group header button to collapse
        var providerHeader = cut.FindAll("button.tree-group-header").First(h => h.TextContent.Contains("Providers"));
        await cut.InvokeAsync(() => providerHeader.Click());

        // After collapse, children should not be visible
        var childNodes = cut.FindAll(".tree-group-children");
        // The Providers group children should be gone (Projects, Global Defaults, Label Routing, and Data Management remain)
        Assert.Equal(4, childNodes.Count);
    }

    [Fact]
    public async Task TreeNav_ToggleProviders_UpdatesAriaExpanded()
    {
        var cut = Render<SettingsTreeNav>();

        var providerHeader = cut.FindAll("button.tree-group-header").First(h => h.TextContent.Contains("Providers"));
        Assert.Equal("true", providerHeader.GetAttribute("aria-expanded"));

        await cut.InvokeAsync(() => providerHeader.Click());

        providerHeader = cut.FindAll("button.tree-group-header").First(h => h.TextContent.Contains("Providers"));
        Assert.Equal("false", providerHeader.GetAttribute("aria-expanded"));
    }

    [Fact]
    public async Task TreeNav_TogglePipeline_CollapsesGroup()
    {
        var cut = Render<SettingsTreeNav>();

        // Click the "Global Defaults" group header to collapse
        var pipelineHeader = cut.FindAll("button.tree-group-header").First(h => h.TextContent.Contains("Global Defaults"));
        await cut.InvokeAsync(() => pipelineHeader.Click());

        // "General" node should no longer be visible
        var allNodes = cut.FindAll("a.tree-node");
        Assert.DoesNotContain(allNodes, n => n.TextContent.Trim() == "General");
    }

    [Fact]
    public void TreeNav_AllGroupsExpandedByDefault()
    {
        var cut = Render<SettingsTreeNav>();

        // All five groups should have children visible
        var childGroups = cut.FindAll(".tree-group-children");
        Assert.Equal(5, childGroups.Count);
    }

    [Fact]
    public void TreeNav_ShowsChevrons()
    {
        var cut = Render<SettingsTreeNav>();

        var chevrons = cut.FindAll(".tree-chevron");
        Assert.Equal(5, chevrons.Count);
        // All expanded by default, so all should show chevron-down icon
        Assert.All(chevrons, c => Assert.NotNull(c.QuerySelector("[data-icon='chevron-down']")));
    }

    [Fact]
    public async Task TreeNav_CollapsedGroup_ShowsRightChevron()
    {
        var cut = Render<SettingsTreeNav>();

        // Collapse the Providers group
        var providerHeader = cut.FindAll("button.tree-group-header").First(h => h.TextContent.Contains("Providers"));
        await cut.InvokeAsync(() => providerHeader.Click());

        var chevrons = cut.FindAll(".tree-chevron");
        // First chevron (Providers) should now be chevron-right
        Assert.NotNull(chevrons[0].QuerySelector("[data-icon='chevron-right']"));
    }
}
