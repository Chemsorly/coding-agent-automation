using Bunit;
using CodingAgentWebUI.Components.Shared;

namespace CodingAgentWebUI.UnitTests.Components;

public class ShortcutHelpOverlayTests : BunitContext
{
    [Fact]
    public void HiddenByDefault()
    {
        var cut = Render<ShortcutHelpOverlay>(parameters => parameters
            .Add(p => p.IsVisible, false));

        Assert.Empty(cut.Markup.Trim());
    }

    [Fact]
    public void RendersShortcutListWhenVisible()
    {
        var cut = Render<ShortcutHelpOverlay>(parameters => parameters
            .Add(p => p.IsVisible, true));

        Assert.Contains("Keyboard Shortcuts", cut.Markup);
        Assert.Contains("Esc", cut.Markup);
        Assert.Contains("Close drawer", cut.Markup);
        Assert.Contains("↓", cut.Markup);
        Assert.Contains("↑", cut.Markup);
        Assert.Contains("Enter", cut.Markup);
    }

    [Fact]
    public void CloseButtonInvokesOnClose()
    {
        var closed = false;
        var cut = Render<ShortcutHelpOverlay>(parameters => parameters
            .Add(p => p.IsVisible, true)
            .Add(p => p.OnClose, () => closed = true));

        cut.Find("button.agent-detail-close").Click();
        Assert.True(closed);
    }

    [Fact]
    public void BackdropClickInvokesOnClose()
    {
        var closed = false;
        var cut = Render<ShortcutHelpOverlay>(parameters => parameters
            .Add(p => p.IsVisible, true)
            .Add(p => p.OnClose, () => closed = true));

        cut.Find(".shortcut-overlay-backdrop").Click();
        Assert.True(closed);
    }
}
