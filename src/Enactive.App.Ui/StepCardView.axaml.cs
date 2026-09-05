using Avalonia.Controls;

namespace Enactive.App.Ui;

/// <summary>
/// The card for one plan step. All of it is in the .axaml; the state and the log it renders live in
/// StepCardViewModel, which is what the orchestrator's events actually talk to.
/// </summary>
internal sealed partial class StepCardView : UserControl
{
    public StepCardView() => InitializeComponent();
}
