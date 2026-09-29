namespace Enactive.Core.Templates;

using System.Text.RegularExpressions;
using Enactive.Core.Permissions;

/// <summary>What kind of value a parameter takes. Drives validation and, later, the input control.</summary>
public enum TemplateParameterType { Text, MultilineText, Boolean, Integer, Choice, Path }

/// <summary>
/// One value the user supplies when starting a task from a template.
///
/// <para>The value is a STRING everywhere - here, in the resolved specification and in the stored
/// snapshot. An <c>object?</c> would look more precise and be less: it round-trips through JSON as a
/// <c>JsonElement</c>, so every consumer ends up re-parsing it anyway, and the parse failure then
/// happens deep in whoever touched it last. Typing happens once, in the validator, where the error
/// can name the parameter.</para>
/// </summary>
public sealed record TemplateParameter(
    string Id,
    string Name,
    TemplateParameterType Type,
    bool Required = true,
    string? Default = null,
    IReadOnlyList<string>? Choices = null,
    string? Description = null);

/// <summary>
/// A check that decides whether the work is actually done, rather than whether the model said it
/// was. Defined here; executed in M2.
/// </summary>
/// <param name="ExpectedExitCode">The exit code that counts as a pass. Anything else fails.</param>
/// <param name="Required">
/// A failed required criterion keeps the run out of Completed. An optional one is reported and
/// changes nothing, which is what a "nice to know" check is for.
/// </param>
public sealed record SuccessCriterionDefinition(
    string Name,
    string Command,
    int ExpectedExitCode = 0,
    bool Required = true,
    CriterionOrigin Origin = CriterionOrigin.Declared,
    bool AlreadyPassing = false)
{
    /// <summary>Verbatim request passage identifying an explicitly requested verification.
    /// Null for template criteria and model suggestions. This is provenance, not shell parsing.</summary>
    public string? RequestQuote { get; init; }
    /// <summary>Planner's explanation of final-check suitability and compliance with request constraints.</summary>
    public string? PlanningReason { get; init; }

    /// <summary>
    /// What this criterion checks, as a TYPE the engine understands rather than a command it runs
    /// (Phase 3). Null for a command check. <see cref="Command"/> then holds a readable form of it,
    /// for the report; the engine does not run it.
    /// </summary>
    public TypedCriterion? Typed { get; init; }

    /// <summary>
    /// The plan position (0-based) of the step the planner attached this criterion to, or null for one stated for
    /// the whole run. A file criterion of a step is checked when that step ends, not only when the run does.
    /// </summary>
    public int? Step { get; init; }
}

/// <summary>The kinds of criterion a planner may state as a type (Phase 3). Each is one the engine can decide on its own.</summary>
public enum TypedCriterionKind
{
    /// <summary>A file is there - and, unless said otherwise, not empty. Decided by the engine, no shell.</summary>
    FileExists,

    /// <summary>A file holds a given text. Decided by the engine, no shell.</summary>
    FileContains,

    /// <summary>The workspace's tests pass - through the ecosystem that recognises it, as a command check.</summary>
    TestsPass,

    /// <summary>
    /// Every item one step named has a result from another step, backed by complete evidence of the
    /// required kind (Phase 5.1). Decided by the engine from the run's step outputs - not from any
    /// step's account of how much it covered.
    /// </summary>
    EvidenceCoversAll,

    /// <summary>
    /// What only judgement can decide about a step's result - that a report says what the work found, that an
    /// explanation is right (Phase 1.4, 5.2). Judged by the reviewer and by nothing else, criterion by criterion, each
    /// verdict citing evidence of the kinds the criterion allows; never run, and never checked at the end of the run.
    /// </summary>
    Semantic
}

/// <summary>A planner-stated criterion, once validated: its kind and what it is about.</summary>
public sealed record TypedCriterion(
    TypedCriterionKind Kind,
    string? Path = null,
    bool NonEmpty = true,
    string? Text = null,
    string? Target = null,
    // EvidenceCoversAll: which step's field lists the items, which step's field holds a result per
    // item, and what kind of evidence each result must be backed by. Steps are 0-based plan indices.
    int? SourceStep = null,
    string? SourceField = null,
    int? ResultsStep = null,
    string? ResultsField = null,
    Enactive.Core.Tasks.EvidenceKind? Evidence = null,
    // A file criterion whose file is the one a step HANDED ON (a path field of its output), not a name the plan
    // fixed. For a result the request names no file for: the name is the work's, and the check follows it.
    int? PathFromStep = null,
    string? PathFromField = null,
    // Semantic: the kinds of evidence a verdict on it may cite (5.2). Null or empty: any of them.
    IReadOnlyList<Enactive.Core.Tasks.EvidenceKind>? Kinds = null)
{
    /// <summary>Whether the engine decides it itself from the workspace, rather than by running a command.</summary>
    public bool InEngine => Kind is TypedCriterionKind.FileExists or TypedCriterionKind.FileContains;

    /// <summary>Whether it is decided from what this run's steps handed on - so only the run can decide it.</summary>
    public bool FromRun => Kind is TypedCriterionKind.EvidenceCoversAll || PathFromStep is not null;
}

