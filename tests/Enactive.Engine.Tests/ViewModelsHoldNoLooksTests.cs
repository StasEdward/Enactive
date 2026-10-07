namespace Enactive.Engine.Tests;

using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Enactive.App.Ui;
using Enactive.App.Ui.ViewModels;
using Enactive.Core.Events;
using Enactive.Core.History;
using Xunit;

/// <summary>
/// A view model says what is the case; the view picks how it looks (Palette).
///
/// <para>Until 2026-10-08 fourteen view models held brushes, a font weight and a margin, and the same table - a
/// run's status to a colour, an Inbox item's kind to a colour - was written two and three times in them. Holding
/// Avalonia's types also kept them out of this project, which builds the window's Avalonia-free files only: the
/// step cards had no test. These keep the rule one rule, ask the step cards what moved into them, and ask the
/// tables what a rule in a view model could not be asked - whether every state has a colour.</para>
/// </summary>
public sealed class ViewModelsHoldNoLooksTests
{
    // ── the rule ────────────────────────────────────────────────────────────

    /// <summary>
    /// No view model holds a look: nothing of Avalonia's in the view models' sources, but the dispatcher - which is
    /// how a view model is told something on the UI thread, not how anything looks.
    /// </summary>
    [Fact]
    public void No_view_model_holds_a_look()
    {
        var folder = Path.Combine(TestRepository.Root, "src", "Enactive.App.Ui", "ViewModels");
        var avalonia = new Regex(@"\bAvalonia\b(?!\.Threading\b)");

        var looks = Directory.EnumerateFiles(folder, "*.cs")
            .SelectMany(file => File.ReadLines(file).Select((line, i) => (file, line, i)))
            .Where(l => !l.line.TrimStart().StartsWith("//", StringComparison.Ordinal) && avalonia.IsMatch(l.line))
            .Select(l => $"{Path.GetFileName(l.file)}:{l.i + 1}: {l.line.Trim()}")
            .ToArray();

        Assert.True(looks.Length == 0, "Looks in view models:\n" + string.Join("\n", looks));
    }

    // ── the tables ──────────────────────────────────────────────────────────

    private static object? Pick(IValueConverter table, object? value)
        => table.Convert(value, typeof(object), null, CultureInfo.InvariantCulture);

    public static TheoryData<string, Type> Tables => new()
    {
        { nameof(Palette.CardTone), typeof(CardTone) },
        { nameof(Palette.EntryIcon), typeof(FeedEntryKind) },
        { nameof(Palette.Agent), typeof(AgentKind) },
        { nameof(Palette.Change), typeof(ChangeState) },
        { nameof(Palette.DiffLine), typeof(DiffLineKind) },
        { nameof(Palette.WorkspaceEdge), typeof(WorkspacePlace) },
        { nameof(Palette.Reach), typeof(ModelReach) },
        { nameof(Palette.Health), typeof(Enactive.Providers.ProviderHealth) },
        { nameof(Palette.Template), typeof(TemplateEdge) },
        { nameof(Palette.Schedule), typeof(ScheduleState) },
        { nameof(Palette.LogLevel), typeof(Enactive.Core.Diagnostics.LogLevel) },
    };

    /// <summary>
    /// Every value of every state a colour is chosen for has one. The tables name each value and throw on one they do
    /// not know, so a state added later is a failure here rather than a card quietly drawn in a fallback colour.
    /// </summary>
    [Theory]
    [MemberData(nameof(Tables))]
    public void Every_state_has_a_colour(string table, Type states)
    {
        var converter = (IValueConverter)typeof(Palette).GetField(table)!.GetValue(null)!;

        Assert.All(Enum.GetValues(states).Cast<object>(), state => Assert.IsAssignableFrom<IBrush>(Pick(converter, state)));
    }

    /// <summary>A card waiting for a person, or blocked, is amber: nothing went wrong in it.</summary>
    [Fact]
    public void A_card_that_needs_you_is_amber()
        => Assert.Same(Brand.Warning, Pick(Palette.CardTone, CardTone.NeedsYou));

    /// <summary>A run's status is coloured by the kind of ending Core says it is - the same table wherever it is shown.</summary>
    [Theory]
    [InlineData("Completed", "Success")]
    [InlineData("Failed", "Danger")]
    [InlineData("NeedsUser", "Amber")]
    public void A_run_s_edge_is_the_colour_of_its_ending(string status, string brand)
        => Assert.Same(typeof(Brand).GetField(brand)!.GetValue(null), Pick(Palette.RunEdge, status));

    // ── what moved into the step cards ──────────────────────────────────────

    private static readonly Guid Run = Guid.NewGuid();

    private static WorkEvent Ev(EventKind kind, string summary, string? payload = null)
        => new(Guid.NewGuid(), Guid.NewGuid(), Run, DateTimeOffset.UtcNow, kind, summary, payload);

    private static WorkEvent[] OneStep(params WorkEvent[] then)
        => [Ev(EventKind.PlanCreated, "Disks — 1 steps: check", WorkEventPayload.PlanPayload("Disks", ["check"])),
            Ev(EventKind.StepStarted, "[1/1] check", "{\"step\":1}"), .. then];

    private static CardTone Live(WorkEvent[] events)
    {
        var feed = new RunFeed();
        foreach (var ev in events)
            feed.Apply(ev);
        return new StepCardViewModel(feed.Cards[0]).Tone;
    }

    /// <summary>A question waiting for a person outranks the card's status: the step has not failed, it is waiting.</summary>
    [Fact]
    public void A_card_waiting_for_an_answer_needs_you_whatever_it_was_doing()
        => Assert.Equal(CardTone.NeedsYou, Live(OneStep(Ev(EventKind.DecisionRequested, "Approve tool 'send_email'?", "{\"step\":1}"))));

    [Fact]
    public void A_blocked_card_needs_you()
        => Assert.Equal(CardTone.NeedsYou, Live(OneStep(Ev(EventKind.StepCompleted, "[1/1] check",
            WorkEventPayload.StepPayload(1, StepOutcomeKind.Blocked, "the share is offline")))));

    [Fact]
    public void A_running_card_is_running_and_a_done_one_done()
    {
        Assert.Equal(CardTone.Running, Live(OneStep()));
        Assert.Equal(CardTone.Done, Live(OneStep(Ev(EventKind.StepCompleted, "[1/1] check",
            WorkEventPayload.StepPayload(1, StepOutcomeKind.Succeeded)))));
    }

    /// <summary>A run reopened from the history wears the same tones it wore live: one fold, one view of it.</summary>
    [Fact]
    public void A_run_reopened_wears_the_tones_it_wore_live()
    {
        var events = OneStep(Ev(EventKind.StepCompleted, "[1/1] check", WorkEventPayload.StepPayload(1, StepOutcomeKind.Failed, "C: is full")));
        var record = new RunRecord(Run, Guid.NewGuid(), "check", "qwen", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Failed",
            events.Select(e => new RunEventRecord(e.At, e.Kind.ToString(), e.Summary, e.StepNo(), e.PayloadJson)).ToArray(), [], []);

        var reopened = new StepCardViewModel(RunFeed.Replay(record).Cards[0]).Tone;

        Assert.Equal((CardTone.Failed, CardTone.Failed), (Live(events), reopened));
    }
}
