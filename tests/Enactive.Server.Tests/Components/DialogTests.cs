namespace Enactive.Server.Tests.Components;

using Bunit;
using Enactive.Server.Components.Ui;
using Microsoft.AspNetCore.Components.Web;

/// <summary>
/// A question asked over the screen (Ui/Dialog.razor) holds the keyboard while it is open: the focus goes into it, the
/// rest of the page is out of reach, and closing it gives the page back. Drawn over the screen but not in charge of it,
/// Tab walked the rows behind the scrim and another row's Delete could be pressed while a question was open.
/// </summary>
public sealed class DialogTests : ComponentHost
{
    private bool _closed;

    private IRenderedComponent<Dialog> Open() => Render<Dialog>(p => p
        .Add(d => d.Title, "Delete 1 run?")
        .Add(d => d.OnClose, () => _closed = true)
        .Add(d => d.Actions, "<button type=\"button\">Delete 1</button>"));

    [Fact]
    public void Opening_it_takes_the_focus_and_the_page()
    {
        var dialog = Open();

        var focused = JSInterop.VerifyFocusAsyncInvoke();
        Assert.Equal(dialog.Find("[role=dialog]").GetAttribute("blazor:elementReference"),
            ((Microsoft.AspNetCore.Components.ElementReference)focused.Arguments[0]!).Id);
        // The rest made inert before the focus moves, so what had it is known to give it back.
        DialogScript.VerifyInvoke("hold");
    }

    [Fact]
    public async Task Closing_it_gives_the_page_and_the_focus_back()
    {
        Open();

        await DisposeComponentsAsync();

        DialogScript.VerifyInvoke("release");
    }

    [Fact]
    public void Escape_closes_it()
    {
        var dialog = Open();

        dialog.Find("[role=dialog]").KeyDown(new KeyboardEventArgs { Key = "Escape" });

        Assert.True(_closed);
    }

    [Fact]
    public void The_scrim_closes_it()
    {
        var dialog = Open();

        dialog.Find(".dialog-scrim").Click();

        Assert.True(_closed);
    }
}
