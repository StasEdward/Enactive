namespace Enactive.Agents;

using System.Text.Json;
using System.Text.Json.Serialization;
using Enactive.Core.Permissions;

/// <summary>A question a run stopped at, and - once somebody has given it - the answer.</summary>
public sealed record ParkedDecision(
    Guid TaskId,
    Guid RequestId,
    string Topic,
    string Detail,
    string FullText,
    string? Subject,
    IReadOnlyList<DecisionOption> Options,
    string? RecommendedOptionId,
    DateTimeOffset AskedAt,
    string? Answer = null,
    DateTimeOffset? AnsweredAt = null)
{
    public bool Answered => Answer is not null;
}

/// <summary>
/// The questions runs have stopped at, and their answers, kept in the workspace so they outlive
/// the process that asked them.
///
/// <para><b>Why.</b> A decision was a synchronous wait inside the tool loop. A run with somebody
/// watching waited for a click; a run with nobody - on a schedule, in the background - was told
/// "no" on the spot and ended Incomplete, and the question was gone: to get the answer "yes" in, the
/// whole task had to be started again by hand, attended. The plan's Phase 1.8 asks for the third
/// thing: a run that STOPS at the question, keeps it, and goes on when it is answered.</para>
///
/// <para><b>What an answer is for.</b> Exactly the question it answered, asked again. A run that
/// resumes redoes the step it stopped in, and when it comes to the same question - the same tool,
/// the same arguments, word for word - the recorded answer is given without asking. A DIFFERENT
/// question is a new question: an approval of one command is not an approval of the next, so a
/// resumed step that decides to run something else stops again and asks about that.</para>
///
/// <para>One file per task under <c>.enactive/decisions</c>, beside the engine's other own data,
/// because the workspace is what a resumed run comes back to.</para>
/// </summary>
public sealed class DecisionLedger(string workspaceRoot)
{
    internal const string Folder = ".enactive/decisions";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly object _gate = new();

    private string FileOf(Guid taskId) => Path.Combine(workspaceRoot, Folder, $"{taskId:N}.json");

    /// <summary>Every question recorded for this task, answered or not, oldest first.</summary>
    public IReadOnlyList<ParkedDecision> For(Guid taskId)
    {
        lock (_gate) return Read(taskId);
    }

    /// <summary>Every question, in every task in this workspace, that is still waiting for somebody.</summary>
    public IReadOnlyList<ParkedDecision> Pending()
    {
        lock (_gate)
        {
            var folder = Path.Combine(workspaceRoot, Folder);
            if (!Directory.Exists(folder)) return [];
            return Directory.GetFiles(folder, "*.json")
                .Select(f => Guid.TryParse(Path.GetFileNameWithoutExtension(f), out var id) ? id : Guid.Empty)
                .Where(id => id != Guid.Empty)
                .SelectMany(Read)
                .Where(d => !d.Answered)
                .OrderBy(d => d.AskedAt)
                .ToArray();
        }
    }

    /// <summary>Records a question a run stopped at. The same question already waiting is not recorded twice.</summary>
    public ParkedDecision Park(DecisionRequest request)
    {
        lock (_gate)
        {
            var all = Read(request.TaskId).ToList();
            if (all.FirstOrDefault(d => !d.Answered && Same(d, request)) is { } waiting) return waiting;
            var parked = new ParkedDecision(request.TaskId, request.Id, request.Topic, request.Detail, request.FullText,
                request.Subject, request.Options, request.RecommendedOptionId, DateTimeOffset.UtcNow);
            all.Add(parked);
            Write(request.TaskId, all);
            return parked;
        }
    }

