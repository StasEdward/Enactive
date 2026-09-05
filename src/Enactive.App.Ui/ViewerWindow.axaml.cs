using Avalonia.Controls;

namespace Enactive.App.Ui;

/// <summary>
/// The window behind everything this app shows as plain text - the environment probe, the project
/// memory digest, a file an artifact wrote, a diff.
///
/// <para>It used to be a bare <c>new Window</c> built in code with a TextBox in it, which is why
/// Environment and Project Memory still had the system title bar after every other window stopped:
/// there was no .axaml to dress. It is a real window now, so it wears the same bar as the rest and
/// opens owned by the window that asked for it, instead of floating loose.</para>
/// </summary>
public sealed partial class ViewerWindow : Window
{
    public ViewerWindow()
    {
        InitializeComponent();

        CopyButton.Click += async (_, _) =>
        {
            var clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
                return;

            await clipboard.SetTextAsync(ContentBox.Text ?? string.Empty);
            StatusText.Text = "Copied.";
        };
    }

    public static void Show(Window owner, string title, string content)
    {
        var window = new ViewerWindow { Title = title };
        window.ContentBox.Text = content;

        var lines = content.Length == 0 ? 0 : content.AsSpan().Count('\n') + 1;
        window.StatusText.Text = $"{lines} line{(lines == 1 ? "" : "s")} · {content.Length} characters";

        window.Show(owner);
    }
}
