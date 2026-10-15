namespace Enactive.Server.Tests.Components;

using Bunit;
using Enactive.Server.Components.Ui;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

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

    /// <summary>
    /// Closed before its script has loaded - a double click whose second half lands on the scrim: the page is not held
    /// afterwards. Holding it then, for a dialog already gone, left every control on the page inert with nothing to
    /// release it, and focusing the missing dialog was an exception that took the page's connection down.
    /// </summary>
    [Fact]
    public async Task Closed_before_its_script_loads_it_never_holds_the_page()
    {
        // The import answered only when the test says, after the dialog is gone (bUnit's own stand-in answers at once).
        var loading = new TaskCompletionSource<IJSObjectReference>();
        Services.AddSingleton<IJSRuntime>(new ImportsWhenTold(loading.Task));
        Open();

        var closing = DisposeComponentsAsync();
        var script = new RecordingScript();
        loading.SetResult(script);
        await closing;

        Assert.Empty(script.Calls);
        Assert.True(script.Disposed);
        Assert.DoesNotContain(JSInterop.Invocations, i => i.Identifier.Contains("focus", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A page whose script import finishes when the test lets it.</summary>
    private sealed class ImportsWhenTold(Task<IJSObjectReference> import) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => identifier == "import" ? new ValueTask<TValue>(Imported<TValue>()) : throw new InvalidOperationException(identifier);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeAsync<TValue>(identifier, args);

        private async Task<TValue> Imported<TValue>() => (TValue)await import;
    }

    /// <summary>A script module that records what is called on it.</summary>
    private sealed class RecordingScript : IJSObjectReference
    {
        public List<string> Calls { get; } = [];
        public bool Disposed { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Add(identifier);
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => InvokeAsync<TValue>(identifier, args);

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
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
