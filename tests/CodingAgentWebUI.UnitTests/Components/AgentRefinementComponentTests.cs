using Bunit;
using CodingAgentWebUI.Components.Pages;

namespace CodingAgentWebUI.UnitTests.Components;

/// <summary>
/// bUnit component tests for the AgentRefinement page.
/// </summary>
public class AgentRefinementComponentTests : BunitContext
{
    [Fact]
    public void AgentRefinement_RendersPageHeader()
    {
        var component = Render<AgentRefinement>();

        Assert.Contains("Agent Refinement", component.Markup);
        Assert.NotNull(component.Find("h1"));
    }

    [Fact]
    public void AgentRefinement_RendersComingSoonMessage()
    {
        var component = Render<AgentRefinement>();

        Assert.Contains("Coming soon", component.Markup);
    }

    [Fact]
    public void AgentRefinement_HasAboutPageClass()
    {
        var component = Render<AgentRefinement>();

        Assert.NotNull(component.Find(".about-page"));
    }

    [Fact]
    public void AgentRefinement_HasHeaderSection()
    {
        var component = Render<AgentRefinement>();

        Assert.NotNull(component.Find(".about-header"));
    }

    [Fact]
    public void AgentRefinement_HasContentSection()
    {
        var component = Render<AgentRefinement>();

        Assert.NotNull(component.Find(".about-content"));
    }

    [Fact]
    public void AgentRefinement_HasMutedParagraph()
    {
        var component = Render<AgentRefinement>();

        Assert.NotNull(component.Find(".about-muted"));
    }
}
