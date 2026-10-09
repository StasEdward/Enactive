using Avalonia.Controls;
using Avalonia.Input;

namespace Enactive.App.Ui;

/// <summary>Which way a question with two ways forward was answered - see <see cref="ConfirmWindow.ChooseAsync"/>.</summary>
public enum ConfirmChoice { Cancel, Confirm, Alternative }

/// <summary>
/// A yes/no dialog. There is no message box in Avalonia, and the one question this app has to ask -
/// "you have work in flight, close anyway?" - is not worth a view model.
///
/// It answers FALSE to every way of dismissing it that is not the confirm button: Escape, the system
/// close button, and Cancel. A dialog that guards against an accidental close must not be closable
/// by accident itself.
/// </summary>
public sealed partial class ConfirmWindow : Window
{
    private bool _confirmed;
    private bool _alternative;

    public ConfirmWindow()
    {
        InitializeComponent();

        ConfirmButton.Click += (_, _) => { _confirmed = true; Close(); };
        AlternativeButton.Click += (_, _) => { _alternative = true; Close(); };
        CancelButton.Click += (_, _) => Close();

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape)
                return;
            e.Handled = true;
            Close();
        };
    }

    /// <summary>Shows the question and waits for the answer. True only if the confirm was clicked.</summary>
    public static async Task<bool> AskAsync(
        Window owner, string headline, string detail, string confirm, string cancel)
    {
        var dialog = new ConfirmWindow();
        dialog.HeadlineText.Text = headline;
        dialog.DetailText.Text = detail;
        dialog.ConfirmButton.Content = confirm;
        dialog.CancelButton.Content = cancel;

        await dialog.ShowDialog(owner);
        return dialog._confirmed;
    }

    /// <summary>
    /// The same question with two ways to go ahead - and still Cancel for every way of dismissing it that is not one of
    /// them. For "delete this, and also undo what it did?", where both answers go ahead and only one takes more back.
    /// </summary>
    public static async Task<ConfirmChoice> ChooseAsync(
        Window owner, string headline, string detail, string confirm, string alternative, string cancel)
    {
        var dialog = new ConfirmWindow();
        dialog.HeadlineText.Text = headline;
        dialog.DetailText.Text = detail;
        dialog.ConfirmButton.Content = confirm;
        dialog.AlternativeButton.Content = alternative;
        dialog.AlternativeButton.IsVisible = true;
        dialog.CancelButton.Content = cancel;

        await dialog.ShowDialog(owner);
        return dialog._confirmed ? ConfirmChoice.Confirm : dialog._alternative ? ConfirmChoice.Alternative : ConfirmChoice.Cancel;
    }
}
