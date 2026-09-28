namespace Enactive.Agents;

using System.Text.Json;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;
using Enactive.Core.Tools;

/// <summary>
/// Runs a run's success criteria and reports what they said.
///
/// <para>The criteria go through the SAME tool and the same permission gate as anything the model
/// asks for. Not because a criterion is untrustworthy in the way a model's tool call is — the user
/// wrote it — but because a workspace template arrives with a cloned repository, and
/// <c>.enactive/templates/*.json</c> in someone else's repository is someone else's command. A
/// check that ran outside the gate would be a way to execute arbitrary shell in a workspace by
/// shipping a JSON file in it.</para>
///
/// <para>Everything that is not a pass is reported honestly rather than swallowed: a denied
/// command, a missing tool and a process that would not start are all
/// <see cref="CriterionOutcome.Unknown"/>, which is not a pass and not an accusation.</para>
/// </summary>
public sealed class SuccessEvaluator : ISuccessEvaluator
{
    /// <summary>Legacy built-in identifier; dispatch now uses trusted RunsSuccessChecks metadata.</summary>
    [Obsolete("Resolve the success-check adapter from ToolDefinition.RunsSuccessChecks.")]
    public const string CommandTool = "run_command";

    public async Task<SuccessReport> EvaluateAsync(
        IReadOnlyList<SuccessCriterionDefinition> criteria,
        IToolRegistry tools,
        IPermissionEngine permissions,
        PermissionPolicy policy,
        IDecisionHandler decisions,
        ToolContext context,
        Guid taskId,
        CancellationToken ct)
    {
        if (criteria.Count == 0)
            return SuccessReport.NothingToCheck;

        var results = new List<CriterionResult>(criteria.Count);

        foreach (var criterion in criteria)
        {
            ct.ThrowIfCancellationRequested();
            results.Add(await EvaluateOneAsync(
                criterion, tools, permissions, policy, decisions, context, taskId, ct));
        }

        return new SuccessReport(results);
    }

