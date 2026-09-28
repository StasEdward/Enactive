namespace Enactive.Agents;

using System.Text.Json;
using System.Text.Json.Serialization;
using Enactive.Core.Chat;
using Enactive.Core.Execution;
using Enactive.Core.Tools;

/// <summary>
/// Where a step stopped at a question: its conversation as it stood, what it had done, and the
/// calls of the turn that had not run yet - enough to carry the step on from that point instead of
/// doing it again from its beginning.
/// </summary>
/// <param name="StepNo">The step, or null for a quick action.</param>
/// <param name="Pending">The call that asked, and the calls after it in the same turn: none of them ran.</param>
internal sealed record ParkedPosition(
    int? StepNo,
    IReadOnlyList<ChatMessage> Messages,
    IReadOnlyList<ExecutedAction> Actions,
    IReadOnlyList<ToolCall> Pending);

/// <summary>A once-only action a task has already taken: which tool, with exactly which arguments, and what it answered.</summary>
internal sealed record DoneOnce(string Tool, string Arguments, string? Output, DateTimeOffset At);

/// <summary>
/// What a task has already done that must not be done again when it is carried on, kept beside it
/// until it reaches an end.
///
/// <para><b>Why.</b> Carrying a task on used to mean doing the stopped step again from its
/// beginning. For a file edit that is survivable - the tools read before they write. For an engine
/// that sends email, posts, deploys, it is not: a step that sent a message and then stopped at a
/// question about its next action would, once answered, send the message again. Two things stop
/// that, one inside the other:</para>
/// <list type="number">
/// <item><b>The position.</b> A step that stops at a question keeps its conversation - every call it
/// made and what each answered - and is carried on FROM there, with the call that asked still to
/// be made. The model sees the message it sent; nothing is redone.</item>
/// <item><b>Once-only actions.</b> Where there is no position to carry on from - the process died
/// mid-step, and the step is redone from its boundary - a tool that declares
/// <see cref="ToolDefinition.OnceOnly"/> is not run again with the same arguments: the task already
/// took that action, and the recorded answer is given instead. Recorded the moment the action
/// succeeds, not at a boundary, because a crash does not wait for one.</item>
/// </list>
/// </summary>
internal sealed class TaskProgress(string workspaceRoot)
{
    internal const string Folder = ".enactive/progress";

    private sealed record State(List<DoneOnce> Done, List<ParkedPosition> Parked)
    {
        /// <summary>Files a step created, by step - written the moment they are created. See WriteBoundary.</summary>
        public List<OwnedFile>? Owned { get; init; }
    }

    internal sealed record OwnedFile(Guid Step, string Path);

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();

    private string FileOf(Guid taskId) => Path.Combine(workspaceRoot, Folder, $"{taskId:N}.json");

    public void Park(Guid taskId, ParkedPosition position)
    {
        lock (_gate)
        {
            var state = Read(taskId);
            state.Parked.RemoveAll(p => p.StepNo == position.StepNo);
            state.Parked.Add(position);
            Write(taskId, state);
        }
    }

    /// <summary>Whether this step stopped at a question and has not been carried on yet.</summary>
    public bool HasParked(Guid taskId, int? stepNo)
    {
        lock (_gate) return Read(taskId).Parked.Any(p => p.StepNo == stepNo);
    }

    /// <summary>Where this step stopped, taken once - by the run that carries it on.</summary>
    public ParkedPosition? TakeParked(Guid taskId, int? stepNo)
    {
        lock (_gate)
        {
            var state = Read(taskId);
            var parked = state.Parked.FirstOrDefault(p => p.StepNo == stepNo);
            if (parked is null) return null;
            state.Parked.Remove(parked);
            Write(taskId, state);
            return parked;
        }
    }

    public void RecordDone(Guid taskId, ToolCall call, string? output)
    {
        lock (_gate)
        {
            var state = Read(taskId);
            state.Done.Add(new DoneOnce(call.Name, Canonical(call.ArgumentsJson), output, DateTimeOffset.UtcNow));
            Write(taskId, state);
        }
    }

    /// <summary>The same once-only action, already taken by this task - same tool, same arguments - or null.</summary>
    public DoneOnce? DoneBefore(Guid taskId, ToolCall call)
    {
        lock (_gate)
        {
            var arguments = Canonical(call.ArgumentsJson);
            return Read(taskId).Done.LastOrDefault(d => d.Tool == call.Name && d.Arguments == arguments);
        }
    }

    /// <summary>
    /// A file this step created, and may therefore go on changing - recorded at once, not at a boundary,
    /// so a step restarted after a crash still owns what it made before it.
    /// </summary>
    public void Own(Guid taskId, Guid step, string path)
    {
        lock (_gate)
        {
            var state = Read(taskId);
            var owned = state.Owned ?? [];
            if (!owned.Any(o => o.Step == step && string.Equals(o.Path, path, StringComparison.OrdinalIgnoreCase)))
                owned.Add(new OwnedFile(step, path));
            Write(taskId, state with { Owned = owned });
        }
    }

    public IReadOnlySet<string> OwnedBy(Guid taskId, Guid step)
    {
        lock (_gate)
            return (Read(taskId).Owned ?? []).Where(o => o.Step == step).Select(o => o.Path)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public void Forget(Guid taskId)
    {
        lock (_gate)
        {
            try { File.Delete(FileOf(taskId)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// The arguments with their formatting taken out, and nothing else: the same action written with
    /// other whitespace is the same action. Anything that changes a value is a different action.
    /// </summary>
    internal static string Canonical(string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            return JsonSerializer.Serialize(doc.RootElement);
        }
        catch (JsonException) { return argumentsJson.Trim(); }
    }

    private State Read(Guid taskId)
    {
        try
        {
            var file = FileOf(taskId);
            if (File.Exists(file) && JsonSerializer.Deserialize<State>(File.ReadAllText(file), Json) is { } state)
                return new State(state.Done ?? [], state.Parked ?? []) { Owned = state.Owned };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new State([], []);
    }

    private void Write(Guid taskId, State state)
    {
        var file = FileOf(taskId);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(state, Json));
        File.Move(temp, file, overwrite: true);
    }
}
