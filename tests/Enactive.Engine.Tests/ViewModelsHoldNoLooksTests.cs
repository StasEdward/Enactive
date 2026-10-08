namespace Enactive.Engine.Tests;

using System.Text.RegularExpressions;
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
/// step cards had no test. These keep the rule one rule and ask the step cards what moved into them; the palette's
/// tables, which need Avalonia, are asked in the window's own tests (Enactive.App.Ui.Tests).</para>
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

    /// <summary>
    /// The engine's tests build without Avalonia: what needs it is asked in the window's own tests. The palette's tables
    /// pulled the package in here once, which is how the next test with a brush in it would quietly do it again.
    /// </summary>
    [Fact]
    public void The_engine_tests_build_without_avalonia()
    {
        var project = File.ReadAllText(Path.Combine(TestRepository.Root, "tests", "Enactive.Engine.Tests", "Enactive.Engine.Tests.csproj"));

        Assert.DoesNotMatch(@"PackageReference\s+Include=""Avalonia", project);
    }

    // ── a run's timeline ───────────────────────────────────────────────────

    /// <summary>
    /// A line of a run's timeline reads as its kind says - read as an EventKind, not compared with names spelled out.
    /// The folded reply is quiet; a kind an older build recorded, which this one does not know, is plain.
    /// </summary>
    [Theory]
    [InlineData("TaskFailed", nameof(TimelineTone.Failed))]
    [InlineData("ErrorObserved", nameof(TimelineTone.Failed))]
    [InlineData("ReviewFailed", nameof(TimelineTone.Refused))]
    [InlineData("TaskCompleted", nameof(TimelineTone.Succeeded))]
    [InlineData("ToolResult", nameof(TimelineTone.Quiet))]
    [InlineData(TimelineTones.AssistantNote, nameof(TimelineTone.Quiet))]
    [InlineData("StepStarted", nameof(TimelineTone.Plain))]
    [InlineData("AKindFromAnOlderBuild", nameof(TimelineTone.Plain))]
    public void A_timeline_line_reads_as_its_kind_says(string kind, string tone)
        => Assert.Equal(tone, TimelineTones.Of(kind).ToString());

    /// <summary>
    /// The palette spells no event kind out. Its timeline table compared the recorded kind with "ErrorObserved",
    /// "ReviewFailed" and the rest, so a kind renamed in Core would have gone plain there without a word.
    /// </summary>
    [Fact]
    public void The_palette_spells_no_event_kind_out()
    {
        var palette = File.ReadAllText(Path.Combine(TestRepository.Root, "src", "Enactive.App.Ui", "Palette.cs"));

        var spelled = Enum.GetNames<EventKind>().Where(name => palette.Contains($"\"{name}\"", StringComparison.Ordinal)).ToArray();
        Assert.True(spelled.Length == 0, "Event kinds spelled out in Palette.cs: " + string.Join(", ", spelled));
    }

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
