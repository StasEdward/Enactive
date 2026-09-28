using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace Enactive.App.Ui;

/// <summary>
/// The Enactive brand palette — the C# mirror of <c>brand/brand.css</c> and the single source
/// of colour for this UI. Never write a hex literal in a control; add a token here instead,
/// and keep the two files in step.
///
/// The one rule that makes the palette work: <b>Ember means ACTION</b> — a running step, the
/// primary button, the artifact. It is never used for a destructive action; delete, reject and
/// failure use <see cref="Danger"/>. Scatter ember decoratively and the UI loses its ability to
/// say "look here, something is happening".
/// </summary>
internal static class Brand
{
    // ── Ink — the environment. Nine-tenths of every surface. ────────────────
    public static readonly Color Ink950 = Color.Parse("#0A0B0D");
    public static readonly Color Ink900 = Color.Parse("#101216");
    public static readonly Color Ink800 = Color.Parse("#171A20");
    public static readonly Color Ink700 = Color.Parse("#21252E");
    public static readonly Color Ink600 = Color.Parse("#2E3440");
    public static readonly Color Ink500 = Color.Parse("#414957");
    public static readonly Color Ink400 = Color.Parse("#5C6575");
    public static readonly Color Ink300 = Color.Parse("#8A93A3");
    public static readonly Color Ink200 = Color.Parse("#B9C0CC");
    public static readonly Color Ink100 = Color.Parse("#DDE2E9");
    public static readonly Color Ink50 = Color.Parse("#F2F4F7");

    // ── Ember — the accent. Action, and nothing else. ───────────────────────
    public static readonly Color Ember200 = Color.Parse("#FFC0A4");
    public static readonly Color Ember300 = Color.Parse("#FF9C72");   // ember text on dark
    public static readonly Color Ember400 = Color.Parse("#FF7C48");
    public static readonly Color Ember500 = Color.Parse("#FF5F26");   // PRIMARY
    public static readonly Color Ember600 = Color.Parse("#E84A14");   // pressed
    public static readonly Color Ember700 = Color.Parse("#BC380F");   // ember text on light
    public static readonly Color Ember800 = Color.Parse("#8E2B0C");
    public static readonly Color Amber400 = Color.Parse("#FFB020");   // secondary / caution

    // ── Semantic ────────────────────────────────────────────────────────────
    public static readonly Color SuccessColor = Color.Parse("#34D399");
    public static readonly Color WarningColor = Color.Parse("#FBBF24");
    public static readonly Color DangerColor = Color.Parse("#FF5D7A");
    public static readonly Color InfoColor = Color.Parse("#4C8DFF");
    public static readonly Color InfoLight = Color.Parse("#94B8FF");   // info text on dark

    // Deep variants — for a filled pill or button carrying WHITE text, where the
    // bright variant above would not reach 4.5:1.
    public static readonly Color SuccessDeep = Color.Parse("#0B7A55");
    public static readonly Color InfoDeep = Color.Parse("#1D4FD8");

    // ── Go ──────────────────────────────────────────────────────────────────
    // Starting work has its own colour. Ember stays the app's accent - selection, focus, the
    // running step, the Allow button - and the buttons that START a run are green, so the thing
    // you press to begin is not the same colour as everything else asking for attention.
    //
    // The base IS SuccessDeep, one hex under two names on purpose: green already means "this
    // finished well" on a run card, and the button green is the same family a shade heavier, so
    // the two read as related rather than as two unrelated greens fighting. It is the deep step
    // because the button carries WHITE text; #34D399 under white is unreadable.
    public static readonly Color Go500 = Color.Parse("#0F9C6C");   // hover — one step up
    public static readonly Color Go600 = SuccessDeep;              // PRIMARY fill
    public static readonly Color Go700 = Color.Parse("#075A3E");   // pressed

    // ── Surfaces ────────────────────────────────────────────────────────────
    public static readonly IBrush Bg = Of(Ink900);
    /// <summary>The icon rail: one step darker than the app, so it reads as chrome, not content.</summary>
    public static readonly IBrush Rail = Of(Ink950);
    public static readonly IBrush Surface = Of(Ink800);
    public static readonly IBrush Card = Of(Ink700);
    public static readonly IBrush Line = Of(Ink600);
    public static readonly IBrush LineStrong = Of(Ink500);

    /// <summary>Translucent lift for a card sitting on an unknown background.</summary>
    public static readonly IBrush CardFill = new ImmutableSolidColorBrush(Colors.White, 0.10);

