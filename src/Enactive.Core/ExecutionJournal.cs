namespace Enactive.Core.Execution;

using System.Text;

/// <summary>How a tool call ended. Never ran is not the same as ran and failed.</summary>
public enum ActionOutcome
{
    /// <summary>It ran and the tool reported success.</summary>
    Succeeded,

    /// <summary>It ran and the tool reported failure — a non-zero exit, a missing file.</summary>
    Failed,

    /// <summary>It never ran: the role does not offer it, the policy blocked it, the user said no.</summary>
    Refused
}

/// <summary>
/// One tool call, as it actually happened.
/// </summary>
/// <param name="Arguments">Compacted for reading — this is evidence, not a replay log.</param>
public sealed record ExecutedAction(
    DateTimeOffset At,
    int? Step,
    string Tool,
    string Arguments,
    ActionOutcome Outcome,
    string? Output);

/// <summary>
/// What a step actually did, written down when it did it.
///
/// <para>The reviewer is asked to judge a step against "the ACTUAL commands run and their real
/// stdout/stderr/exit codes", and that evidence was assembled by reading back the model's own
/// conversation. Two things are wrong with reading it there. The conversation is the model's
/// WORKING MEMORY, and once it has to be shortened to fit a context window the tool results are
/// replaced by a stub — so on exactly the runs under most pressure, the reviewer was handed less and
/// never told. And a transcript is a record of what was SAID; what was DONE is a different fact that
/// happens to have been written in the same place.</para>
///
/// <para>So it is recorded separately, at the moment each call returns, and nothing that shortens a
/// prompt can touch it. A refusal is recorded too: "the user did not permit this" is exactly the
/// sort of thing a reviewer must not have to infer from silence.</para>
/// </summary>
public sealed class ExecutionJournal
{
    private readonly List<ExecutedAction> _actions = new();
    private readonly object _gate = new();

    /// <summary>Everything recorded so far, oldest first.</summary>
    public IReadOnlyList<ExecutedAction> Actions
    {
        get { lock (_gate) return _actions.ToArray(); }
    }

    /// <summary>
    /// A position to describe from later. A review judges the CURRENT attempt, so a retry starts
    /// from here rather than re-reading what an earlier, rejected attempt did.
    /// </summary>
    public int Mark()
    {
        lock (_gate) return _actions.Count;
    }

    public void Record(int? step, string tool, string arguments, ActionOutcome outcome, string? output)
    {
        lock (_gate)
            _actions.Add(new ExecutedAction(DateTimeOffset.UtcNow, step, tool, arguments, outcome, output));
    }

    /// <summary>Whether anything recorded from <paramref name="from"/> on used one of these tools.</summary>
    public bool UsedAny(IReadOnlyCollection<string> tools, int from = 0)
    {
        lock (_gate)
            for (var i = Math.Max(0, from); i < _actions.Count; i++)
                foreach (var tool in tools)
                    if (string.Equals(_actions[i].Tool, tool, StringComparison.OrdinalIgnoreCase))
                        return true;

        return false;
    }

    /// <summary>
    /// The evidence, in the shape the reviewer prompt has always been written for: what was asked,
    /// then what came back. Capped, because a reviewer prompt is not the place to send a whole
    /// build log — and the cap is stated rather than applied silently.
    /// </summary>
    public string Describe(int from = 0, int maxChars = 3000)
    {
        ExecutedAction[] slice;
        lock (_gate)
            slice = _actions.Skip(Math.Max(0, from)).ToArray();

        if (slice.Length == 0)
            return "(no tools were run in this step)";

        var sb = new StringBuilder();
        foreach (var action in slice)
        {
            sb.Append("-> ").Append(action.Tool).Append(' ').AppendLine(action.Arguments);

            var answer = action.Outcome switch
            {
                ActionOutcome.Refused => "REFUSED: " + (action.Output ?? "not permitted"),
                ActionOutcome.Failed => "ERROR: " + (action.Output ?? "failed"),
                _ => action.Output ?? "OK"
            };

            sb.Append("<- ").AppendLine(answer);
        }

        var text = sb.ToString().Trim();
        return text.Length > maxChars ? text[..maxChars] + "\n… (truncated)" : text;
    }
}