/// <summary>
/// Ceilings on a single run. Null means "no limit of its own" - which is not zero, and is not the
/// same as a limit that happens to be large.
/// </summary>
public sealed record ExecutionLimits(
    int? MaxSteps = null,
    int? MaxTokens = null,
    int? MaxDurationSeconds = null)
{
    public static readonly ExecutionLimits None = new();
}

/// <summary>
/// What a template is allowed to say about permissions: only how to NARROW them.
///
/// <para>There is deliberately no Allow list here. The rule "a template may not grant more than the
/// workspace allows" is then structural rather than enforced - widening cannot be expressed, so no
/// later edit to a resolver can accidentally permit it. The three fields only ever subtract:
/// <see cref="MaxLevel"/> caps the autonomy tier, <see cref="AskBefore"/> adds tools that must be
/// approved, <see cref="Deny"/> adds tools that are refused outright.</para>
/// </summary>
public sealed record PermissionCeiling(
    PermissionLevel? MaxLevel = null,
    IReadOnlyList<string>? AskBefore = null,
    IReadOnlyList<string>? Deny = null)
{
    public static readonly PermissionCeiling Open = new();

    /// <summary>The lists as lists, so a caller never writes <c>?? Array.Empty&lt;string&gt;()</c>.</summary>
    public IReadOnlyList<string> DenyList => Deny ?? Array.Empty<string>();

    public IReadOnlyList<string> AskBeforeList => AskBefore ?? Array.Empty<string>();
}

/// <summary>
/// One thing a template cannot do its job without, and the tools that could do it.
///
/// <para><b>Why a template has to state this rather than have it inferred.</b> There is already a
/// test asserting no built-in denies a tool its own goal text NAMES, and that test says in its own
/// summary that it would not have caught the case that happened: Code Review's goal never says
/// "git" - it says "everything that has changed since the last commit", which requires git without
/// naming it. A test can check the words; only a person can check the meaning. This is where that
/// person writes the meaning down, once, next to the goal it belongs to.</para>
///
/// <para><b>Alternatives, not a list of requirements.</b> Seeing a diff needs git OR a shell, and a
/// run that has either can do the job. Modelling this as a flat list of tools would refuse
/// schedules that are perfectly able to run, which is the same failure as accepting ones that are
/// not - just quieter and more annoying.</para>
/// </summary>
/// <param name="What">
/// The capability, in the words a person would use: "see what has changed". It becomes the middle
/// of the sentence explaining why a schedule cannot be saved, so it reads as a thing the template
/// needs to DO and not as a tool it wants.
/// </param>
/// <param name="Otherwise">
/// The other way out, in the words of whoever declared the need, for a need that only applies to
/// some ways of filling the template in. Shown after the general advice, so a person sees both. Null
/// where the need is unconditional.
/// </param>
/// <param name="WhenParameterIsDefault">
/// The need applies ONLY while this parameter still holds its default.
///
/// <para>Code Review needs a diff because its scope parameter defaults to "everything that has
/// changed since the last commit" — point it at a folder and it needs nothing of the sort. The first
/// version of this check ignored that and would have refused a schedule that was going to work
/// perfectly well, which is the same failure as accepting one that was not, only more
/// irritating.</para>
///
/// <para>It is a mechanical question, not a semantic one: did the person supply a value, and is it
/// different from the default. That is knowable exactly. What it deliberately does NOT try to
/// answer is whether the value they supplied is itself a diff — somebody who types "the changes
/// since the last release" gets no warning. Under-refusing is the safe direction: the run is cheap
/// now and says why it stopped, which is what §9an was for.</para>
/// </summary>
public sealed record TemplateNeed(
    string What,
    IReadOnlyList<string> AnyOf,
    string? Otherwise = null,
    string? WhenParameterIsDefault = null)
{
    /// <summary>
    /// Whether this need applies to a template filled in like this.
    /// </summary>
    /// <param name="supplied">
    /// What the person actually typed. A parameter absent from here is one they left alone, which
    /// IS the default - the resolver fills it in later, and a need that only looked at supplied
    /// values would never fire for the case it exists for.
    /// </param>
    public bool AppliesTo(TaskTemplate template, IReadOnlyDictionary<string, string>? supplied)
    {
        if (WhenParameterIsDefault is not { Length: > 0 } id)
            return true;

        if (supplied is null || !supplied.TryGetValue(id, out var value) || string.IsNullOrWhiteSpace(value))
            return true;

        var declared = template.ParameterList
            .FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))?.Default;

        return string.Equals(value.Trim(), declared?.Trim() ?? "", StringComparison.Ordinal);
    }
}