    // Hover and selection on a card: MORE OF THE SAME LIGHT, not a different colour. A list of
    // cards that each carry a status edge cannot also change hue to say "you are here" - the two
    // would argue, and the status would lose.
    public static readonly IBrush CardFillHover = new ImmutableSolidColorBrush(Colors.White, 0.14);
    public static readonly IBrush CardFillActive = new ImmutableSolidColorBrush(Colors.White, 0.19);
    /// <summary>Translucent shade for an inset well (diff bodies, code blocks).</summary>
    public static readonly IBrush Scrim = new ImmutableSolidColorBrush(Colors.Black, 0.13);

    // A box you type into is a WELL, not a card: the same ink as whatever it sits on, a shade
    // deeper. CardFill is the opposite move - white over the ground - and using it on an input
    // made every field a pale grey block that belonged to no palette here. Hover comes up a
    // little; focus keeps the fill and takes ember on the border, because ember on an input
    // means "typing goes here" and nothing else.
    public static readonly IBrush InputFill = new ImmutableSolidColorBrush(Colors.Black, 0.22);
    public static readonly IBrush InputFillHover = new ImmutableSolidColorBrush(Colors.Black, 0.14);

    // ── Text ────────────────────────────────────────────────────────────────
    public static readonly IBrush Text = Of(Ink50);          // primary
    public static readonly IBrush TextBody = Of(Ink200);     // long form
    public static readonly IBrush TextMuted = Of(Ink300);    // secondary
    public static readonly IBrush TextFaint = Of(Ink400);    // non-essential only

    // ── Accent + semantic brushes ───────────────────────────────────────────
    public static readonly IBrush Accent = Of(Ember500);
    /// <summary>Ember at low opacity: a selected row is MARKED, not repainted. A solid ember
    /// block reads as "this is the action", which a list selection is not.</summary>
    public static readonly IBrush AccentFill = new ImmutableSolidColorBrush(Ember500, 0.16);
    public static readonly IBrush AccentHover = Of(Ember400);
    public static readonly IBrush AccentPressed = Of(Ember600);
    public static readonly IBrush AccentSoft = Of(Ember300);
    public static readonly IBrush Amber = Of(Amber400);

    /// <summary>The fill of a button that starts a run. White text on it — see the Go ramp.</summary>
    public static readonly IBrush Go = Of(Go600);
    public static readonly IBrush GoHover = Of(Go500);
    public static readonly IBrush GoPressed = Of(Go700);
    /// <summary>Go light enough to read as text or an edge on a dark ground: the outline peer.</summary>
    public static readonly IBrush GoSoft = Of(SuccessColor);

    public static readonly IBrush Success = Of(SuccessColor);
    public static readonly IBrush Warning = Of(WarningColor);
    public static readonly IBrush Danger = Of(DangerColor);
    public static readonly IBrush Info = Of(InfoColor);
    /// <summary>Info light enough to read on its own tint - see <see cref="PhaseText"/>.</summary>
    public static readonly IBrush InfoSoft = Of(InfoLight);

    /// <summary>
    /// "You are here": the selected tab, the current workspace. NOT an action.
    ///
    /// <para>These used to be Ember, which is the one thing the palette says at the top of this file
    /// that Ember must not be - a selection is not the action you are meant to take. It also read as
    /// a warning, which is what got it reported. Ember is now down to the primary button, the focus
    /// ring and caret, and the unread dot; being rarer is what lets it still mean something.</para>
    /// </summary>
    public static readonly IBrush Current = Info;

    // ── Plan step status ────────────────────────────────────────────────────
    public static readonly IBrush StepPending = TextFaint;

    // Info, not Ember. Two reasons, and the second is the one that decided it.
    //
    // The palette's own rule says Ember means the action YOU are meant to take, never a level of
    // danger and never a block of status - and a running step is neither. But the practical
    // complaint came first: next to the red of a failed step, Ember at this size reads as a milder
    // red, so a list of steps looked like a list of problems while it was simply working. Blue is
    // already the palette's "in progress, nothing is wrong" (it is Suggest on the autonomy scale)
    // and it cannot be mistaken for the failure colour at a glance.
    public static readonly IBrush StepRunning = Info;
    public static readonly IBrush StepDone = Success;
    public static readonly IBrush StepSkipped = LineStrong;
    public static readonly IBrush StepFailed = Danger;
    // Done but not confirmed: amber, like a step waiting on a person - not failed, not settled.
    public static readonly IBrush StepUnverified = Warning;

    // ── Autonomy tiers ──────────────────────────────────────────────────────
    // Read as a risk scale, because that is what it is: how much the agent may do
    // to the workspace without asking. Green through blue and yellow to red - the
    // reading every dashboard in the world has trained people on, so the colour
    // says "how much rope" before the words are read.
    //
    // Ember is deliberately NOT on this scale. Ember is the action you are meant
    // to take; a level of danger is not something to encourage.
    public static readonly IBrush AutonomyObserve = Success;      // watches, changes nothing
    public static readonly IBrush AutonomySuggest = Info;         // proposes, you apply
    public static readonly IBrush AutonomyExecute = Warning;      // edits freely, asks before commands
    public static readonly IBrush AutonomyAutonomous = Danger;    // runs commands unasked

