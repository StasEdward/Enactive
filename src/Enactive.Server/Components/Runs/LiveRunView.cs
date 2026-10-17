namespace Enactive.Server.Components.Runs;

using Enactive.Application.Projections;
using Enactive.Core.History;

// What the live run's page shows of the run, copied out of the session's fold under its lock (RunSession.Read) - the
// page draws these and never the fold itself, which the run changes as it goes. Public records of plain values, as a
// component's parameters must be: the projections they are read from are internal to the service.

/// <summary>One line of a step card: what the step did, and its result once it has one.</summary>
public sealed record StepLine(FeedEntryKind Kind, string Label, string Detail);

/// <summary>
/// A declared check's result (Plan Phase 3.5), in the words the window's card shows it in (CheckLines): its verdict and that
/// verdict's colour (Looks.Check), which check and where it fired, why, and the workspace-relative file of its whole output.
/// </summary>
public sealed record CheckView(string Word, string Tone, string CheckId, string Where, string Reason, string? DetailPath)
{
    internal static CheckView Of(CheckLine line)
        => new(line.Word, Looks.Check(line.Verdict), line.CheckId, line.Where, line.Reason ?? "", line.DetailPath);

    internal static IReadOnlyList<CheckView> Of(IEnumerable<FeedCheck> checks) => CheckLines.Of(checks).Select(Of).ToList();
}

/// <summary>One step card, as the window's StepCardView draws it.</summary>
/// <param name="Checks">The declared checks that fired for the step: after its writes, at its end.</param>
public sealed record StepView(string Title, FeedCardStatus Status, bool WaitingForYou, string Activity, string Tally,
    IReadOnlyList<StepLine> Lines, IReadOnlyList<CheckView> Checks)
{
    /// <summary>A card of the fold, copied out: the live run's as it goes, a past run's as its record rebuilds it.</summary>
    internal static StepView Of(FeedCard card)
        => new(card.Title, card.Status, card.WaitingForYou, card.Activity, card.Tally,
            card.Entries.Select(e => new StepLine(e.Kind, e.Label, e.Detail)).ToList(), CheckView.Of(card.Checks));
}

/// <summary>A ROUTING row: which model served a phase.</summary>
public sealed record RoutingLine(string Phase, string Model);

/// <summary>A WHERE IT RAN row, its label in the colour of where it ran (Looks.Reach).</summary>
public sealed record WorkLine(string Label, string Detail, string Tone);

/// <summary>A MODEL PERFORMANCE row: the model's speed folded, every figure opened.</summary>
public sealed record SpeedLine(string Provider, string Model, string Speed, string SpeedLabel, string Times, string Tokens,
    string Cache, string First, string Requests, string Explanation);

/// <summary>
/// The run's files as its Artifacts tab shows them (RunFiles): those it wrote, those of them a rejected step put back, and
/// the changes it proposed instead of writing - each a different card, never one list of "files the run wrote".
/// </summary>
public sealed record RunFilesView(IReadOnlyList<string> Written, IReadOnlyList<string> PutBack, IReadOnlyList<string> Staged)
{
    public static readonly RunFilesView Empty = new([], [], []);

    /// <summary>The ARTIFACTS tile and tab: a card for each file written and each change proposed (RunPanel.Artifacts).</summary>
    public int Count => Written.Count + Staged.Count;
}

/// <summary>The run as its page shows it, at one event (<see cref="Seq"/>).</summary>
public sealed record LiveRunView(
    string Title,
    string? Phase,
    int StepsDone,
    int StepsTotal,
    IReadOnlyList<StepView> Steps,
    string CurrentAction,
    string AgentLabel,
    bool AgentVisible,
    bool AgentReviewing,
    int ToolCalls,
    RunFilesView Artifacts,
    int PromptTokens,
    int CompletionTokens,
    IReadOnlyList<RoutingLine> Routing,
    IReadOnlyList<WorkLine> Work,
    IReadOnlyList<SpeedLine> Speeds,
    long Seq)
{
    public static readonly LiveRunView Empty = new("", null, 0, 0, [], "", "", false, false, 0, RunFilesView.Empty, 0, 0, [], [], [], 0);

    /// <summary>The declared checks that fired for the run as a whole (Plan Phase 3.5): at a wave's end, its end, owed at apply.</summary>
    public IReadOnlyList<CheckView> RunChecks { get; init; } = [];

    /// <summary>The tile, as the window's (RunFigures).</summary>
    public string TokensText => RunFigures.Tokens(RunFigures.Reported(PromptTokens, CompletionTokens));

    public string TokensDetail => RunFigures.TokensDetail(RunFigures.Reported(PromptTokens, CompletionTokens));

    /// <summary>The steps line under the tiles, as the window's StatusProgress (RunFigures).</summary>
    public string Progress => RunFigures.Steps(StepsDone, StepsTotal);
}

/// <summary>
/// How often a live run's page moves its clock - ELAPSED, and how long a question still waits - while the run goes; null,
/// never. A service of its own so a test can stop it: a page that redrew every second anyway hid whether a change was
/// drawn because the page was told of it.
/// </summary>
internal sealed record LiveRunClock(TimeSpan? Every)
{
    public static readonly LiveRunClock EverySecond = new(TimeSpan.FromSeconds(1));
}

/// <summary>
/// Reads a run's fold into a <see cref="LiveRunView"/>. One per page: it keeps each card's copy until the card changes
/// (FeedCard.Version), so a run of a hundred steps is not copied line by line ten times a second for the one that moved.
/// Called only under the session's lock (RunSession.Read), which is also what keeps two reads of it from crossing.
/// </summary>
internal sealed class LiveRunReader
{
    private readonly Dictionary<FeedCard, (int Version, StepView View)> _cards = new(ReferenceEqualityComparer.Instance);

    public LiveRunView Read(RunFeed feed, RunActivity activity, RunPanel panel, long seq)
        => new(feed.Title, feed.Phase, feed.StepsDone, feed.StepsTotal,
            feed.Cards.Select(Card).ToList(),
            activity.CurrentAction, activity.AgentLabel, activity.AgentVisible, activity.Agent == AgentKind.Reasoner,
            activity.ToolCalls,
            new RunFilesView(panel.Files.Written.ToList(), panel.Files.Reverted, panel.Files.Staged.ToList()),
            panel.PromptTokens, panel.CompletionTokens,
            panel.Routing.Rows.Select(r => new RoutingLine(r.Phase, r.Model)).ToList(),
            panel.Work.Rows.Select(r => new WorkLine(r.Label, r.Detail, Looks.Reach(r.Reach))).ToList(),
            panel.Performance.Rows.Select(r => new SpeedLine(r.Provider, r.Model, r.Speed, r.SpeedLabel, r.Times, r.Tokens,
                r.Cache, r.First, r.Requests, r.Explanation)).ToList(),
            seq)
        {
            RunChecks = CheckView.Of(feed.RunChecks)
        };

    private StepView Card(FeedCard card)
    {
        if (_cards.TryGetValue(card, out var kept) && kept.Version == card.Version)
            return kept.View;
        var view = StepView.Of(card);
        _cards[card] = (card.Version, view);
        return view;
    }
}
