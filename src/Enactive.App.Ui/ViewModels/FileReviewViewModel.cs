namespace Enactive.App.Ui.ViewModels;

using System.Globalization;
using Enactive.Application;
using Enactive.Application.Projections;

/// <summary>
/// Review on a file a run wrote, as the viewer shows it - the service's review (WrittenFileReview, the same decision for
/// both hosts): what the run changed in it, as a diff against how it was before the run, with each line's numbers in the
/// file as it was and as it is and the whole file one switch away; or the file as it is, with why there is no diff.
///
/// <para>Review used to open the file's text only, so a run that changed one line of a long file showed the long file
/// and left the person to find the line - while the service's Review already showed the change.</para>
/// </summary>
internal sealed class FileReviewViewModel
{
    public FileReviewViewModel(WrittenFileReview.Review review)
    {
        Text = review.Text ?? "";
        Problem = review.Problem;
        Note = review.Note;
        Rows = review.Diff is { } diff ? DiffLines.Numbered(diff).Select(line => new DiffRowViewModel(line)).ToArray() : [];
    }

    /// <summary>The file as it is now: the whole file the switch shows, and what Copy all hands over.</summary>
    public string Text { get; }

    /// <summary>Why the file cannot be shown at all - gone, too big, not text - or null.</summary>
    public string? Problem { get; }

    /// <summary>Above the review: what the diff is against, or why there is none.</summary>
    public string Note { get; }

    /// <summary>The diff's lines; none when there is no diff to show.</summary>
    public IReadOnlyList<DiffRowViewModel> Rows { get; }

    public bool HasDiff => Rows.Count > 0;

    /// <summary>Beside the buttons: how much the run changed - the lines only the new file has, and only the old.</summary>
    public string Measured
    {
        get
        {
            var added = Rows.Count(r => r.Old.Length == 0 && r.New.Length > 0);
            var removed = Rows.Count(r => r.Old.Length > 0 && r.New.Length == 0);
            return $"{added} line{(added == 1 ? "" : "s")} added · {removed} removed";
        }
    }
}

/// <summary>One line of a review's diff with its numbers beside it, drawn by what it does to the file (Palette.DiffLine, DiffRow).</summary>
internal sealed class DiffRowViewModel(NumberedDiffLine line)
{
    public string Text { get; } = line.Text;
    public DiffLineKind Kind { get; } = line.Kind;
    public string Old { get; } = Number(line.Old);
    public string New { get; } = Number(line.New);

    private static string Number(int? n) => n?.ToString(CultureInfo.InvariantCulture) ?? "";
}