    /// <summary>Colour for an autonomy slider position (0 = Observe … 3 = Autonomous).</summary>
    public static IBrush Autonomy(int level) => level switch
    {
        0 => AutonomyObserve,
        1 => AutonomySuggest,
        2 => AutonomyExecute,
        _ => AutonomyAutonomous
    };

    // ── A decision the user has to make: work is blocked, not running. ──────
    public static readonly IBrush DecisionFill = Of(Color.Parse("#2A1B05"));
    public static readonly IBrush DecisionBorder = Amber;

    // ── Agent pills (white text on a filled pill) ───────────────────────────
    // ── Status pills ────────────────────────────────────────────────────────
    // A pill states a fact; it is not an alarm. Solid saturated fills at this size read
    // as one - a running task painted in full Ember looked like something had gone
    // wrong. So a pill is a TINT with the colour carried by its text: enough to spot
    // across the window, not enough to shout.
    //
    // The tint went from Ember to Info for the same reason the step edge did: warm orange beside
    // the failure red says "problem" before the word on it is read, and a run in progress is not
    // one.
    public static readonly IBrush PillRunningFill = new ImmutableSolidColorBrush(InfoColor, 0.18);
    public static readonly IBrush PillDoneFill = new ImmutableSolidColorBrush(SuccessColor, 0.18);
    public static readonly IBrush PillFailedFill = new ImmutableSolidColorBrush(DangerColor, 0.18);
    public static readonly IBrush PillOpenFill = new ImmutableSolidColorBrush(Amber400, 0.18);
    public static readonly IBrush PillIdleFill = CardFill;

    /// <summary>The tint behind a phase or a stored status.</summary>
    public static IBrush PhaseFill(string phase) => Phase(phase) switch
    {
        PhaseKind.Done => PillDoneFill,
        PhaseKind.Failed => PillFailedFill,
        PhaseKind.Open => PillOpenFill,
        PhaseKind.Idle => PillIdleFill,
        _ => PillRunningFill
    };

    /// <summary>The word on the pill. The base colour is too dark on its own tint, so running
    /// takes <see cref="InfoLight"/> - the info blue made for text on dark.</summary>
    public static IBrush PhaseText(string phase) => Phase(phase) switch
    {
        PhaseKind.Done => Success,
        PhaseKind.Failed => Danger,
        PhaseKind.Open => Amber,
        PhaseKind.Idle => TextMuted,
        _ => InfoSoft
    };

    private enum PhaseKind { Running, Done, Failed, Open, Idle }

    private static PhaseKind Phase(string phase) => phase.ToLowerInvariant() switch
    {
        "completed" or "succeeded" or "ok" => PhaseKind.Done,
        "failed" or "error" => PhaseKind.Failed,
        // Never wrote a final status: the run stopped somewhere nobody watched.
        "incomplete" => PhaseKind.Open,
        "cancelled" or "canceled" or "idle" or "" => PhaseKind.Idle,
        _ => PhaseKind.Running
    };

    public static readonly IBrush PillCoder = Of(SuccessDeep);
    public static readonly IBrush PillReasoner = Of(InfoDeep);

    /// <summary>
    /// Publishes the palette into the application's resources, so a XAML style can reach exactly the
    /// tokens the C# side uses: <c>{DynamicResource Brand.Accent}</c> and friends. This is what keeps
    /// ONE definition of every colour - this file - with no hex literal in any .axaml.
    ///
    /// It also overrides Fluent's seven accent keys, out of which the theme builds every accented
    /// control: slider thumb, checkbox tick, focus ring, selection. Without them the stock theme
    /// speaks Windows blue in the middle of an Ember UI.
    /// </summary>
    /// <summary>Points a set of the theme's own resource keys at one of ours.</summary>
    private static void Fluent(IResourceDictionary resources, IBrush brush, params string[] keys)
    {
        foreach (var key in keys)
            resources[key] = brush;
    }