    private static async Task<CriterionResult> EvaluateOneAsync(
        SuccessCriterionDefinition criterion,
        IToolRegistry tools,
        IPermissionEngine permissions,
        PermissionPolicy policy,
        IDecisionHandler decisions,
        ToolContext context,
        Guid taskId,
        CancellationToken ct)
    {
        // A criterion stated as a type the engine understands is decided by the engine: no shell,
        // no command to guess, the same on every platform (Phase 3). A test criterion is a command,
        // and goes the ordinary way below.
        if (criterion.Typed is { InEngine: true })
            return TypedCriteria.Evaluate(criterion, context.WorkspaceRoot);

        // Decided from what the run's steps handed on, which only the run has (Phase 5.1).
        if (criterion.Typed is { FromRun: true })
            return new(criterion.Name, criterion.Command, criterion.Required, CriterionOutcome.Unknown, null,
                "it is decided from this run's step outputs, and none were given here", criterion.Origin, criterion.AlreadyPassing);

        CriterionResult Unknown(string why)
            => new(criterion.Name, criterion.Command, criterion.Required,
                   CriterionOutcome.Unknown, null, why, criterion.Origin, criterion.AlreadyPassing);

        var candidates = tools.Definitions.Where(d => d.Kind == ToolKind.Command && d.RunsSuccessChecks).ToArray();
        if (candidates.Length != 1)
            return Unknown("Exactly one tool must declare the success-check command protocol; found " + candidates.Length + ".");
        var commandTool = candidates[0].Name;

        PermissionLevel required;
        try
        {
            required = tools.RequiredLevelOf(commandTool);
        }
        catch (Exception ex)
        {
            // The declared adapter cannot be resolved. Not a failure of the work - we simply cannot
            // check it here, and saying so beats reporting a pass nobody verified.
            return Unknown($"'{commandTool}' is not available in this workspace: {ex.Message}");
        }

        var gate = permissions.Evaluate(policy, commandTool, required);

        if (gate == PermissionDecision.Deny)
            return Unknown(
                $"the permission policy forbids '{commandTool}', so this check could not be run.");

        if (gate == PermissionDecision.Ask)
        {
            // Declared required checks block completion when skipped; proposed checks do not.
            var request = new DecisionRequest(
                taskId,
                $"Run the success check '{criterion.Name}'?",
                $"Command: {criterion.Command}",
                new[] { new DecisionOption("allow", "Run it"), new DecisionOption("deny", "Skip") },
                RecommendedOptionId: "allow",
                Subject: commandTool,
                FullDetail: $"Success criterion '{criterion.Name}' for this run:\n\n{criterion.Command}\n\n"
                          + $"It passes on exit code {criterion.ExpectedExitCode}. "
                          + (!criterion.Required
                              ? "It is optional: skipping it records NOT CHECKED and does not block completion."
                              : criterion.Origin == CriterionOrigin.Proposed
                                  ? "It was proposed by the planner: skipping it records NOT CHECKED and does not block completion. If it runs and fails, it blocks completion."
                                  : "It is REQUIRED: skipping it records NOT CHECKED and means the run cannot be reported as completed."));

            DecisionOutcome outcome;
            try
            {
                outcome = await decisions.RequestAsync(request, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Unknown($"the approval for this check could not be obtained: {ex.Message}");
            }

            if (!string.Equals(outcome.OptionId, "allow", StringComparison.OrdinalIgnoreCase))
                return Unknown("the user did not permit this check to run.");
        }

        ToolResult result;
        try
        {
            var call = new ToolCall(
                Guid.NewGuid().ToString("N"),
                commandTool,
                JsonSerializer.Serialize(new Dictionary<string, string> { ["command"] = criterion.Command }));

            result = await tools.InvokeAsync(call, context, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Unknown($"the check could not be run: {ex.Message}");
        }

        // A command the SHELL would not start - a word it does not have, text it could not parse,
        // a parameter that cmdlet does not take. It still carries an exit code, so judging by the
        // number alone reported "the work is wrong" about a check that never ran. That is the one
        // verdict this must never give: it accuses the work of a fault in the check.
        //
        // The tool is the only thing that knows (see ToolResults.Unreadable), and since 2026-09-20
        // it says so. This is why a PROPOSED check is safe to arm by default - a command the
        // planner guessed wrong cannot fail a run, it can only fail to answer.
        if (result.DidNotRun)
            return Unknown("the shell would not start this check, so it has verified nothing. "
                         + Trim(result.Error ?? result.Output));

        // The exit code, not the tool's own Success flag. run_command reports failure for any
        // non-zero code, but a criterion is allowed to EXPECT one - 'git diff --exit-code' means
        // something by returning 1 - so the number is what decides, and the flag is not consulted.
        if (!TryExitCode(result, out var exitCode))
            return Unknown(
                "the check ran but reported no exit code, so there is nothing to judge it by. "
                + Trim(result.Error ?? result.Output));

        return new CriterionResult(
            criterion.Name, criterion.Command, criterion.Required,
            exitCode == criterion.ExpectedExitCode ? CriterionOutcome.Passed : CriterionOutcome.Failed,
            exitCode,
            exitCode == criterion.ExpectedExitCode ? null : Trim(result.Output ?? result.Error),
            criterion.Origin,
            criterion.AlreadyPassing) { Output = result.Output ?? result.Error };
    }

    private static bool TryExitCode(ToolResult result, out int exitCode)
    {
        exitCode = 0;
        if (!result.Metadata.TryGetValue("exitCode", out var raw) || raw is null)
            return false;

        switch (raw)
        {
            case int i:
                exitCode = i;
                return true;
            case long l:
                exitCode = (int)l;
                return true;
            case JsonElement { ValueKind: JsonValueKind.Number } element when element.TryGetInt32(out var e):
                exitCode = e;
                return true;
            default:
                return int.TryParse(raw.ToString(), out exitCode);
        }
    }

    /// <summary>
    /// Enough of the output to act on, and no more. The whole thing is already capped by the tool;
    /// this keeps a failing build from filling the run report with its own log.
    /// </summary>
    private const int MaxDetailChars = 1200;

    internal static string Trim(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return "";

        var trimmed = text!.Trim();
        return trimmed.Length <= MaxDetailChars
            ? trimmed
            : trimmed[..MaxDetailChars] + $"\n… (showing the first {MaxDetailChars} of {trimmed.Length} characters)";
    }
}
