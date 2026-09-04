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

    // Deep variants — for a filled pill or button carrying WHITE text, where the
    // bright variant above would not reach 4.5:1.
    public static readonly Color SuccessDeep = Color.Parse("#0B7A55");
    public static readonly Color InfoDeep = Color.Parse("#1D4FD8");

    // ── Surfaces ────────────────────────────────────────────────────────────
    public static readonly IBrush Bg = Of(Ink900);
    public static readonly IBrush Surface = Of(Ink800);
    public static readonly IBrush Card = Of(Ink700);
    public static readonly IBrush Line = Of(Ink600);
    public static readonly IBrush LineStrong = Of(Ink500);

    /// <summary>Translucent lift for a card sitting on an unknown background.</summary>
    public static readonly IBrush CardFill = new ImmutableSolidColorBrush(Colors.White, 0.10);
    /// <summary>Translucent shade for an inset well (diff bodies, code blocks).</summary>
    public static readonly IBrush Scrim = new ImmutableSolidColorBrush(Colors.Black, 0.13);

    // ── Text ────────────────────────────────────────────────────────────────
    public static readonly IBrush Text = Of(Ink50);          // primary
    public static readonly IBrush TextBody = Of(Ink200);     // long form
    public static readonly IBrush TextMuted = Of(Ink300);    // secondary
    public static readonly IBrush TextFaint = Of(Ink400);    // non-essential only

    // ── Accent + semantic brushes ───────────────────────────────────────────
    public static readonly IBrush Accent = Of(Ember500);
    public static readonly IBrush AccentSoft = Of(Ember300);
    public static readonly IBrush Amber = Of(Amber400);
    public static readonly IBrush Success = Of(SuccessColor);
    public static readonly IBrush Warning = Of(WarningColor);
    public static readonly IBrush Danger = Of(DangerColor);
    public static readonly IBrush Info = Of(InfoColor);

    // ── Plan step status ────────────────────────────────────────────────────
    public static readonly IBrush StepPending = TextFaint;
    public static readonly IBrush StepRunning = Accent;
    public static readonly IBrush StepDone = Success;
    public static readonly IBrush StepSkipped = LineStrong;
    public static readonly IBrush StepFailed = Danger;

    // ── Autonomy tiers. Warmth escalates with how much rope the agent has. ──
    public static readonly IBrush AutonomyObserve = TextMuted;
    public static readonly IBrush AutonomySuggest = Info;
    public static readonly IBrush AutonomyExecute = Accent;
    public static readonly IBrush AutonomyAutonomous = Amber;

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
    public static readonly IBrush PillCoder = Of(SuccessDeep);
    public static readonly IBrush PillReasoner = Of(InfoDeep);

    private static IBrush Of(Color c) => new ImmutableSolidColorBrush(c);
}
