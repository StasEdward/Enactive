using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Enactive.App.Ui.ViewModels;

namespace Enactive.App.Ui;

/// <summary>
/// The card for one plan step. The layout is in the .axaml; the state and the log it renders live in
/// StepCardViewModel, which is what the orchestrator's events actually talk to. What is here is only
/// how a person opens it.
/// </summary>
internal sealed partial class StepCardView : UserControl
{
    public StepCardView()
    {
        InitializeComponent();

        // A card is opened by double-clicking anywhere on it, not only on the small "›" row - asked
        // for on 2026-09-24, with a task card of 101 tools and 64 notes to get into.
        DoubleTapped += OnDoubleTapped;

        // The "›" row opens it too, and gets the same scroll.
        AddHandler(Button.ClickEvent, (_, _) => ShowLatestSoon(), RoutingStrategies.Bubble);
    }

    private StepCardViewModel? Card => DataContext as StepCardViewModel;

    /// <summary>
    /// Opens or closes the card. A second quick click on one of its own buttons is somebody pressing
    /// that button twice, not a request to toggle the card - the same rule the settings lists use.
    /// </summary>
    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual source && source.FindAncestorOfType<Button>(true) is not null)
            return;

        if (Card is not { HasEntries: true } card)
            return;

        card.ToggleCommand.Execute(null);
        e.Handled = true;
        ShowLatestSoon();
    }

    /// <summary>
    /// When a person opens a card, what they want is where it has got to: its latest entries, at the
    /// bottom. Only for an opening THEY did - a card the engine opens by itself (a failure, a question)
    /// must not pull the page away from whatever is being read.
    ///
    /// <para>Posted rather than done now: the card has its new height only after the next layout pass,
    /// and before it the bottom is still where the closed card ended.</para>
    /// </summary>
    private void ShowLatestSoon()
        => Dispatcher.UIThread.Post(() =>
        {
            if (Card is not { IsExpanded: true })
                return;

            var height = Bounds.Height;
            this.BringIntoView(new Rect(0, Math.Max(0, height - 1), Bounds.Width, 1));
        }, DispatcherPriority.Background);
}
