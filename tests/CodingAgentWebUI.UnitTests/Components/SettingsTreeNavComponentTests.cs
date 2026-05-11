using Bunit;
using Microsoft.AspNetCore.Components;
using CodingAgentWebUI.Components.Pages;
using CodingAgentWebUI.Pipeline.Models;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for the SettingsTreeNav component.
/// Covers rendering of tree groups, node selection, and expand/collapse behavior.
/// </summary>
public class SettingsTreeNavComponentTests : BunitContext
{
    [Fact]
    public void TreeNav_RendersAllGroups()
    {
        var cut = Render<SettingsTreeNav>(p => p
            .Add(s => s.SelectedNode, "")
            .Add(s => s.OnNodeSelected, EventCallback<string>.Empty));

        Assert.Contains("Providers", cut.Markup);
        Assert.Contains("Pipeline", cut.Markup);
        Assert.Contains("Label Routing", cut.Markup);
    }

    [Fact]
    public void TreeNav_RendersProviderNodes()
    {
        var cut = Render<SettingsTreeNav>(p => p
            .Add(s => s.SelectedNode, "")
            .Add(s => s.OnNodeSelected, EventCallback<string>.Empty));

        Assert.Contains("Issue", cut.Markup);
        Assert.Contains("Repository", cut.Markup);
        Assert.Contains("Agent", cut.Markup);
    }

    [Fact]
    public void TreeNav_RendersPipelineNodes()
    {
        var cut = Render<SettingsTreeNav>(p => p
            .Add(s => s.SelectedNode, "")
            .Add(s => s.OnNodeSelected, EventCallback<string>.Empty));

        Assert.Contains("General", cut.Markup);
        Assert.Contains("Pipeline Loop", cut.Markup);
        Assert.Contains("Prompts", cut.Markup);
        Assert.Contains("Quality Gates", cut.Markup);
        Assert.DoesNotContain("Security", cut.Markup);
    }

    [Fact]
    public void TreeNav_RendersLabelRoutingNodes()
    {
        var cut = Render<SettingsTreeNav>(p => p
            .Add(s => s.SelectedNode, "")
            .Add(s => s.OnNodeSelected, EventCallback<string>.Empty));

        Assert.Contains("Agent Profiles", cut.Markup);
        Assert.Contains("Quality Gate Configs", cut.Markup);
        Assert.Contains("Reviewer Configs", cut.Markup);
    }

    [Fact]
    public void TreeNav_SelectedNode_HasActiveClass()
    {
        var cut = Render<SettingsTreeNav>(p => p
            .Add(s => s.SelectedNode, SettingsNodes.ProvidersIssue)
            .Add(s => s.OnNodeSelected, EventCallback<string>.Empty));

        var activeNodes = cut.FindAll(".tree-node.active");
        Assert.Single(activeNodes);
        Assert.Contains("Issue", activeNodes[0].TextContent);
    }

    [Fact]
    public async Task TreeNav_ClickNode_InvokesOnNodeSelected()
    {
        string? selectedNode = null;
        var cut = Render<SettingsTreeNav>(p => p
            .Add(s => s.SelectedNode, "")
            .Add(s => s.OnNodeSelected, EventCallback.Factory.Create<string>(this, v => selectedNode = v)));

        // Click the "Issue" node
        var issueNode = cut.FindAll(".tree-node").First(n => n.TextContent.Trim() == "Issue");
        await issueNode.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(SettingsNodes.ProvidersIssue, selectedNode);
    }

    [Fact]
    public async Task TreeNav_ClickPipelineNode_InvokesCorrectNodeId()
    {
        string? selectedNode = null;
        var cut = Render<SettingsTreeNav>(p => p
            .Add(s => s.SelectedNode, "")
            .Add(s => s.OnNodeSelected, EventCallback.Factory.Create<string>(this, v => selectedNode = v)));

        var generalNode = cut.FindAll(".tree-node").First(n => n.TextContent.Trim() == "General");
        await generalNode.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        Assert.Equal(SettingsNodes.PipelineGeneral, selectedNode);
    }

    [Fact]
    public async Task TreeNav_ToggleProviders_CollapsesGroup()
    {
        var cut = Render<SettingsTreeNav>(p => p
            .Add(s => s.SelectedNode, "")
            .Add(s => s.OnNodeSelected, EventCallback<string>.Empty));

        // Initially expanded — should show "Issue" node
        Assert.Contains("Issue", cut.Markup);

        // Click the "Providers" group header to collapse
        var providerHeader = cut.FindAll(".tree-group-header").First(h => h.TextContent.Contains("Providers"));
        await providerHeader.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        // After collapse, children should not be visible
        var childNodes = cut.FindAll(".tree-group-children");
        // The Providers group children should be gone (only Pipeline and Label Routing remain)
        Assert.Equal(2, childNodes.Count);
    }

    [Fact]
    public async Task TreeNav_TogglePipeline_CollapsesGroup()
    {
        var cut = Render<SettingsTreeNav>(p => p
            .Add(s => s.SelectedNode, "")
            .Add(s => s.OnNodeSelected, EventCallback<string>.Empty));

        // Click the "Pipeline" group header to collapse
        var pipelineHeader = cut.FindAll(".tree-group-header").First(h => h.TextContent.Contains("Pipeline"));
        await pipelineHeader.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        // "General" node should no longer be visible
        var allNodes = cut.FindAll(".tree-node");
        Assert.DoesNotContain(allNodes, n => n.TextContent.Trim() == "General");
    }

    [Fact]
    public void TreeNav_AllGroupsExpandedByDefault()
    {
        var cut = Render<SettingsTreeNav>(p => p
            .Add(s => s.SelectedNode, "")
            .Add(s => s.OnNodeSelected, EventCallback<string>.Empty));

        // All three groups should have children visible
        var childGroups = cut.FindAll(".tree-group-children");
        Assert.Equal(3, childGroups.Count);
    }

    [Fact]
    public void TreeNav_ShowsChevrons()
    {
        var cut = Render<SettingsTreeNav>(p => p
            .Add(s => s.SelectedNode, "")
            .Add(s => s.OnNodeSelected, EventCallback<string>.Empty));

        var chevrons = cut.FindAll(".tree-chevron");
        Assert.Equal(3, chevrons.Count);
        // All expanded by default, so all should show ▼
        Assert.All(chevrons, c => Assert.Contains("▼", c.TextContent));
    }

    [Fact]
    public async Task TreeNav_CollapsedGroup_ShowsRightChevron()
    {
        var cut = Render<SettingsTreeNav>(p => p
            .Add(s => s.SelectedNode, "")
            .Add(s => s.OnNodeSelected, EventCallback<string>.Empty));

        // Collapse the Providers group
        var providerHeader = cut.FindAll(".tree-group-header").First(h => h.TextContent.Contains("Providers"));
        await providerHeader.ClickAsync(new Microsoft.AspNetCore.Components.Web.MouseEventArgs());

        var chevrons = cut.FindAll(".tree-chevron");
        // First chevron (Providers) should now be ▶
        Assert.Contains("▶", chevrons[0].TextContent);
    }
}
