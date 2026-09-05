using Avalonia.Controls;
using Avalonia.Input;

namespace Enactive.App.Ui;

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

    public ConfirmWindow()
    {
        InitializeComponent();

        ConfirmButton.Click += (_, _) => { _confirmed = true; Close(); };
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
}
