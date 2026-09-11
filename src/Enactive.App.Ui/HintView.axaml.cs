using Avalonia;
using Avalonia.Controls;

namespace Enactive.App.Ui;

/// <summary>
/// The small "?" beside a label, holding advice a person reads while choosing.
///
/// <para>The text is NOT written here and is not written in the window either — see
/// <c>Enactive.Core.Providers.PhaseAdvice</c>. This control is the presentation and nothing else,
/// which is what lets the wording be tested: <c>Enactive.App.Ui</c> is a WinExe no test project
/// references, so a sentence that lives in a .axaml is a sentence nothing can check.</para>
///
/// <para>A styled property rather than a plain CLR one so the text can be bound — a hint whose
/// wording depends on what is selected is a thing this will be asked for, and retro-fitting
/// bindability to a property people already set in XAML is the kind of change that silently stops
/// working for whoever set it the old way.</para>
/// </summary>
public sealed partial class HintView : UserControl
{
    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<HintView, string?>(nameof(Text));

    public HintView()
    {
        InitializeComponent();

        // Nothing to say means no question mark at all. A "?" that opens an empty panel is worse
        // than no "?" — it invites a click and answers nothing, and a reader then distrusts the
        // other three.
        UpdateVisibility();
    }

    /// <summary>The advice this shows. Null or blank hides the control entirely.</summary>
    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty)
        {
            Body.Text = Text;
            UpdateVisibility();
        }
    }

    private void UpdateVisibility() => IsVisible = !string.IsNullOrWhiteSpace(Text);
}
