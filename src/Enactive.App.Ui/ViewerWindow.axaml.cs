using Avalonia.Controls;
using Enactive.App.Ui.ViewModels;
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
///
/// <para><b>A file a run wrote is reviewed as what the run changed</b> (<see cref="ShowReview"/>): a diff against how it
/// was before the run, the whole file one switch away - as the service's Review shows it.</para>
/// </summary>
public sealed partial class ViewerWindow : Window
{
    /// <summary>What Copy hands over: the file, not whatever a renderer made of it.</summary>
    private string _source = string.Empty;

    /// <summary>What the switch goes back to from the source: a renderer's view of the file, or a review's diff of it.</summary>
    private Control? _rendering;

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
            if (_rendering is not null)
                _rendering.IsVisible = !source;
        };
    }

    /// <param name="relativePath">
    /// The file's name, when this IS a file - it is the only thing that decides which viewer is
    /// used. Null for the app's own reports, which are text and are shown as text.
    /// </param>
    /// <param name="lead">A line above the document on what it is a reading of; none, nothing.</param>
    public static void Show(Window owner, string title, string content, string? relativePath = null, string? lead = null)
    {
        var window = new ViewerWindow { Title = title };
        window.Lead(lead);
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
                window.ShowRendering(window.Rendered);
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

    /// <summary>
    /// A file a run wrote, reviewed (<see cref="FileReviewViewModel"/>): the diff first, when there is one - it is what
    /// Review is for - with the whole file as it is now one switch away, and what the diff is against said above it;
    /// without one, the file as Show shows it, with why there is no diff said above it. Copy all hands over the file.
    /// </summary>
    internal static void ShowReview(Window owner, string title, string relativePath, FileReviewViewModel review)
    {
        if (review.Problem is { } problem)
        {
            Show(owner, title, problem);
            return;
        }
        if (!review.HasDiff)
        {
            Show(owner, title, review.Text, relativePath, review.Note);
            return;
        }

        var window = new ViewerWindow { Title = title };
        window._source = review.Text;
        window.ContentBox.Text = review.Text;
        window.DiffRows.ItemsSource = review.Rows;
        window.ShowRendering(window.DiffPane);
        window.ShowSource.Content = "Whole file";
        ToolTip.SetTip(window.ShowSource, "Show the whole file as it is now.");
        window.Lead(review.Note);

        window.StatusText.Text = review.Measured;
        window.Show(owner);
    }

    /// <summary>Shows <paramref name="rendering"/> in place of the source, with the switch back to it.</summary>
    private void ShowRendering(Control rendering)
    {
        _rendering = rendering;
        ContentBox.IsVisible = false;
        rendering.IsVisible = true;
        ShowSource.IsVisible = true;
    }

    private void Lead(string? note)
    {
        LeadText.Text = note;
        LeadText.IsVisible = note is { Length: > 0 };
    }
}