    public static void PublishTo(Application app)
    {
        var r = app.Resources;

        r["SystemAccentColor"] = Ember500;
        r["SystemAccentColorLight1"] = Ember400;
        r["SystemAccentColorLight2"] = Ember300;
        r["SystemAccentColorLight3"] = Ember200;
        r["SystemAccentColorDark1"] = Ember600;
        r["SystemAccentColorDark2"] = Ember700;
        r["SystemAccentColorDark3"] = Ember800;

        // Keys mirror the member names above, so a token is looked up in XAML by the same name it
        // has in code. Add a token here whenever you add one to the palette.
        r["Brand.Bg"] = Bg;
        r["Brand.Rail"] = Rail;
        r["Brand.Surface"] = Surface;
        r["Brand.Card"] = Card;
        r["Brand.CardFill"] = CardFill;
        r["Brand.CardFillHover"] = CardFillHover;
        r["Brand.CardFillActive"] = CardFillActive;
        r["Brand.Scrim"] = Scrim;
        r["Brand.InputFill"] = InputFill;
        r["Brand.InputFillHover"] = InputFillHover;
        r["Brand.Line"] = Line;
        r["Brand.LineStrong"] = LineStrong;

        r["Brand.Text"] = Text;
        r["Brand.TextBody"] = TextBody;
        r["Brand.TextMuted"] = TextMuted;
        r["Brand.TextFaint"] = TextFaint;

        r["Brand.Accent"] = Accent;
        r["Brand.AccentFill"] = AccentFill;
        r["Brand.PillRunningFill"] = PillRunningFill;
        r["Brand.PillDoneFill"] = PillDoneFill;
        r["Brand.PillFailedFill"] = PillFailedFill;
        r["Brand.PillOpenFill"] = PillOpenFill;
        r["Brand.AccentHover"] = AccentHover;
        r["Brand.AccentPressed"] = AccentPressed;
        r["Brand.AccentSoft"] = AccentSoft;
        r["Brand.Amber"] = Amber;
        r["Brand.Go"] = Go;
        r["Brand.GoHover"] = GoHover;
        r["Brand.GoPressed"] = GoPressed;
        r["Brand.GoSoft"] = GoSoft;
        r["Brand.Success"] = Success;
        r["Brand.Warning"] = Warning;
        r["Brand.Danger"] = Danger;
        r["Brand.Info"] = Info;
        r["Brand.Current"] = Current;

        r["Brand.StepPending"] = StepPending;
        r["Brand.StepRunning"] = StepRunning;
        r["Brand.StepDone"] = StepDone;
        r["Brand.StepSkipped"] = StepSkipped;
        r["Brand.StepFailed"] = StepFailed;

        r["Brand.DecisionFill"] = DecisionFill;
        r["Brand.DecisionBorder"] = DecisionBorder;
        r["Brand.PillCoder"] = PillCoder;

        // ── Fluent's own keys ────────────────────────────────────────────────
        // The theme sets some template values as LOCAL values through DynamicResource -
        // "Background={DynamicResource ComboBoxDropDownBackground}" written on the element
        // itself. A local value outranks any style setter, which is why four passes of
        // "ComboBox /template/ ..." and "PopupRoot > Border" changed nothing: there was no
        // selector that could win. The lookup is dynamic, though, so defining the key here
        // is the one thing that does.
        //
        // The names are Avalonia 12.1.1's own, read from the tagged source of
        // Themes.Fluent/Controls/ComboBox.xaml - not from an 11.x recipe.
        Fluent(r, InputFill, "ComboBoxBackground", "ComboBoxBackgroundUnfocused");

        // The dropdown is OPAQUE, and it is the one that has to be. InputFill is black at 22% -
        // fine for a field sunk into a page, and see-through for a panel that floats over one:
        // the text underneath read straight through it. Ink950 is the same colour that 22% lands
        // on over the app's ground, with nothing behind it to show.
        Fluent(r, Rail, "ComboBoxDropDownBackground");
        Fluent(r, InputFillHover,
            "ComboBoxBackgroundPointerOver", "ComboBoxBackgroundPressed");
        Fluent(r, Scrim, "ComboBoxBackgroundDisabled");
        Fluent(r, Line,
            "ComboBoxBorderBrush", "ComboBoxBackgroundBorderBrushUnfocused",
            "ComboBoxBorderBrushDisabled");
        Fluent(r, LineStrong,
            "ComboBoxBorderBrushPointerOver", "ComboBoxBorderBrushPressed",
            "ComboBoxDropDownBorderBrush");
        Fluent(r, Accent, "ComboBoxBackgroundBorderBrushFocused");
        Fluent(r, Text,
            "ComboBoxForeground", "ComboBoxForegroundFocused", "ComboBoxForegroundFocusedPressed");
        Fluent(r, TextMuted,
            "ComboBoxPlaceHolderForeground", "ComboBoxPlaceHolderForegroundFocusedPressed",
            "ComboBoxDropDownGlyphForeground", "ComboBoxDropDownGlyphForegroundFocused",
            "ComboBoxDropDownGlyphForegroundFocusedPressed");
        Fluent(r, TextFaint, "ComboBoxForegroundDisabled", "ComboBoxDropDownGlyphForegroundDisabled");
        r["Brand.PillReasoner"] = PillReasoner;
    }

    private static IBrush Of(Color c) => new ImmutableSolidColorBrush(c);
}
