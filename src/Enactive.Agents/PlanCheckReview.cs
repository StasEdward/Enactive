namespace Enactive.Agents;

using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Context;
using Enactive.Core.Execution;
using Enactive.Core.Providers;
using Enactive.Core.Templates;
using Enactive.Core.Tools;

/// <summary>Read-only semantic review of final verification criteria by the planning model.
/// The engine validates coverage and provenance, not the meaning of arbitrary shell commands.</summary>
internal static class PlanCheckReview
{
    internal const int MaxFinalChecks = 16;
    internal static async Task<PlanResult> RunAsync(PlanResult plan, string request, WorkContext context,
        IChatProvider provider, string model, RunBudget budget, int outputBudget, CancellationToken ct,
        bool preserveCriteria = false, IReadOnlyList<ToolDefinition>? tools = null)
    {
        var sources = RequestObligations.Create(request).Items;
        var messages = new List<ChatMessage>
        {
            ChatMessage.System("Review the planner's FINAL verification criteria before any execution. You are the planner, not the worker. "
                + "Read EVERY original source unit. Recover omitted explicitly required final commands, preserve their exact flags and paths, "
                + "and remove or replace suggestions conflicting with ANY request restriction (including allowed tools/commands, network and workspace). "
                + "Commands always run in the current workspace root. Do not run tools. Do not invent filesystem facts. "
                + "A final check must be safe to repeat AFTER all work. Intermediate mutation/failure/setup/destructive commands belong to worker steps, "
                + "not final checks; record them in the source assessment without replaying them at the end. Examples and prohibitions are not instructions to run commands. "
                + "Return ONLY JSON {sources:[{id,assessment}],checks:[{name,command,origin,request_quote,expectedExitCode,reason}],forbidden_effects:[{effect,source_quote}],action_policy:null,unresolved:null}. "
                + "action_policy is null when there is no explicit tool/command allowlist. Otherwise return "
                + "{allowed_tools:[exact tool names],command_prefixes:[executable and allowed subcommand],source_quote,reason}. "
                + "Use the supplied tool inventory. Interpret words such as only explicitly; explain the boundary in reason. "
                + "A local-files-only restriction excludes external integrations. Commands outside allowed families must not "
                + "be proposed as checks. Prefixes contain simple space-separated executable/subcommand tokens only, no shell syntax. "
                + "Use commandPolicy from the tool inventory: SimpleCommand supports a single literal command; PowerShell supports static commands, sequences and pipelines with literal arguments, with EVERY command checked against command_prefixes. Dynamic expressions, script blocks, redirection and aliases are not supported under this policy. Omit command tools with commandPolicy=None. Use [] to forbid all commands. "
                + "This restricts invocations, not effects inside allowed processes; do not promise network/process sandboxing. "
                + "If a restriction cannot be represented faithfully, return unresolved instead of silently weakening it. "
                + "Preserve an existing action_policy unchanged on resume or criterion repair. "
                + "Classify explicit unconditional bans on deleting files as effect=file-deletion with a verbatim source_quote. "
                + "Include bans even when deletion would help restoration, cleanup or mutation testing. Tool approval cannot waive them. "
                + "Do not classify examples, conditional restrictions or merely cautionary wording as unconditional bans. "
                + "Only file-deletion is currently supported as a typed effect; assess other restrictions in sources. "
                + "Include every source ID exactly once, assessing required verification and restrictions in it, even when checks is empty. "
                + "checks is the COMPLETE corrected list, at most 16. origin is requested or proposed. "
                + "requested requires a verbatim request_quote containing the exact command. proposed requires request_quote=null and expectedExitCode=0. "
                + "Each reason explains why this check is appropriate as a final check and permitted by the entire request. "
                + "Do not replace a required command with your preferred command, silently waive a requirement, or truncate required checks to fit the limit. "
                + "If constraints conflict or a final check cannot safely be specified, set unresolved to an explanation; no execution will start. "
                + "A document/explanation may correctly have checks=[]; do not invent shell checks merely to have one."),
            ChatMessage.User(Planner.Where(context) + RequestObligations.ExecutionPrompt(request)
                + "\nPlan and draft final criteria:\n" + JsonSerializer.Serialize(new {
                    plan.Title, steps = plan.Plan?.Steps.Select(s => new { s.Title, s.ObligationIds }), checks = plan.Checks,
                    existingRestrictions = plan.Restrictions, actionPolicy = plan.ActionPolicy,
                    tools = tools?.Select(t => new { t.Name, kind = t.Kind.ToString(), commandPolicy = t.CommandPolicy.ToString() })
                }))
        };
        if (preserveCriteria)
            messages[0] = ChatMessage.System(messages[0].Content +
                "\nThis is a LOCKED criteria review (template, resume, or proposed repair). Return every supplied criterion "
                + "with the same name, command, origin and expectedExitCode; do not add, drop or rewrite any criterion. "
                + "The suggested-check count limit does not apply to this fixed list. "
                + "origin=declared is also allowed for a supplied declared criterion, with request_quote=null. "
                + "Assess compliance with the original request's restrictions independently of host approval settings. "
                + "If any criterion conflicts, or compliance is uncertain, return unresolved with the specific conflict. "
                + "An approved tool or template does not waive the user's task restrictions.");
        var prompt = 0; var output = 0; int? cached = null; int? created = null;
        string? problem = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (budget.TurnExhaustedAfter(prompt, output) is { } spent) { problem = spent; break; }
            ChatCompletion completion;
            try
            {
                completion = await provider.CompleteAsync(GenerationAllowance.Fit(new(model, messages, Temperature: 0,
                    Purpose: GenerationPurpose.Planning, OutputTokenLimit: Math.Max(1, outputBudget)), provider), ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { problem = "Verification contract review failed: " + ex.Message; break; }
            prompt += completion.PromptTokens ?? 0;
            output += completion.CompletionTokens ?? 0;
            cached = TokenCounts.Add(cached, completion.CachedPromptTokens);
            created = TokenCounts.Add(created, completion.CacheCreationPromptTokens);
            var answer = completion.Message.Content ?? "";
            try
            {
                if (completion.FinishReason is "length" or "max_tokens" || completion.Message.ToolCalls is { Count: > 0 })
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
                if (plan.Restrictions.Any(r => !restrictions.Any(n => n.Effect == r.Effect)))
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
                        || prefixes.Any(p => p is null || !System.Text.RegularExpressions.Regex.IsMatch(p,
                            @"^[A-Za-z0-9_.-]+(?: [A-Za-z0-9_.-]+)*$", System.Text.RegularExpressions.RegexOptions.None,
                            TimeSpan.FromMilliseconds(100))))
                        throw new JsonException("Action policy needs verbatim provenance, known tools and simple command prefixes.");
                    if (names.Any(n => tools!.Any(t => t.Name == n && t.Kind == ToolKind.Command && t.CommandPolicy == CommandPolicySyntax.None)))
                        throw new JsonException("allowed_tools contains a command adapter with commandPolicy=None; select supported adapters from the inventory.");
                    actionPolicy = new(names, prefixes, quote, reason);
                }
                if (plan.ActionPolicy is not null && (actionPolicy is null || !plan.ActionPolicy.SameAs(actionPolicy)))
                    throw new JsonException("Previously established action policy cannot be removed or changed.");
                var unresolved = root.GetProperty("unresolved");
                if (unresolved.ValueKind != JsonValueKind.Null)
                {
                    problem = "Unresolved verification contract: " + Required(root, "unresolved");
                    break;
                }
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
                    foreach (var original in plan.Checks)
                    {
                        var match = remaining.FindIndex(c => c.Name == original.Name && c.Command == original.Command
                            && c.Origin == original.Origin && c.ExpectedExitCode == original.ExpectedExitCode);
                        if (match < 0) throw new JsonException("Locked criterion omitted or changed; report unresolved instead.");
                        checks.Add(original with { PlanningReason = remaining[match].PlanningReason });
                        remaining.RemoveAt(match);
                    }
                    if (remaining.Count > 0) throw new JsonException("Locked review cannot add criteria.");
                }
                if (plan.Checks.Where(c => c.Origin == CriterionOrigin.Requested).Any(original =>
                    !checks.Any(c => c.Origin == CriterionOrigin.Requested && c.Command == original.Command
                        && c.ExpectedExitCode == original.ExpectedExitCode)))
                    throw new JsonException("An existing requested criterion was omitted or changed. Preserve it, or report an unresolved conflict.");
                if (actionPolicy is not null && checks.Any(c => !actionPolicy.AllowedTools.Contains("run_command")
                    || !actionPolicy.AllowsCommand(c.Command)))
                    throw new JsonException("Final criterion conflicts with the task action policy; revise proposed checks or report unresolved.");
                return Result(budget.TurnExhaustedAfter(prompt, output)) with {
                    Checks = checks, Restrictions = restrictions, ActionPolicy = actionPolicy };
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException)
            {
                problem = "Invalid verification contract: " + ex.Message;
                messages.Add(ChatMessage.Assistant(answer));
                messages.Add(ChatMessage.User(problem + " Return the complete corrected contract; preserve all original requirements and restrictions."));
            }
        }
        return Result(problem ?? "Verification contract review incomplete.");

        PlanResult Result(string? error) => plan with { PromptTokens = prompt, CompletionTokens = output,
            CachedPromptTokens = cached, CacheCreationPromptTokens = created, IncompleteReason = error };
    }

    private static string Required(JsonElement item, string name)
    {
        var value = item.GetProperty(name).GetString();
        return !string.IsNullOrWhiteSpace(value) ? value : throw new JsonException(name + " must not be blank.");
    }
}
