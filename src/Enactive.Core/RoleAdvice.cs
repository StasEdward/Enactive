namespace Enactive.Core.Guidance;

/// <summary>
/// What the two model fields on a ROLE decide, for somebody who has just seen the same models
/// offered again under AI · Phases and reasonably asked which of the two screens wins.
///
/// <para><b>The question, in the words it was asked in:</b> <i>"in the roles section a model is
/// chosen for each role, and then the same again in phases. It looks like configuring it twice.
/// What do we choose a model per role for? It works by phases anyway."</i> It does not, and
/// nothing on either screen said so — the role's model is the answer for NORMAL steps, which is
/// most of them, and the two Execute phases are optional overrides for the ends of the scale.
/// <c>ModelRouter.ResolveExecute</c> is four lines and says it exactly:
/// <c>Trivial =&gt; light ?? base, Complex =&gt; heavy ?? base, _ =&gt; base</c>.</para>
///
/// <para>In Core rather than in the window for the reason <see cref="PhaseAdvice"/> gives:
/// <c>Enactive.App.Ui</c> is a WinExe no test project references, so a sentence written in a
/// <c>.axaml</c> is a sentence nothing can check.</para>
/// </summary>
public static class RoleAdvice
{
    public const string Model =
        "This role's own model — and the one that runs most of its work.\n\n"
        + "The planner rates every step trivial / normal / complex. NORMAL steps run here, and "
        + "normal is the planner's default: unless a step is obviously mechanical or obviously the "
        + "hard part of the run, this is the model that does it.\n\n"
        + "AI · Phases does not replace this. Plan and Review are separate jobs with their own "
        + "models; the two Execute bindings only catch the ends of the scale, and only if you set "
        + "them. On (none) — which is the default — trivial and complex steps run here too, and "
        + "this field is the whole answer.\n\n"
        + "The Execute bindings are also GLOBAL: they override every role, not just this one. So a "
        + "light model bound there will take the trivial steps of this role as well, whatever is "
        + "chosen here.\n\n"
        // Said here because (none) on this field reads as "this role will not run", and the role
        // runs all the same: EngineComposition gives a role without a model the first one it
        // finds (FallbackModel). Nothing on the screen said so, and a person who left the field
        // on (none) to keep a role idle found it working on a model they had not picked.
        + "(none) here means no model has been chosen for this role. It still runs — on the first "
        + "model of the first provider that has one, whichever that is on the day — so pick one "
        + "here if it matters which.";

    public const string Fallback =
        "Where this role goes when its own model cannot be reached.\n\n"
        + "Not a second opinion and not a cheaper option — a substitute for a provider that is "
        + "down, out of quota, or answering with an error. It is used mid-run, in the middle of a "
        + "step, so pick something that can finish the work rather than something that merely "
        + "replies.\n\n"
        + "A local model's natural fallback is a hosted one, because the failure it is covering is "
        + "usually the local runtime being busy or not started. A hosted model's fallback is worth "
        + "thinking about twice: if the reason it failed is the network, a second hosted model will "
        + "fail the same way.\n\n"
        + "(none) means a failure here ends the step, which is honest and sometimes what you want: "
        + "a run that quietly finished on a model you did not choose is harder to read than one "
        + "that stopped.";

    /// <summary>
    /// Every field this advises on, so a test can hold the set rather than guess it — the same
    /// bargain <see cref="SettingsAdvice.All"/> and <see cref="PhaseAdvice.Phases"/> make.
    /// </summary>
    public static readonly IReadOnlyList<(string Field, string Text)> All =
    [
        (nameof(Model), Model),
        (nameof(Fallback), Fallback)
    ];
}