    /// <summary>
    /// Gives a waiting question its answer. False when there is no such question, it is already
    /// answered, or the answer is not one of the options it offered - an answer to a question that
    /// was not asked authorises nothing.
    /// </summary>
    public bool Answer(Guid taskId, Guid requestId, string optionId)
    {
        lock (_gate)
        {
            var all = Read(taskId).ToList();
            var index = all.FindIndex(d => d.RequestId == requestId);
            if (index < 0 || all[index].Answered
                || !all[index].Options.Any(o => string.Equals(o.Id, optionId, StringComparison.OrdinalIgnoreCase)))
                return false;
            all[index] = all[index] with { Answer = optionId, AnsweredAt = DateTimeOffset.UtcNow };
            Write(taskId, all);
            return true;
        }
    }

    /// <summary>The recorded answer to exactly this question, or null.</summary>
    public DecisionOutcome? Answered(DecisionRequest request)
    {
        lock (_gate)
            return Read(request.TaskId).LastOrDefault(d => d.Answered && Same(d, request)) is { } answered
                ? new DecisionOutcome(answered.Answer!, "answered while the run waited for you")
                : null;
    }

    /// <summary>Forgets a task's questions - when it has reached an end.</summary>
    public void Forget(Guid taskId)
    {
        lock (_gate)
        {
            try { File.Delete(FileOf(taskId)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The same question: what it asks about, in full, and about which tool.</summary>
    private static bool Same(ParkedDecision parked, DecisionRequest request)
        => parked.Topic == request.Topic && parked.FullText == request.FullText && parked.Subject == request.Subject;

    private List<ParkedDecision> Read(Guid taskId)
    {
        var file = FileOf(taskId);
        try
        {
            return File.Exists(file)
                ? JsonSerializer.Deserialize<List<ParkedDecision>>(File.ReadAllText(file), Json) ?? []
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    private void Write(Guid taskId, List<ParkedDecision> all)
    {
        var file = FileOf(taskId);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        // Whole or not at all: a half-written answer would authorise something nobody said.
        var temp = file + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(all, Json));
        File.Move(temp, file, overwrite: true);
    }
}

/// <summary>
/// The answer when nobody is here NOW, but somebody will be: not "no", but "wait". The question is
/// kept and the run stops at it, to go on when it is answered - see <see cref="DecisionLedger"/>.
///
/// <para>The other two answers for a run with nobody watching are both worse for some tasks.
/// <see cref="UnattendedDecisionHandler"/> says no, which is right for a scheduled job that must
/// never do what nobody approved, and ends the run without the thing it needed.
/// <see cref="BackgroundDecisionHandler"/> says no and leaves a note. This says neither: it can
/// approve, just not yet.</para>
/// </summary>
public sealed class ParkingDecisionHandler : IDecisionHandler
{
    /// <summary>Yes: somebody will answer, later. Tools that need an approval stay on offer.</summary>
    public bool CanApprove => true;

    public Task<DecisionOutcome> RequestAsync(DecisionRequest request, CancellationToken ct)
        => throw new DecisionPendingException(request);
}

/// <summary>A run stopping at a question nobody is here to answer. Unwinds the run like a cancellation, because that is what it is until somebody answers.</summary>
public sealed class DecisionPendingException(DecisionRequest request)
    : OperationCanceledException("Waiting for a decision: " + request.Topic)
{
    public DecisionRequest Request { get; } = request;
}

/// <summary>
/// The run's decisions, through the ledger: a question already answered while the run waited is
/// given its answer; a question the handler parks is written down before the run stops.
/// </summary>
internal sealed class LedgeredDecisions(IDecisionHandler inner, DecisionLedger ledger) : IDecisionHandler
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, ParkedDecision> _parked = new();

    public bool CanApprove => inner.CanApprove;

    public async Task<DecisionOutcome> RequestAsync(DecisionRequest request, CancellationToken ct)
    {
        if (ledger.Answered(request) is { } answered) return answered;
        try { return await inner.RequestAsync(request, ct); }
        catch (DecisionPendingException)
        {
            _parked[request.TaskId] = ledger.Park(request);
            throw;
        }
    }

    /// <summary>The question this task's run stopped at, taken once - by the run that stopped.</summary>
    public ParkedDecision? TakeParked(Guid taskId) => _parked.TryRemove(taskId, out var parked) ? parked : null;
}
