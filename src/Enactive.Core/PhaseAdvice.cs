namespace Enactive.Core.Guidance;

using Enactive.Core.Events;

/// <summary>
/// What to bind to each phase, in the words somebody choosing from a drop-down needs.
///
/// <para><b>Why this is not in the window.</b> A person opening AI · Phases is shown four
/// identical drop-downs over the same list of models and no reason to put one model in one and a
/// different model in another. The mechanics were already written there - "Execute runs on the
/// worker's own model", "the planner rates each step" - and the mechanics are not the question
/// being asked. The question is "which of MY models goes here", and nothing answered it.</para>
///
/// <para>Here rather than in <c>Enactive.App.Ui</c> for the reason <c>ScheduleWords</c> gives:
/// that project is a WinExe nothing references, so a string written there is a string no test can
/// reach. It is also the second consumer waiting to happen - a first-run wizard asking the same
/// question needs the same answer, and the worst outcome would be two answers.</para>
///
/// <para>Written as guidance, not as rules. None of this is enforced anywhere and it must not
/// read as though it were: a person who wants a local model planning is not doing anything the
/// engine will stop, and the text says what that costs rather than forbidding it.</para>
/// </summary>
public static class PhaseAdvice
{
    /// <summary>
    /// The two auto-routing phases are not <see cref="WorkEventPayload.WorkPurpose"/> values - they
    /// are both "execute" to the engine, and differ only in which step is sent where. Named here so
    /// a caller asks for one by a constant rather than by a string it typed.
    /// </summary>
    public const string ExecuteLight = "execute-light";

    /// <summary>See <see cref="ExecuteLight"/>.</summary>
    public const string ExecuteHeavy = "execute-heavy";

    /// <summary>
    /// The advice for a phase, or null when nothing is written for it. Null rather than an empty
    /// string, so a caller that shows a hint icon can leave it out entirely instead of offering a
    /// question mark that answers nothing.
    /// </summary>
    public static string? For(string phase) => phase switch
    {
        WorkEventPayload.WorkPurpose.Plan => Plan,
        WorkEventPayload.WorkPurpose.Review => Review,
        ExecuteLight => Light,
        ExecuteHeavy => Heavy,
        _ => null
    };

    /// <summary>Every phase this advises on, so a test can hold the set rather than guess it.</summary>
    public static readonly IReadOnlyList<string> Phases =
    [
        WorkEventPayload.WorkPurpose.Plan,
        WorkEventPayload.WorkPurpose.Review,
        ExecuteLight,
        ExecuteHeavy
    ];

    public const string Plan =
        "Put your best model here.\n\n"
        + "Planning happens once per run and produces the step list everything else follows. A weak "
        + "plan is not a slow run — it is the wrong work done well: steps in an order that cannot "
        + "hold, or one step where there should have been four. Nothing downstream can recover from "
        + "it, because the reviewer checks whether a step did what the plan asked, not whether the "
        + "plan was worth asking.\n\n"
        + "It is also the cheapest call in the run: one turn, a few hundred tokens. A strong cloud "
        + "model here costs almost nothing and decides everything.\n\n"
        + "(none) plans on the executing model — reasonable when the worker is already a strong "
        + "model and you would rather not add a second provider.";

    public const string Review =
        "A strong model, or none at all.\n\n"
        + "The reviewer is the only thing standing between \"the model said it worked\" and \"it "
        + "worked\". It reads the step's report and the evidence and decides whether the two agree — "
        + "which is a harder job than doing the step, and a model that is not up to it fails in the "
        + "worst direction: it waves work through, and the run finishes green over nothing.\n\n"
        + "It is the phase most worth spending on and the one people cut first, because a reviewer "
        + "that passes everything looks exactly like a reviewer that is not needed.\n\n"
        + "(none) switches review off entirely — single-agent, faster, cheaper, and the run's "
        + "verdict is then the worker's own opinion of itself. Fine for throwaway work; not for "
        + "anything scheduled to run while you are asleep.\n\n"
        + "This is not the \"reviewer\" ROLE under AI · Team. That role is a worker like any other, "
        + "with read-only tools, for when you want to run a task that only looks. Its own model "
        + "field applies when a run is started as that role, and has nothing to do with this "
        + "binding — the reviewer of every step is chosen here, whichever role did the work.";

    public const string Light =
        "A local model is fine here. Cheap and dim is the point. 😉\n\n"
        + "Per-step auto-routing is optional: the planner rates each step trivial / normal / "
        + "complex. Trivial steps come here, complex ones go to the heavy model, and normal steps "
        + "stay on the worker's own model. Leave both on (none) and no routing happens at all.\n\n"
        + "Trivial steps are the ones the planner rated as needing no judgement: read this file, "
        + "list that folder, copy one thing to another. There is nothing to be clever about, and "
        + "sending them to a cloud model is paying reasoning prices for typing.\n\n"
        + "This is where a 7B running on your own machine earns its keep — it is not doing the "
        + "thinking, it is doing the fetching.\n\n"
        + "(none) leaves trivial steps on the worker's own model, which is the right choice if you "
        + "have no local runtime or your worker is already cheap.";

    public const string Heavy =
        "Your strongest model, and expect it to be used rarely.\n\n"
        + "Complex steps are the ones the planner flagged as the hard part of the run: the change "
        + "with consequences, the diagnosis, the step everything after it depends on. Those are "
        + "worth the expensive model precisely because there are few of them.\n\n"
        + "Leaving this on (none) is not a failure — it means complex steps stay on the worker's own "
        + "model, which is what you want when the worker is already strong. Binding a WEAKER model "
        + "here than the worker is the mistake worth avoiding: it would send the hardest steps of "
        + "the run to the least able model in it.";
}
