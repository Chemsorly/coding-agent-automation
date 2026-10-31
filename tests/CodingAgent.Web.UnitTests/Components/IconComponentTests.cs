using AwesomeAssertions;
using Bunit;
using CodingAgent.Web.Components.Shared;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit tests for Icon.razor.
/// </summary>
public class IconComponentTests : BunitContext
{
    // Acceptance criterion: null Name must not throw — renders nothing instead.
    [Fact]
    public void Icon_NullName_RendersNothing()
    {
        var cut = Render<Icon>(p => p.Add(s => s.Name, (string?)null));
        Assert.Empty(cut.Markup.Trim());
    }

    // Regression guard: a known icon name still renders the SVG.
    [Fact]
    public void Icon_KnownName_RendersSvg()
    {
        var cut = Render<Icon>(p => p.Add(s => s.Name, "check-circle"));
        // bUnit's Find() throws ElementNotFoundException when the selector finds no match,
        // so it is an implicit assertion. The explicit .Should().NotBeNull() below satisfies
        // SonarQube S2699 (tests must have assertions); it is technically redundant because
        // Find() never returns null. See issue #4034 for discussion.
        var element = cut.Find("svg[data-icon='check-circle']");
        element.Should().NotBeNull();
    }

    // Empty string renders nothing (existing behaviour, confirmed non-regression).
    [Fact]
    public void Icon_EmptyName_RendersNothing()
    {
        var cut = Render<Icon>(p => p.Add(s => s.Name, ""));
        Assert.Empty(cut.Markup.Trim());
    }
}