/// <summary>
/// A saved task: a pre-written command, the bounds it runs under, and what has to be true at the
/// end. It is NOT a plan and it executes nothing - the planner still plans and the orchestrator
/// still runs the graph. The template says what to ask for and what the run may not do.
/// </summary>
/// <param name="Id">
/// A slug, and also the file name the template is stored under - which is why
/// <see cref="TemplateValidator"/> treats it as a path component and not as a label.
/// </param>
/// <param name="Goal">
/// The task text, with <c>{parameter_id}</c> placeholders. See <see cref="TemplateResolution"/> for
/// what is and is not substituted.
/// </param>
/// <param name="Builtin">
/// Shipped with the app: read-only, because a user who edits one and later wants the original back
/// has nowhere to get it. Duplicate produces an editable copy.
/// </param>
public sealed record TaskTemplate(
    string Id,
    string Name,
    string Goal,
    int Version = 1,
    string? Description = null,
    string? Category = null,
    IReadOnlyList<TemplateParameter>? Parameters = null,
    PermissionCeiling? Permissions = null,
    IReadOnlyList<SuccessCriterionDefinition>? SuccessCriteria = null,
    ExecutionLimits? Limits = null,
    string? WorkerId = null,
    bool ReviewRequired = true,
    bool Builtin = false)
{
    /// <summary>
    /// What this template cannot work without. Empty means "nothing in particular", which is the
    /// truthful answer for most of them and the only safe default: a template that has not been
    /// thought about must not start refusing schedules.
    ///
    /// <para>An init property rather than another positional parameter, so every existing
    /// construction of a template still compiles and every stored template still deserialises into
    /// one. There are twenty-odd positional arguments already.</para>
    /// </summary>
    public IReadOnlyList<TemplateNeed> Needs { get; init; } = Array.Empty<TemplateNeed>();

    public IReadOnlyList<TemplateParameter> ParameterList => Parameters ?? Array.Empty<TemplateParameter>();
    public IReadOnlyList<SuccessCriterionDefinition> CriteriaList
        => SuccessCriteria ?? Array.Empty<SuccessCriterionDefinition>();
    public PermissionCeiling Ceiling => Permissions ?? PermissionCeiling.Open;
    public ExecutionLimits LimitsOrNone => Limits ?? ExecutionLimits.None;

    /// <summary>
    /// An editable copy under a new id. The version restarts at 1 and the built-in flag is dropped:
    /// this is a new template that happens to have been typed for you, not a fork that keeps
    /// claiming the original's lineage.
    /// </summary>
    public TaskTemplate Duplicate(string newId, string? newName = null)
        => this with
        {
            Id = newId,
            Name = newName ?? Name + " (copy)",
            Version = 1,
            Builtin = false
        };
}

/// <summary>One thing wrong with a template, named precisely enough to fix.</summary>
public sealed record TemplateProblem(string Field, string Message)
{
    public override string ToString() => $"{Field}: {Message}";
}

/// <summary>
/// Checks a template before it is stored or run.
///
/// <para>The id check is the load-bearing one. The id becomes
/// <c>&lt;folder&gt;/&lt;id&gt;.json</c>, so an id of <c>../../settings</c> writes outside the
/// template folder. It is validated here AND again in the store: a store that trusts its caller is
/// one refactor away from not being validated at all.</para>
/// </summary>
public static class TemplateValidator
{
    /// <summary>
    /// A slug that is safe as a file name and as a placeholder token: starts with a letter, then
    /// letters, digits, hyphen or underscore. Starting with a letter is what keeps <c>{0}</c> and
    /// <c>{}</c> from being mistaken for parameter references in goal text.
    /// </summary>
    private static readonly Regex Slug = new("^[a-z][a-z0-9_-]*$", RegexOptions.Compiled);

