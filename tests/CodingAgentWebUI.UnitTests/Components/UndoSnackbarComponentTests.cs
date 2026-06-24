using Bunit;
using CodingAgentWebUI.Components.Shared;

namespace CodingAgentWebUI.UnitTests.Components;

public class UndoSnackbarComponentTests : BunitContext
{
    [Fact]
    public void UndoSnackbar_InitiallyHidden()
    {
        var cut = Render<UndoSnackbar>();
        Assert.DoesNotContain("undo-snackbar", cut.Markup);
    }

    [Fact]
    public async Task UndoSnackbar_Show_RendersMessageAndButton()
    {
        var cut = Render<UndoSnackbar>();
        var component = cut.Instance;

        await cut.InvokeAsync(() => component.Show("Action completed.", () => Task.CompletedTask));

        Assert.Contains("Action completed.", cut.Markup);
        Assert.Contains("Undo", cut.Markup);
        Assert.Contains("undo-snackbar", cut.Markup);
    }

    [Fact]
    public async Task UndoSnackbar_UndoClick_InvokesCallback()
    {
        var undoCalled = false;
        var cut = Render<UndoSnackbar>();
        var component = cut.Instance;

        await cut.InvokeAsync(() => component.Show("Test.", async () => { undoCalled = true; await Task.CompletedTask; }));

        cut.Find(".undo-snackbar-btn").Click();

        Assert.True(undoCalled);
        Assert.DoesNotContain("undo-snackbar", cut.Markup);
    }

    [Fact]
    public async Task UndoSnackbar_SecondShow_ReplacesFirst()
    {
        var cut = Render<UndoSnackbar>();
        var component = cut.Instance;

        await cut.InvokeAsync(() => component.Show("First.", () => Task.CompletedTask));
        await cut.InvokeAsync(() => component.Show("Second.", () => Task.CompletedTask));

        Assert.Contains("Second.", cut.Markup);
        Assert.DoesNotContain("First.", cut.Markup);
    }
}
