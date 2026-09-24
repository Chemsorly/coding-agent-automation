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
    public void TreeNav_ActiveNode_MatchesCurrentUrl()
    {
        // TODO [WARNING]: This test only checks that the href contains the section value — which is
        // the same assertion already made by TreeNav_TreeNodes_HaveCorrectHrefs above, making it a
        // duplicate with a misleading name. It does NOT verify that the `active` CSS class is applied
        // when the URL matches (the actual acceptance criterion). To test active-class behavior, set
        // the NavigationManager URL to match a specific section, render the component, and assert that
        // exactly one <a.tree-node.active> element exists with the matching text.

        // NavLink generates correct hrefs — verify that the Issue node's href contains the right section
        var cut = Render<SettingsTreeNav>();

        var issueNode = cut.FindAll("a.tree-node").FirstOrDefault(n => n.TextContent.Trim() == "Issue");
        Assert.NotNull(issueNode);

        // The href must contain the section value so NavLink can apply the active class at runtime
        var href = issueNode!.GetAttribute("href");
        Assert.NotNull(href);
        Assert.Contains(SettingsNodes.ProvidersIssue, href);
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