    /// <summary>Anything shaped like a placeholder, whether or not it names a real parameter.</summary>
    internal static readonly Regex Placeholder = new(@"\{([a-z][a-z0-9_-]*)\}", RegexOptions.Compiled);

    public static bool IsSafeId(string? id)
        => !string.IsNullOrWhiteSpace(id) && Slug.IsMatch(id!);

    public static IReadOnlyList<TemplateProblem> Validate(TaskTemplate template)
    {
        var problems = new List<TemplateProblem>();

        if (!IsSafeId(template.Id))
            problems.Add(new TemplateProblem(nameof(template.Id),
                "must be a slug: a lowercase letter, then letters, digits, '-' or '_'. It is used as "
                + "a file name, so a path separator or '..' is refused rather than sanitised."));

        if (string.IsNullOrWhiteSpace(template.Name))
            problems.Add(new TemplateProblem(nameof(template.Name), "cannot be empty."));

        if (string.IsNullOrWhiteSpace(template.Goal))
            problems.Add(new TemplateProblem(nameof(template.Goal), "cannot be empty - it is the task."));

        if (template.Version < 1)
            problems.Add(new TemplateProblem(nameof(template.Version), "starts at 1."));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var parameter in template.ParameterList)
        {
            var where = $"Parameter '{parameter.Id}'";

            if (!IsSafeId(parameter.Id))
                problems.Add(new TemplateProblem(where,
                    "id must be a slug starting with a lowercase letter."));
            else if (!seen.Add(parameter.Id))
                problems.Add(new TemplateProblem(where, "id is declared twice."));

            if (string.IsNullOrWhiteSpace(parameter.Name))
                problems.Add(new TemplateProblem(where, "needs a name to show next to the input."));

            if (parameter.Type == TemplateParameterType.Choice
                && (parameter.Choices is null || parameter.Choices.Count < 2))
                problems.Add(new TemplateProblem(where,
                    "a Choice parameter needs at least two choices; with one there is nothing to choose."));

            if (parameter.Default is { } value && !Accepts(parameter, value))
                problems.Add(new TemplateProblem(where,
                    $"the default '{value}' is not a valid {parameter.Type} value."));
        }

        // A placeholder that names nothing is a typo - '{isue}' for '{issue}' - and would otherwise
        // travel silently into the prompt as literal text. Only checked when the template declares
        // parameters at all, so a goal full of braces in a template that has none stays legal.
        if (template.ParameterList.Count > 0 && !string.IsNullOrEmpty(template.Goal))
        {
            foreach (Match match in Placeholder.Matches(template.Goal))
            {
                var name = match.Groups[1].Value;
                if (!seen.Contains(name))
                    problems.Add(new TemplateProblem(nameof(template.Goal),
                        $"'{{{name}}}' does not name a parameter of this template."));
            }
        }

        foreach (var criterion in template.CriteriaList)
        {
            if (string.IsNullOrWhiteSpace(criterion.Name))
                problems.Add(new TemplateProblem("SuccessCriteria", "a criterion needs a name to report under."));
            if (string.IsNullOrWhiteSpace(criterion.Command))
                problems.Add(new TemplateProblem($"Criterion '{criterion.Name}'", "has no command to run."));
        }

        foreach (var (field, limit) in new (string, int?)[]
        {
            (nameof(ExecutionLimits.MaxSteps), template.LimitsOrNone.MaxSteps),
            (nameof(ExecutionLimits.MaxTokens), template.LimitsOrNone.MaxTokens),
            (nameof(ExecutionLimits.MaxDurationSeconds), template.LimitsOrNone.MaxDurationSeconds)
        })
        {
            if (limit is <= 0)
                problems.Add(new TemplateProblem("Limits",
                    $"{field} must be positive, or null for no limit of its own. Zero would mean the "
                    + "run may do nothing at all."));
        }

        return problems;
    }

    /// <summary>Whether a supplied string is a usable value for this parameter's type.</summary>
    public static bool Accepts(TemplateParameter parameter, string value)
        => parameter.Type switch
        {
            TemplateParameterType.Boolean => bool.TryParse(value, out _),
            TemplateParameterType.Integer => long.TryParse(value, out _),
            TemplateParameterType.Choice => parameter.Choices is { } choices
                && choices.Contains(value, StringComparer.Ordinal),
            _ => true
        };
}
