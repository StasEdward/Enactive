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
    internal const int MaxFinalChecks = PlanCheckContract.MaxFinalChecks;

    /// <summary>
    /// How much of a tool's description the review is shown. A description is whatever the tool's author wrote -
    /// an external server's may run to pages - and the review reads every tool's on every run; the state a tool
    /// reports about itself is said at the start.
    /// </summary>
    internal const int MaxToolDescription = 600;

    private static string Shown(string description)
        => description.Length <= MaxToolDescription ? description : description[..MaxToolDescription] + "…";
    internal static async Task<PlanResult> RunAsync(PlanResult plan, string request, WorkContext context,
        IChatProvider provider, string model, RunBudget budget, int outputBudget, CancellationToken ct,
        bool preserveCriteria = false, IReadOnlyList<ToolDefinition>? tools = null, string? workspaceRoot = null,
        // Before the work: an unsettled contract without a restriction it could be about goes to a person (Unsettled),
        // instead of stopping the run. A repair after a failure stays strict.
        bool askWhenUnsettled = false)
    {
        // Criteria the engine decides itself are not commands, and are not this review's: nothing in
        // them can run, so nothing in them can break the task's restrictions. Set aside, and put back
        // after, so a review answered with a list of commands cannot lose them (Phase 3.4).
        // They are shown to it in their own typed form, with where each file's name came from, and it may
        // correct what the plan chose - within EngineCriteriaReview's limits - but never turn one into a command.
        var decidedByTheEngine = plan.Checks.Where(c => c.Typed is not null).ToArray();
        plan = plan with { Checks = plan.Checks.Where(c => c.Typed is null).ToArray() };
        var reviewsEngineCriteria = !preserveCriteria && decidedByTheEngine.Length > 0;
        var inputs = new PlanCheckInputs(request, plan.Checks, plan.Restrictions, plan.ActionPolicy,
            tools?.Select(t => new PlanCheckTool(t.Name, t.Kind, t.CommandPolicy)).ToArray(), preserveCriteria);
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
                + "allowed_tools that names only command tools restricts COMMANDS: the tools that read and change files in the workspace "
                + "stay available, and every other tool is refused. To restrict the file tools as well, name every tool that may be used. "
                + "Use the supplied tool inventory. A tool's description says what is already configured for it outside the request "
                + "(an account, a destination, a default argument): that is not missing from the request, and is no reason for unresolved. Interpret words such as only explicitly; explain the boundary in reason. "
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
                    plan.Title, steps = plan.Plan?.Steps.Select((s, i) => new { index = i, s.Title, s.ObligationIds }),
                    // Origin by NAME, in the answer's own words. Serialized as it was, a template's check
                    // reached the planner as "Origin":0 while a locked review demanded origin=declared
                    // back; on 2026-09-28 it guessed "proposed", twice, and both runs ended there.
                    checks = plan.Checks.Select(c => new { c.Name, c.Command, c.ExpectedExitCode, c.Required,
                        origin = c.Origin.ToString().ToLowerInvariant(), c.AlreadyPassing, c.RequestQuote, c.PlanningReason }),
                    engineCriteria = reviewsEngineCriteria ? EngineCriteriaReview.Show(decidedByTheEngine, request, workspaceRoot, plan.Plan) : null,
                    existingRestrictions = plan.Restrictions, actionPolicy = plan.ActionPolicy,
                    // With what each tool says of itself. By name, kind and policy alone the review took what a
                    // tool already has set up for something the request forgot: on 2026-10-03 a request to mail a
                    // report was stopped at "lacks an e-mail recipient/SMTP details", with both in Settings and the
                    // sending tool's description saying so - to the worker, who was never reached.
                    tools = tools?.Select(t => new { t.Name, kind = t.Kind.ToString(), commandPolicy = t.CommandPolicy.ToString(),
                        description = Shown(t.Description) })
                }))
        };
        if (reviewsEngineCriteria)
            messages[0] = ChatMessage.System(messages[0].Content + EngineCriteriaReview.Prompt);
        if (preserveCriteria)
            messages[0] = ChatMessage.System(messages[0].Content +
                "\nThis is a LOCKED criteria review (template, resume, or proposed repair). Return every supplied criterion "
                + "with the same name, command, origin and expectedExitCode; do not add, drop or rewrite any criterion. "
                + "The suggested-check count limit does not apply to this fixed list. "
                + "origin=declared is also allowed for a supplied declared criterion, with request_quote=null. "
                + "Assess compliance with the original request's restrictions independently of host approval settings. "
                + "If any criterion conflicts, or compliance is uncertain, return unresolved with the specific conflict. "
                + "An approved tool or template does not waive the user's task restrictions.");
        // Asked, and corrected once when the contract is refused - the round every review shares (StructuredAnswer).
        // The contract's own validation judges an answer that did not finish, so it is read either way.
        var round = await StructuredAnswer.AskAsync<CheckedContract>(provider, messages,
            m => new ChatRequest(model, m, Temperature: 0, Purpose: GenerationPurpose.Planning, OutputTokenLimit: Math.Max(1, outputBudget)),
            (answer, complete) =>
            {
                try { return (new CheckedContract(PlanCheckContract.Validate(answer, complete, inputs), answer), []); }
                catch (Exception ex) when (PlanCheckContract.IsRefusal(ex))
                {
                    // Written down with what it was checked against: a refused contract ends the run
                    // before any work starts, and the log alone cannot replay it.
                    PlanCheckCorpus.Record(workspaceRoot,
                        new(DateTimeOffset.UtcNow, model, answer, complete, inputs, ex.Message));
                    return (null, [ex.Message]);
                }
            },
            errors => "Invalid verification contract: " + errors[0]
                + " Return the complete corrected contract; preserve all original requirements and restrictions.",
            budget.TurnExhaustedAfter, requireComplete: false, ct);
        var usage = round.Usage;

        if (round.Shortfall("The verification contract") is { } shortfall)
            return Result(shortfall);

        var (contract, said) = round.Value!;
        if (contract.Unresolved is { } unresolved)
        {
            // Not settled. Where there is a restriction it can be about - a ban on deleting files, an allowlist
            // of tools or commands, one established before - that is a stop. Without one, it is for a person
            // to decide (Phase 1.8: an ambiguity nothing can resolve is NeedsUser), not for the review alone:
            // twice on 2026-09-29 a run was stopped before its first step because "sending an email cannot be
            // verified by a command" and a test command's two exit codes did not fit one field.
            if (!askWhenUnsettled || contract.Restrictions.Count > 0 || contract.ActionPolicy is not null
                || inputs.Restrictions.Count > 0 || inputs.ActionPolicy is not null)
                return Result("Unresolved verification contract: " + unresolved);
            return Result(budget.TurnExhaustedAfter(usage.Prompt, usage.Completion)) with { Unsettled = unresolved };
        }
        var (engineCriteria, notes) = reviewsEngineCriteria
            ? EngineCriteriaReview.Apply(decidedByTheEngine, said, request, workspaceRoot, plan.Plan)
            : (decidedByTheEngine, []);
        return Result(budget.TurnExhaustedAfter(usage.Prompt, usage.Completion)) with {
            Checks = [.. contract.Checks, .. engineCriteria], Restrictions = contract.Restrictions, ActionPolicy = contract.ActionPolicy,
            // With what the plan itself had to say (a check it kept as a proposal), which this result replaces.
            ContractNotes = [.. plan.ContractNotes, .. contract.Notes, .. notes] };

        PlanResult Result(string? error) => plan with { Checks = [.. plan.Checks, .. decidedByTheEngine], IncompleteReason = error, Usage = usage };
    }

    /// <summary>A contract that passed validation, with the answer it was read from.</summary>
    private sealed record CheckedContract(PlanContract Contract, string Answer);
}
