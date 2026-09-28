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
    string? Unresolved);

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
        foreach (var source in root.GetProperty("sources").EnumerateArray())
        {
            var id = Required(source, "id");
            if (!sources.Any(s => s.Id == id) || !seen.Add(id)) throw new JsonException("Unknown or duplicate source ID: " + id);
            _ = Required(source, "assessment");
        }
        if (seen.Count != sources.Count) throw new JsonException("Every original source ID needs an assessment.");
        var restrictions = new List<TaskRestriction>();
        foreach (var item in root.GetProperty("forbidden_effects").EnumerateArray())
        {
            var effect = Required(item, "effect");
            var quote = Required(item, "source_quote");
            if (effect != "file-deletion" || !request.Contains(quote, StringComparison.Ordinal))
                throw new JsonException("Unknown task restriction or source quote absent from request.");
            restrictions.Add(new(ForbiddenTaskEffect.FileDeletion, quote));
        }
        if (inputs.Restrictions.Any(r => !restrictions.Any(n => n.Effect == r.Effect)))
            throw new JsonException("Previously established task restrictions cannot be removed.");
        TaskActionPolicy? actionPolicy = null;
        var policy = root.GetProperty("action_policy");
        if (policy.ValueKind != JsonValueKind.Null)
        {
            var names = policy.GetProperty("allowed_tools").EnumerateArray().Select(x => x.GetString()!).ToArray();
            var prefixes = policy.GetProperty("command_prefixes").EnumerateArray().Select(x => x.GetString()!).ToArray();
            var quote = Required(policy, "source_quote");
            var reason = Required(policy, "reason");
            if (!request.Contains(quote, StringComparison.Ordinal)
                || names.Any(n => string.IsNullOrWhiteSpace(n) || tools?.Any(t => t.Name == n) != true)
                || prefixes.Any(p => p is null || !Regex.IsMatch(p,
                    @"^[A-Za-z0-9_.-]+(?: [A-Za-z0-9_.-]+)*$", RegexOptions.None, TimeSpan.FromMilliseconds(100))))
                throw new JsonException("Action policy needs verbatim provenance, known tools and simple command prefixes.");
            if (names.Any(n => tools!.Any(t => t.Name == n && t.Kind == ToolKind.Command && t.CommandPolicy == CommandPolicySyntax.None)))
                throw new JsonException("allowed_tools contains a command adapter with commandPolicy=None; select supported adapters from the inventory.");
            actionPolicy = new(names, prefixes, quote, reason);
        }
        if (inputs.ActionPolicy is not null && (actionPolicy is null || !inputs.ActionPolicy.SameAs(actionPolicy)))
            throw new JsonException("Previously established action policy cannot be removed or changed.");
        var unresolved = root.GetProperty("unresolved");
        if (unresolved.ValueKind != JsonValueKind.Null)
            return new([], restrictions, actionPolicy, Required(root, "unresolved"));
        var checks = new List<SuccessCriterionDefinition>();
        var items = root.GetProperty("checks");
        if (!preserveCriteria && items.GetArrayLength() > MaxFinalChecks) throw new JsonException("Too many final checks; resolve the contract without dropping requirements.");
        foreach (var item in items.EnumerateArray())
        {
            var name = Required(item, "name"); var command = Required(item, "command");
            var origin = Required(item, "origin"); var reason = Required(item, "reason");
            var exit = item.GetProperty("expectedExitCode").GetInt32();
            var quote = item.GetProperty("request_quote");
            string? text = null;
            if (origin == "requested")
            {
                text = Required(item, "request_quote");
                if (!request.Contains(text, StringComparison.Ordinal) || !text.Contains(command, StringComparison.Ordinal))
                    throw new JsonException("Requested commands require exact original request provenance.");
            }
            else if (preserveCriteria && origin == "declared" && quote.ValueKind == JsonValueKind.Null) { }
            else if (origin != "proposed" || quote.ValueKind != JsonValueKind.Null || exit != 0)
                throw new JsonException("Proposed checks require null provenance and exit 0.");
            checks.Add(new(name, command, exit, Origin: origin == "requested" ? CriterionOrigin.Requested
                : origin == "declared" ? CriterionOrigin.Declared : CriterionOrigin.Proposed)
                { RequestQuote = text, PlanningReason = reason });
        }
        if (preserveCriteria)
        {
            var remaining = checks.ToList();
            checks.Clear();
            foreach (var original in inputs.Checks)
            {
                var match = remaining.FindIndex(c => c.Name == original.Name && c.Command == original.Command
                    && c.Origin == original.Origin && c.ExpectedExitCode == original.ExpectedExitCode);
                if (match < 0) throw new JsonException("Locked criterion omitted or changed; report unresolved instead.");
                checks.Add(original with { PlanningReason = remaining[match].PlanningReason });
                remaining.RemoveAt(match);
            }
            if (remaining.Count > 0) throw new JsonException("Locked review cannot add criteria.");
        }
        if (inputs.Checks.Where(c => c.Origin == CriterionOrigin.Requested).Any(original =>
            !checks.Any(c => c.Origin == CriterionOrigin.Requested && c.Command == original.Command
                && c.ExpectedExitCode == original.ExpectedExitCode)))
            throw new JsonException("An existing requested criterion was omitted or changed. Preserve it, or report an unresolved conflict.");
        if (actionPolicy is not null && checks.Any(c => !actionPolicy.AllowedTools.Contains("run_command")
            || !actionPolicy.AllowsCommand(c.Command)))
            throw new JsonException("Final criterion conflicts with the task action policy; revise proposed checks or report unresolved.");
        return new(checks, restrictions, actionPolicy, null);
    }

    private static string Required(JsonElement item, string name)
    {
        var value = item.GetProperty(name).GetString();
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
