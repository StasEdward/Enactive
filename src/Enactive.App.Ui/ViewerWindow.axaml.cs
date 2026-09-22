using Avalonia.Controls;
// SetTextAsync is an extension in Avalonia 12, not a member of IClipboard.
using Avalonia.Input.Platform;

namespace Enactive.App.Ui;

/// <summary>
/// The window behind everything this app shows as a document - the environment probe, the project
/// memory digest, a file an artifact wrote, a diff.
///
/// <para>It used to be a bare <c>new Window</c> built in code with a TextBox in it, which is why
/// Environment and Project Memory still had the system title bar after every other window stopped:
/// there was no .axaml to dress. It is a real window now, so it wears the same bar as the rest and
/// opens owned by the window that asked for it, instead of floating loose.</para>
///
/// <para><b>And a file is shown as what it IS.</b> A run's whole deliverable is often one Markdown
/// document, and every one of them arrived here as monospaced source in a box that does not wrap -
/// the same box as an environment probe, because that box was all there was, while the renderer
/// for it had been sitting in the log analysis window since it was written.
/// <see cref="ArtifactViewerCatalog"/> chooses; a viewer can also come from a folder, so the next
/// type does not need this file changed.</para>
///
/// <para><b>The source is always one click away.</b> The log analysis window learnt that first:
/// a renderer that handles a subset must never be the only way to see a file, because whatever it
/// did not understand is only readable as itself.</para>
/// </summary>
public sealed partial class ViewerWindow : Window
{
    /// <summary>What Copy hands over: the file, not whatever a renderer made of it.</summary>
    private string _source = string.Empty;

    public ViewerWindow()
    {
        InitializeComponent();

        CopyButton.Click += async (_, _) =>
        {
            var clipboard = GetTopLevel(this)?.Clipboard;
            if (clipboard is null)
                return;

            await clipboard.SetTextAsync(_source);
            StatusText.Text = "Copied.";
        };

        ShowSource.IsCheckedChanged += (_, _) =>
        {
            var source = ShowSource.IsChecked == true;
            ContentBox.IsVisible = source;
            Rendered.IsVisible = !source;
        };
    }

    /// <param name="relativePath">
    /// The file's name, when this IS a file - it is the only thing that decides which viewer is
    /// used. Null for the app's own reports, which are text and are shown as text.
    /// </param>
    public static void Show(Window owner, string title, string content, string? relativePath = null)
    {
        var window = new ViewerWindow { Title = title };
        window._source = content;
        window.ContentBox.Text = content;

        var lines = content.Length == 0 ? 0 : content.AsSpan().Count('\n') + 1;
        var measured = $"{lines} line{(lines == 1 ? "" : "s")} · {content.Length} characters";

        if (ArtifactViewerCatalog.HasRenderer(relativePath))
        {
            var viewer = ArtifactViewerCatalog.For(relativePath);

            // A viewer is somebody's code, and a plugin's more so. One that throws must cost the
            // reader the rendering, never the file: the source pane below is already filled in.
            try
            {
                window.Body.Content = viewer.Build(content);
                window.ContentBox.IsVisible = false;
                window.Rendered.IsVisible = true;
                window.ShowSource.IsVisible = true;
                measured += $" · shown as {viewer.Id}";
            }
            catch (Exception ex)
            {
                measured += $" · the {viewer.Id} viewer failed ({ex.Message}), showing the source";
            }
        }

        if (ArtifactViewerCatalog.Problems.Count > 0)
            measured += $" · {ArtifactViewerCatalog.Problems.Count} viewer plugin(s) could not be loaded";

        window.StatusText.Text = measured;
        window.Show(owner);
    }
}
