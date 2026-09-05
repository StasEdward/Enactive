using Avalonia.Controls;
using Avalonia.Input;

namespace Enactive.App.Ui;

/// <summary>
/// Asks for one line of text. Like <see cref="ConfirmWindow"/>, it exists because Avalonia has no
/// dialog of its own and this app has exactly one question of this shape - what to call a
/// workspace - which is not worth a view model.
///
/// Every way out that is not Save answers null, and an answer that is only whitespace is null too:
/// a rename to nothing is not a rename.
/// </summary>
public sealed partial class PromptWindow : Window
{
    private string? _answer;

    public PromptWindow()
    {
        InitializeComponent();

        OkButton.Click += (_, _) =>
        {
            var text = ValueBox.Text?.Trim();
            _answer = string.IsNullOrEmpty(text) ? null : text;
            Close();
        };
        CancelButton.Click += (_, _) => Close();

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;
            e.Handled = true;
            Close();
        };

        Opened += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    /// <summary>The text the user settled on, or null if they did not.</summary>
    public static async Task<string?> AskAsync(Window owner, string headline, string detail, string value)
    {
        var dialog = new PromptWindow();
        dialog.HeadlineText.Text = headline;
        dialog.DetailText.Text = detail;
        dialog.ValueBox.Text = value;

        await dialog.ShowDialog(owner);
        return dialog._answer;
    }
}
