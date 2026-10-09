namespace Enactive.Agents;

using System.Text;
using System.Text.Json;
using Enactive.Core;
using Enactive.Core.Artifacts;
using Enactive.Core.Chat;
using Enactive.Core.Context;
using Enactive.Core.Events;
using Enactive.Core.Execution;
using Enactive.Core.Providers;
using Enactive.Core.Tools;

/// <summary>
/// Holds a step's changes to the files the run found against what the request says may be changed
/// (<see cref="ForbiddenTaskEffect.FileChange"/>) - at the change, not at the step's review.
///
/// <para><b>Why.</b> Run bb77e810, 2026-10-09: "Do not change any source file to make a test pass". A step titled
/// "Restore behaviour, confirm test passes" opened with "I need to fix the actual bug in GetBestMove", changed the
/// source, then rewrote 21 older tests that the change broke - twenty minutes, a broken build, and no review until the
/// step would end. The limit was in the request all along; only the step's review read it.</para>
///
/// <para><b>Why a judgement and not a rule.</b> The same request asked for the source to be broken on purpose and put
/// back. A change to a source file was the request in one step and against it in the next; only what the change is
/// FOR tells them apart. So the first change a step makes to a file that existed before the run is put to the planning
/// model with the limit's own words, the step, what the step said it was doing, and the change - and refused when it
/// goes against them. Once allowed, that file is the step's for the rest of the step: a step is not asked about every
/// edit, and a file the run made itself is never asked about.</para>
///
/// <para>When the question cannot be put - no answer, an unusable one - the change goes ahead, as it did before this
/// existed, and the step's review still judges it. A guard that stops work because a model did not answer would be a
/// new way to fail a run, not a way to keep it to the request.</para>
/// </summary>
internal sealed class ChangeLimitGuard(
    string request,
    IReadOnlyList<string> limits,
    IChatProvider provider,
    ModelRef model,
    RunBudget budget,
    int outputBudget)
{
    /// <summary>
    /// How many times one step is asked about one file before the limit stands without asking. A step refused three
    /// times is offering the same change in new words; asking again costs a review each time and says nothing new.
    /// </summary>
    internal const int AsksPerFile = 3;

    /// <summary>How much of a change, and of the file as it is now, the question shows.</summary>
    internal const int ShownChars = 4000;

    private readonly object _gate = new();
    // By step, file, and whether the change takes the file away: an edit allowed is not a removal allowed. Run 7f3435,
    // 2026-10-09: a step's edit of a test file the run found was allowed, and the same step then deleted the file -
    // 24,733 bytes of older tests - without the question being put again.
    private readonly HashSet<(int?, string, bool)> _allowed = [];
    private readonly Dictionary<(int?, string, bool), int> _refused = [];

    /// <summary>What a check decided: the refusal the step is told, or null to go ahead - and what asking cost.</summary>
    internal sealed record Decision(string? Refusal, TokenUsage Usage, string? Note = null)
    {
        public static Decision GoAhead { get; } = new(null, TokenUsage.None);
    }

    private sealed record Verdict(bool Allow, string Reason);

    public IReadOnlyList<string> Limits => limits;

    /// <param name="saidByStep">What the step said in the turn that made the call - its own account of what it is doing.</param>
    public async Task<Decision> CheckAsync(int? stepNo, string? stepTitle, string? saidByStep, ToolCall call,
        ToolDefinition? definition, IArtifactScope store, string workspaceRoot, CancellationToken ct)
    {
        if (definition?.ChangedPathArguments is not { Count: > 0 } arguments || definition.RestoresRunStart) return Decision.GoAhead;

        var removed = RemovedPaths(call, definition).Select(ShellLookup.Normal).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var asked = new List<(string Path, string? Now)>();
        foreach (var path in WriteBoundary.PathsOf(call, arguments))
        {
            var rel = ShellLookup.Normal(path);
            // The engine's own folder - the scratch, its notes - is no file of the request's.
            if (rel.Length == 0 || rel.Equals(WorkspaceGuard.ReservedFolder, StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith(WorkspaceGuard.ReservedFolder + "/", StringComparison.OrdinalIgnoreCase))
                continue;
            lock (_gate)
                if (_allowed.Contains((stepNo, rel.ToLowerInvariant(), removed.Contains(rel)))) continue;

            string full;
            try { full = WorkspaceGuard.ResolveInside(workspaceRoot, rel); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { continue; }   // the ordinary gate says why
            if (!File.Exists(full) && !Directory.Exists(full)) continue;                       // new: nothing the run found
            if ((await store.BeforeRunAsync(rel, ct)).State == BeforeRunState.Absent) continue; // the run made it

            asked.Add((rel, File.Exists(full) ? Start(full) : null));
        }
        if (asked.Count == 0) return Decision.GoAhead;

        lock (_gate)
            if (asked.FirstOrDefault(a => _refused.GetValueOrDefault((stepNo, a.Path.ToLowerInvariant(), removed.Contains(a.Path))) >= AsksPerFile) is { Path: { } spent })
                return new($"'{spent}' was not changed: this step's changes to it were refused {AsksPerFile} times against what the "
                           + $"request says may be changed ({Quoted()}), and it is not asked again. Leave it as it is, or hand on "
                           + "why the step cannot be done without changing it.", TokenUsage.None);

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(Instruction),
            ChatMessage.User(Question(stepTitle, saidByStep, call, asked, removed))
        };
        var round = await StructuredAnswer.AskAsync(provider, messages,
            current => new ChatRequest(model.Model, current, Temperature: 0, Purpose: GenerationPurpose.Planning,
                OutputTokenLimit: Math.Max(1, outputBudget)),
            (answer, _) => Read(answer),
            errors => StructuredAnswer.Listed(errors, "Return ONLY JSON {\"allow\":true|false,\"reason\":\"...\"}."),
            budget.TurnExhaustedAfter, requireComplete: true, ct);

        if (round.Value is not { } verdict)
            return new(null, round.Usage, $"A change to {string.Join(", ", asked.Select(a => a.Path))} went ahead unchecked against "
                       + $"the request's limit on changes: {round.Shortfall("the question")}.");

        lock (_gate)
            foreach (var (path, _) in asked)
            {
                var key = (stepNo, path.ToLowerInvariant(), removed.Contains(path));
                if (verdict.Allow) _allowed.Add(key);
                else _refused[key] = _refused.GetValueOrDefault(key) + 1;
            }
        return verdict.Allow
            ? new(null, round.Usage)
            : new($"'{string.Join("', '", asked.Select(a => a.Path))}' was not changed: the request limits what may be changed "
                  + $"({Quoted()}), and this change goes against it - {verdict.Reason}. A file this run changed on purpose "
                  + "is put back as it was with restore_file.", round.Usage);
    }

    /// <summary>What asking cost, as the run counts it: against the run's budget, as a review.</summary>
    internal WorkEvent UsageEvent(Guid taskId, Guid runId, int? stepNo, TokenUsage usage)
    {
        budget.TokensUsed(usage.Prompt, usage.Completion);
        return new(Guid.NewGuid(), taskId, runId, DateTimeOffset.UtcNow, EventKind.UsageReported,
            $"tokens: {usage.Prompt} in, {usage.Completion} out" + (usage.Cached is > 0 ? $" ({usage.Cached} cached)" : "")
            + $" ({model.ProviderId}/{model.Model}, {WorkEventPayload.WorkPurpose.Review})",
            WorkEventPayload.UsagePayload(usage.Prompt, usage.Completion, stepNo, model.ProviderId, model.Model,
                WorkEventPayload.WorkPurpose.Review, usage.Cached, usage.Created));
    }

    private const string Instruction = """
        A step of a plan is about to change a file that was in the workspace before the run. The request limits what may
        be changed, in its own words below. Decide whether the request allows THIS change, made in THIS step, for what the
        step is doing. A limit forbids what it names - judge by what the change is for: the same file may be changed for
        one purpose the request asks for and not for another it forbids. A change the request itself asks for is allowed.
        Return ONLY JSON {"allow":true|false,"reason":"..."}; the reason is said to the step when the change is refused.
        """;

    private string Question(string? stepTitle, string? saidByStep, ToolCall call, IReadOnlyList<(string Path, string? Now)> asked,
        IReadOnlySet<string> removed)
    {
        var sb = new StringBuilder();
        sb.AppendLine("The request:").AppendLine(request).AppendLine();
        sb.AppendLine("What the request says may be changed:");
        foreach (var limit in limits) sb.AppendLine($"- \"{limit}\"");
        sb.AppendLine();
        sb.AppendLine("The step: " + (stepTitle ?? "(not a planned step)"));
        if (!string.IsNullOrWhiteSpace(saidByStep))
            sb.AppendLine("What the step said as it made the change:").AppendLine(Cut(saidByStep.Trim()));
        sb.AppendLine();
        sb.AppendLine($"The change: {call.Name} {Cut(call.ArgumentsJson ?? "")}");
        foreach (var gone in asked.Where(a => removed.Contains(a.Path)))
            sb.AppendLine($"It takes {gone.Path} away: the file is gone from where it was, whole.");
        foreach (var (path, now) in asked.Where(a => a.Now is not null))
            sb.AppendLine().AppendLine($"{path} as it is now:").AppendLine(now);
        return sb.ToString();
    }

    private static (Verdict? Value, IReadOnlyList<string> Errors) Read(string answer)
    {
        if (ModelText.ExtractJsonObject(ModelText.StripThink(answer)) is not { } json)
            return (null, ["there is no JSON object in the answer"]);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("allow", out var allow) || allow.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return (null, ["\"allow\" must be true or false"]);
            var reason = root.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString()!.Trim() : "";
            if (allow.ValueKind == JsonValueKind.False && reason.Length == 0)
                return (null, ["a refusal needs its \"reason\": it is what the step is told"]);
            return (new Verdict(allow.ValueKind == JsonValueKind.True, reason), []);
        }
        catch (JsonException) { return (null, ["the answer is not valid JSON"]); }
    }

    /// <summary>
    /// The paths a call takes away: every path a deleting tool names, and where a moving tool moves FROM - its first
    /// changed path (ReadLedger reads a move the same way). The file is gone from there either way.
    /// </summary>
    internal static IEnumerable<string> RemovedPaths(ToolCall call, ToolDefinition? definition)
        => definition is { ChangedPathArguments: { Count: > 0 } arguments, FileCoverage: var coverage }
            ? coverage switch
            {
                FileCoverageBehavior.Delete => WriteBoundary.PathsOf(call, arguments),
                FileCoverageBehavior.Move => WriteBoundary.PathsOf(call, [arguments[0]]),
                _ => []
            }
            : [];

    private string Quoted() => string.Join("; ", limits.Select(l => $"\"{l}\""));

    private static string Cut(string text) => text.Length <= ShownChars ? text : text[..ShownChars] + " …(cut)";

    private static string? Start(string full)
    {
        try
        {
            using var reader = new StreamReader(full);
            var buffer = new char[ShownChars + 1];
            var read = reader.ReadBlock(buffer, 0, buffer.Length);
            return Cut(new string(buffer, 0, read));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }
}
