namespace Enactive.Core.Guidance;

/// <summary>
/// What every switch on AI · General actually does, in the words of whoever has to decide.
///
/// <para><b>Relocated, not rewritten.</b> Every string below was already in
/// <c>SettingsWindow.axaml</c> as a grey paragraph under its control, and each is here as it was
/// written. Moving them changes where they are READ - a "?" beside the label, opened when somebody
/// wants it - and where they can be CHECKED. It does not change what the app says, and a
/// relocation that quietly improved the prose on the way would be a change nobody reviewed.</para>
///
/// <para><b>Why the screen needed it.</b> Fourteen paragraphs of explanation, stacked, made a
/// settings page that had to be read end to end to be used at all: the control somebody came for
/// was four scrolls down, behind reasoning about controls they were not changing. The text is good
/// and it is why these settings are understandable; it just does not all need to be on screen at
/// once.</para>
///
/// <para>In Core rather than in the window for the reason <see cref="PhaseAdvice"/> gives:
/// <c>Enactive.App.Ui</c> is a WinExe no test project references, so a sentence in a .axaml is a
/// sentence nothing can check.</para>
/// </summary>
public static class SettingsAdvice
{
    public const string NumCtx =
        "How much context to load Ollama models with. Bigger costs VRAM and can spill onto the CPU "
        + "(check `ollama ps`). Leave blank to not override it.";

    public const string GlobalInstructions =
        "Applied to every run and appended to every worker's instructions.";

    public const string DisableThinking =
        "Reasoning models like qwen3 can spend a whole turn in <think> and return nothing. "
        + "Disabling it makes them answer (and call tools) directly. Only affects Ollama.";

    public const string VerifyWrites =
        "Reading a file back after writing catches a weak model inventing content. It's an extra "
        + "call per write, so turn it off when you mostly run strong models.";

    public const string ReviewContent =
        "A step that only writes a document has no exit code to check, so the ordinary review has "
        + "nothing to look at and passes anything. With this on, the reviewer reads the text "
        + "instead and fails invented package names, made-up command syntax, wrong ports and "
        + "corrupted identifiers. Needs a Review model bound under AI · Phases; costs one reviewer "
        + "call on the written text.";

    public const string CheckSoundness =
        "The reviewer checks whether a step's report is TRUE. A report can be true in every "
        + "particular while its conclusion follows from none of it — a fix reported over a test "
        + "that was already failing, and stayed failing, passes honestly and the run finishes "
        + "green.\n\n"
        + "This asks a second question: which calls SHOW the objective was met? The answer is a "
        + "list of call numbers, and the engine looks them up rather than believing them — a call "
        + "that was never made, or one that failed, does not prove anything. A step whose work was "
        + "reading, analysing or writing has nothing to cite and is never held to it.\n\n"
        + "Needs a Review model; costs one more reviewer call per step that ran something.";

    public const string RevertRejectedSteps =
        "When the reviewer rejects a step for good, undo what it wrote. Otherwise the run says "
        + "Failed while the rejected version stays in your workspace — which is the one you would "
        + "open next. A file you changed after the step wrote it is left alone and named in the log.";

    public const string ReviewRetries =
        "How many times a rejected step may be redone before the run gives up. 1 means two tries in "
        + "total. Each retry costs another run of the worker AND another reviewer call, so this is "
        + "the dial between using the reviewer's feedback and letting a weak model argue with an "
        + "expensive one. 0–5.";

    public const string AllowImplicitToolCalls =
        "Normally a JSON block in a reply executes nothing and the model is asked to send a real "
        + "tool call. Turning this on executes it — which means an example, a quote or a file the "
        + "agent just read can become an action. Only for a local model that cannot emit structured "
        + "tool calls.";

    public const string MaxParallelSteps =
        "How many independent steps of a plan may run at once. 1 keeps one step at a time on a "
        + "single conversation. Above 1 each concurrent step gets its own forked conversation and "
        + "sees only what its siblings concluded — faster on wide plans, and worth it mainly when "
        + "steps route to different providers, since two steps on one Ollama queue on the GPU.";

    public const string EvidenceBudget =
        "How many characters of tool output the reviewer sees, shared between every call the step "
        + "made. It is divided, not per call: three reads get a usable slice each, thirteen get "
        + "about 320 characters each — too little to check anything quoted from a source file. "
        + "Raise it for work that reads a lot; it is paid on every review call.";

    public const string ShellCommands =
        "run_command and run_powershell hand a command line to the operating system. The workspace "
        + "is where it starts and nothing more: every other tool asks for one named action against "
        + "a path this app resolves and checks, a shell can cd anywhere the account can reach.\n\n"
        + "Decided here rather than by the autonomy slider, so raising that for the file tools does "
        + "not raise it for command execution. An approval for a shell is never remembered beyond "
        + "the session.";

    public const string LogRetentionDays =
        "One log file per day, in %APPDATA%\\Enactive\\logs. Older files are deleted when the log "
        + "rolls over or when you lower this, and the new day's file records what it removed. "
        + "0 keeps everything — which is what the app did before this setting existed.";

    public const string LogPromptBodies =
        "On by default: the prompt bodies are what make a log worth reading when a run goes wrong, "
        + "and they are also most of its size — a single documentation run has exported at three "
        + "megabytes. Turning this off keeps the record that every call happened, and the model's "
        + "answers, and leaves out what was sent.";

    /// <summary>
    /// Every piece of advice on this screen, for the checks that hold for all of them.
    ///
    /// <para>A list rather than reflection over the class: a constant added without being put here
    /// is a constant nothing checks, and that should be a deliberate omission somebody can see in
    /// a diff rather than something reflection papers over.</para>
    /// </summary>
    public static readonly IReadOnlyList<(string Setting, string Text)> All =
    [
        (nameof(NumCtx), NumCtx),
        (nameof(GlobalInstructions), GlobalInstructions),
        (nameof(DisableThinking), DisableThinking),
        (nameof(VerifyWrites), VerifyWrites),
        (nameof(ReviewContent), ReviewContent),
        (nameof(CheckSoundness), CheckSoundness),
        (nameof(RevertRejectedSteps), RevertRejectedSteps),
        (nameof(ReviewRetries), ReviewRetries),
        (nameof(AllowImplicitToolCalls), AllowImplicitToolCalls),
        (nameof(MaxParallelSteps), MaxParallelSteps),
        (nameof(EvidenceBudget), EvidenceBudget),
        (nameof(ShellCommands), ShellCommands),
        (nameof(LogRetentionDays), LogRetentionDays),
        (nameof(LogPromptBodies), LogPromptBodies)
    ];
}
