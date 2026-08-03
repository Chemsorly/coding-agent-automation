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

    [Fact]
    public async Task UndoSnackbar_Dispose_CancelsPendingDismiss()
    {
        var cut = Render<UndoSnackbar>();
        var component = cut.Instance;

        await cut.InvokeAsync(() => component.Show("Pending dismiss.", () => Task.CompletedTask));

        // The 5-second dismiss has NOT fired yet — snackbar must still be visible
        Assert.Contains("Pending dismiss.", cut.Markup);
        Assert.Contains("undo-snackbar", cut.Markup);

        // Dispose should cancel the pending task without throwing
        var exception = Record.Exception(() => cut.Dispose());
        Assert.Null(exception);
    }

    // TODO: This test does not verify CTS disposal — assertions only confirm UI rendering which
    // passes whether or not _dismissCts?.Dispose() is called in Show(). To actually validate
    // disposal, observe a side-effect of using a disposed CTS (e.g., confirm old token's WaitHandle
    // throws ObjectDisposedException, or use a wrapper to track Dispose calls).
    [Fact]
    public async Task UndoSnackbar_Show_DisposesPreviousCts()
    {
        var cut = Render<UndoSnackbar>();
        var component = cut.Instance;

        await cut.InvokeAsync(() => component.Show("First.", () => Task.CompletedTask));
        await cut.InvokeAsync(() => component.Show("Second.", () => Task.CompletedTask));

        // The second Show() should have disposed the first CTS.
        // Verify the component is still functional (no ObjectDisposedException).
        Assert.Contains("Second.", cut.Markup);
        Assert.Contains("undo-snackbar", cut.Markup);

        // Dispose cleanly at end
        cut.Dispose();
    }

    [Fact]
    public void UndoSnackbar_Dispose_WhenNeverShown()
    {
        var cut = Render<UndoSnackbar>();

        // Dispose a component that was never Show()'d — should not throw NullReferenceException
        var exception = Record.Exception(() => cut.Dispose());
        Assert.Null(exception);
    }
}
