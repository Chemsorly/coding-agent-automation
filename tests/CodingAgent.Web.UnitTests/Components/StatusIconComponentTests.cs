using Bunit;
using CodingAgent.Web.Components.Shared;

namespace CodingAgent.Web.UnitTests.Components;

/// <summary>
/// bUnit tests for the enabled/disabled marker used in the Settings lists.
/// </summary>
public class StatusIconComponentTests : BunitContext
{
    [Theory]
    [InlineData(true, "is-on", "Enabled", "check-circle")]
    [InlineData(false, "is-off", "Disabled", "x-circle")]
    public void StatusIcon_RendersStateClassLabelAndGlyph(bool enabled, string stateClass, string label, string icon)
    {
        var cut = Render<StatusIcon>(p => p.Add(s => s.Enabled, enabled));

        var marker = cut.Find(".status-icon");
        Assert.Contains(stateClass, marker.ClassList);
        Assert.Equal(label, marker.GetAttribute("aria-label"));
        Assert.Equal(label, marker.GetAttribute("title"));

        // One size for every list, so the column no longer mixes 24px and 14px icons.
        var svg = cut.Find($"svg[data-icon='{icon}']");
        Assert.Equal("16", svg.GetAttribute("width"));
    }
}
