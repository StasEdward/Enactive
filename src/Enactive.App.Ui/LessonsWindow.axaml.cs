using Avalonia.Controls;
using Enactive.Application;

namespace Enactive.App.Ui;

/// <summary>A workspace's lessons and rules, opened from its card in the main window (Plan Phase 5.2, 5.5).</summary>
public sealed partial class LessonsWindow : Window
{
    public LessonsWindow() => InitializeComponent();

    internal LessonsWindow(LessonsViewModel lessons) : this()
    {
        Title = lessons.Heading;
        List.DataContext = lessons;
        // Read again when it comes forward: a run that ended behind it may have proposed more.
        Activated += (_, _) => _ = lessons.RefreshAsync();
    }
}
