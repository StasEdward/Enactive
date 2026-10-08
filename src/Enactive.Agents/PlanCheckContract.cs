namespace Enactive.Agents;

using System.Text.Json;
using System.Text.RegularExpressions;
using Enactive.Core.Execution;
using Enactive.Core.Templates;
using Enactive.Core.Tools;

/// <summary>A tool as the contract review sees it: the three facts the validator reads, and nothing it does not.</summary>
internal sealed record PlanCheckTool(string Name, ToolKind Kind, CommandPolicySyntax CommandPolicy);

/// <summary>
/// Everything the planner's verification contract is checked against, apart from the answer itself.
/// As data, so a refusal can be written down with it and replayed.
/// </summary>
internal sealed record PlanCheckInputs(
    string Request,
    IReadOnlyList<SuccessCriterionDefinition> Checks,
    IReadOnlyList<TaskRestriction> Restrictions,
    TaskActionPolicy? ActionPolicy,
    IReadOnlyList<PlanCheckTool>? Tools,
    bool PreserveCriteria);

/// <summary>What an accepted contract says. <see cref="Unresolved"/> is an answer, not a refusal: the planner declining, with a reason.</summary>
internal sealed record PlanContract(
    IReadOnlyList<SuccessCriterionDefinition> Checks,
    IReadOnlyList<TaskRestriction> Restrictions,
    TaskActionPolicy? ActionPolicy,
    string? Unresolved)
{
    /// <summary>What was accepted with a remark, for the run to say.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}

/// <summary>
/// The validation of the planner's verification contract. ONE definition, used by
/// <see cref="PlanCheckReview"/> and by the replay of recorded refusals, so the replay cannot drift
/// from what production checks.
///
/// <para>It throws on a refusal, exactly as it did when it lived inline in the review loop: the
/// message is what the planner is told, and what the run ends with if the second answer is refused
/// too. <see cref="Refusal"/> is the same thing as a value.</para>
/// </summary>
internal static class PlanCheckContract
{
    internal const int MaxFinalChecks = 16;

    /// <summary>The exceptions that mean "this answer is refused", and not "the engine is broken".</summary>
    internal static bool IsRefusal(Exception ex)
        => ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException;

    /// <summary>Whether two criteria pass on the same exit codes - one code, or a list, in any order.</summary>
    private static bool SamePassingCodes(SuccessCriterionDefinition a, SuccessCriterionDefinition b)
        => a.PassingExitCodes.Order().SequenceEqual(b.PassingExitCodes.Order());

    /// <summary>
    /// What a locked review is told when its answer breaks the lock - the whole rule, every time. Each refusal said one
    /// half: told it had dropped a supplied criterion, a review put it back and added the command the request names, was
    /// refused for adding, and had no attempt left (2026-10-08, the run ended before its first step).
    /// </summary>
    internal static string LockedRule(IEnumerable<SuccessCriterionDefinition> supplied)
        => "This list is fixed: return exactly these criteria - "
           + string.Join(", ", supplied.Select(c => $"'{c.Name}' ({c.Command}, exit {c.PassingExitCodesText})"))
           + " - each unchanged, and add none. A command the request requires that is not among them is said in unresolved, "
           + "never added.";

    /// <summary>Why this answer is refused, or null when it is accepted.</summary>
    internal static string? Refusal(string answer, bool complete, PlanCheckInputs inputs)
    {
        try { _ = Validate(answer, complete, inputs); return null; }
        catch (Exception ex) when (IsRefusal(ex)) { return ex.Message; }
    }

    /// <param name="complete">False when the answer was cut off or came with tool calls: then it is not a contract, whatever it says.</param>
    internal static PlanContract Validate(string answer, bool complete, PlanCheckInputs inputs)
    {
        var request = inputs.Request;
        var tools = inputs.Tools;
        var preserveCriteria = inputs.PreserveCriteria;
        var sources = RequestObligations.Create(request).Items;

        if (!complete)
            throw new JsonException("Contract review must be complete JSON without tool calls.");
        using var doc = JsonDocument.Parse(ModelText.ExtractJsonObject(ModelText.StripThink(answer)) ?? "{}");
        var root = doc.RootElement;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in Part(root, "sources", "The contract").EnumerateArray())
        {
            var id = Required(source, "id");
            if (!sources.Any(s => s.Id == id) || !seen.Add(id)) throw new JsonException("Unknown or duplicate source ID: " + id);
            _ = Required(source, "assessment");
        }
        if (seen.Count != sources.Count) throw new JsonException("Every original source ID needs an assessment.");
        var restrictions = new List<TaskRestriction>();
        var notes = new List<string>();
        foreach (var item in Part(root, "forbidden_effects", "The contract").EnumerateArray())
        {
            var effect = Required(item, "effect");
            var quote = Required(item, "source_quote");
            if (!request.Contains(quote, StringComparison.Ordinal))
                throw new JsonException("Unknown task restriction or source quote absent from request.");
            // A ban the request really makes, of a kind the engine has no typed effect for: said, and not a reason
            // to refuse the contract. It was one - on 2026-10-04 a review listed "do not change a source file to make
            // a test pass", quoted word for word, beside a contract otherwise in order, and the run ended there
            // before any work. Nothing is weakened by leaving it out of this list: the engine never enforced it, the
            // request the worker and every review read still says it, and the list goes on holding only what the
            // engine does enforce. A quote the request does not contain is still refused, above.
            if (effect != "file-deletion")
            {
                notes.Add($"A restriction the engine does not enforce by itself, left to the reviews: {effect} (\"{quote}\").");
                continue;
            }
            restrictions.Add(new(ForbiddenTaskEffect.FileDeletion, quote));
        }
        if (inputs.Restrictions.Any(r => !restrictions.Any(n => n.Effect == r.Effect)))
            throw new JsonException("Previously established task restrictions cannot be removed.");
        TaskActionPolicy? actionPolicy = null;
        var policy = Part(root, "action_policy", "The contract");
        if (policy.ValueKind != JsonValueKind.Null)
        {
            var names = Part(policy, "allowed_tools", "action_policy").EnumerateArray().Select(x => x.GetString()!).ToArray();
            var prefixes = Part(policy, "command_prefixes", "action_policy").EnumerateArray().Select(x => x.GetString()!).ToArray();
            var quote = Required(policy, "source_quote");
            var reason = Required(policy, "reason");
            if (!request.Contains(quote, StringComparison.Ordinal)
                || names.Any(n => string.IsNullOrWhiteSpace(n) || tools?.Any(t => t.Name == n) != true)
                || prefixes.Any(p => p is null || !Regex.IsMatch(p,
                    @"^[A-Za-z0-9_.-]+(?: [A-Za-z0-9_.-]+)*$", RegexOptions.None, TimeSpan.FromMilliseconds(100))))
                throw new JsonException("Action policy needs verbatim provenance, known tools and simple command prefixes.");
            if (names.Any(n => tools!.Any(t => t.Name == n && t.Kind == ToolKind.Command && t.CommandPolicy == CommandPolicySyntax.None)))
                throw new JsonException("allowed_tools contains a command adapter with commandPolicy=None; select supported adapters from the inventory.");
            actionPolicy = new(names, prefixes, quote, reason)
            {
                // A list of command tools bounds commands, not the file tools (TaskActionPolicy.CommandsOnly).
                CommandsOnly = names.Length > 0 && names.All(n => tools!.Any(t => t.Name == n && t.Kind == ToolKind.Command))
            };
        }
        if (inputs.ActionPolicy is not null && (actionPolicy is null || !inputs.ActionPolicy.SameAs(actionPolicy)))
            throw new JsonException("Previously established action policy cannot be removed or changed.");
        var unresolved = Part(root, "unresolved", "The contract");
        if (unresolved.ValueKind != JsonValueKind.Null)
            return new([], restrictions, actionPolicy, Required(root, "unresolved")) { Notes = notes };
        var checks = new List<SuccessCriterionDefinition>();
        var items = Part(root, "checks", "The contract");
        if (!preserveCriteria && items.GetArrayLength() > MaxFinalChecks) throw new JsonException("Too many final checks; resolve the contract without dropping requirements.");
        foreach (var item in items.EnumerateArray())
        {
            var name = Required(item, "name"); var command = Required(item, "command");
            var origin = Required(item, "origin"); var reason = Required(item, "reason");
            // The exit codes that pass: one, or - where the request itself says which count as success - a list. The engine
            // judges by a list already (SuccessCriterionDefinition.ExpectedExitCodes); this contract could not carry one, so
            // a request saying a test command's exit 1 is a finding, not a failure, was "unresolved" every time it was
            // reviewed - asked of a person on 2026-09-29 and again on 2026-10-08.
            var codes = item.ValueKind == JsonValueKind.Object && item.TryGetProperty("expectedExitCodes", out var listed)
                && listed.ValueKind == JsonValueKind.Array && listed.GetArrayLength() > 0
                ? listed.EnumerateArray().Select(c => c.GetInt32()).Distinct().ToArray() : null;
            var exit = codes is not null && !(item.TryGetProperty("expectedExitCode", out var single) && single.ValueKind == JsonValueKind.Number)
                ? (codes.Contains(0) ? 0 : codes[0])
                : Part(item, "expectedExitCode", "A check").GetInt32();
            var quote = Part(item, "request_quote", "A check");
            string? text = null;
            if (preserveCriteria)
            {
                // Locked: every supplied criterion is kept exactly as it was, provenance included
                // (see below), so the label on the answer's copy of it decides nothing - and is not
                // judged. Judging it refused the runs of 2026-09-28 over a word the planner had not
                // been shown. Name, command and exit code still have to match.
            }
            else if (origin == "requested")
            {
                text = Required(item, "request_quote");
                if (!request.Contains(text, StringComparison.Ordinal) || !text.Contains(command, StringComparison.Ordinal))
                    throw new JsonException("Requested commands require exact original request provenance.");
                // A code other than 0 passes only where the request says so: each is in the request's own words. Without
                // this a review could make any command pass on any code, and call it what the request asked.
                if ((codes ?? [exit]).FirstOrDefault(c => c != 0 && !Regex.IsMatch(request, $@"(?<![\d-]){c}(?!\d)",
                        RegexOptions.None, TimeSpan.FromMilliseconds(100))) is var unsaid and not 0)
                    throw new JsonException($"Exit code {unsaid} is not in the request; a requested check passes on 0, or on the codes the request itself names.");
            }
            else if (origin != "proposed" || quote.ValueKind != JsonValueKind.Null || exit != 0 || codes is { Length: > 1 })
                throw new JsonException("Proposed checks require null provenance and exit 0.");
            checks.Add(new(name, command, exit, Origin: origin == "requested" ? CriterionOrigin.Requested
                : origin == "declared" ? CriterionOrigin.Declared : CriterionOrigin.Proposed)
                { RequestQuote = text, PlanningReason = reason, ExpectedExitCodes = codes is { Length: > 1 } ? codes : null });
        }
        if (preserveCriteria)
        {
            var remaining = checks.ToList();
            checks.Clear();
            foreach (var original in inputs.Checks)
            {
                var match = remaining.FindIndex(c => c.Name == original.Name && c.Command == original.Command
                    && SamePassingCodes(c, original));
                if (match < 0) throw new JsonException("A locked criterion was omitted or changed. " + LockedRule(inputs.Checks));
                checks.Add(original with { PlanningReason = remaining[match].PlanningReason });
                remaining.RemoveAt(match);
            }
            if (remaining.Count > 0) throw new JsonException("A criterion was added to a locked list. " + LockedRule(inputs.Checks));
        }
        if (inputs.Checks.Where(c => c.Origin == CriterionOrigin.Requested).Any(original =>
            !checks.Any(c => c.Origin == CriterionOrigin.Requested && c.Command == original.Command
                && SamePassingCodes(c, original))))
            throw new JsonException("An existing requested criterion was omitted or changed. Preserve it, or report an unresolved conflict.");
        if (actionPolicy is not null && checks.Any(c => !actionPolicy.AllowedTools.Contains("run_command")
            || !actionPolicy.AllowsCommand(c.Command)))
            throw new JsonException("Final criterion conflicts with the task action policy; revise proposed checks or report unresolved.");
        return new(checks, restrictions, actionPolicy, null) { Notes = notes };
    }

    /// <summary>
    /// A part of the answer, or a refusal that NAMES it. Read with the runtime's own lookup, a part that was not
    /// there came back to the planner as "The given key was not present in the dictionary" (2026-10-04): the one
    /// further answer it had was spent guessing what that meant.
    /// </summary>
    private static JsonElement Part(JsonElement item, string name, string where)
        => item.ValueKind == JsonValueKind.Object && item.TryGetProperty(name, out var value) ? value
            : throw new JsonException($"{where} has no \"{name}\": every part of the contract is given, null or [] when it is empty.");

    private static string Required(JsonElement item, string name)
    {
        var part = Part(item, name, "An entry of the contract");
        var value = part.ValueKind == JsonValueKind.String ? part.GetString()
            : throw new JsonException($"\"{name}\" must be text.");
        return !string.IsNullOrWhiteSpace(value) ? value : throw new JsonException(name + " must not be blank.");
    }
}

/// <summary>
/// A verification contract the planner gave and validation refused, with exactly what it was
/// checked against. Before this, only the combined review kept its refusals, and the planner's -
/// which end a run before any work starts, twice on 2026-09-28 - left only a log line.
/// </summary>
internal sealed record PlanCheckCorpusCase(
    DateTimeOffset RecordedAt,
    string Model,
    string Answer,
    bool Complete,
    PlanCheckInputs Inputs,
    string Refusal);

internal static class PlanCheckCorpus
{
    /// <summary>Under the workspace, beside the engine's other own data.</summary>
    internal const string Folder = ".enactive/plan-check-corpus";

    internal static void Record(string? workspaceRoot, PlanCheckCorpusCase refusal)
        => RefusalCorpus.Record(workspaceRoot, Folder, refusal.RecordedAt, refusal);

    internal static PlanCheckCorpusCase Read(string path) => RefusalCorpus.Read<PlanCheckCorpusCase>(path);

    /// <summary>The recorded answer against today's validation: null when it is accepted now.</summary>
    internal static string? Replay(PlanCheckCorpusCase recorded)
        => PlanCheckContract.Refusal(recorded.Answer, recorded.Complete, recorded.Inputs);
}
