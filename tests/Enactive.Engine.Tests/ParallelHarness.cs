namespace Enactive.Engine.Tests;

using System.Collections.Concurrent;
using Enactive.Core.Chat;
using Enactive.Core.Permissions;
using Enactive.Core.Providers;

/// <summary>
/// A chat provider for runs where more than one step is in flight.
///
/// <para><see cref="FakeChatProvider"/> cannot serve one: it answers from a <c>Queue</c> by position,
/// with no lock on the queue or on its request list, so two concurrent steps race for turns that
/// were written for one of them and the run is different every time. Position is the wrong key once
/// order is not guaranteed.</para>
///
/// <para>This answers by WHAT WAS ASKED. The orchestrator tells a step which step it is — "Proceed
/// with this step of the plan: <c>&lt;title&gt;</c>" — so a script is written per step title and each
/// step gets its own turns however they interleave. Everything is under one lock, and every request
/// is kept for the assertions that matter here: which conversation a step was given, and what its
/// siblings told it.</para>
/// </summary>
public sealed class ByStepChatProvider : IChatProvider
{
    private const string StepMarker = "Proceed with this step of the plan: ";

    private readonly object _gate = new();
    private readonly string _plan;
    private readonly Dictionary<string, Queue<Turn>> _steps = new(StringComparer.Ordinal);
    private readonly List<ChatRequest> _requests = new();
    private bool _planned;

    public ByStepChatProvider(string plan) => _plan = plan;

    /// <summary>The turns this step takes, in order, each time it is asked.</summary>
    public ByStepChatProvider Step(string title, params Turn[] turns)
    {
        lock (_gate)
            _steps[title] = new Queue<Turn>(turns);
        return this;
    }

    /// <summary>What a step says when its script runs out.</summary>
    public Turn WhenExhausted { get; set; } = Turn.Says("Done.");

    /// <summary>Every request made, in the order they arrived.</summary>
    public IReadOnlyList<ChatRequest> Requests
    {
        get { lock (_gate) return _requests.ToArray(); }
    }

    /// <summary>Every request whose conversation belongs to this step.</summary>
    public IReadOnlyList<ChatRequest> RequestsFor(string title)
    {
        lock (_gate)
            return _requests.Where(r => TitleOf(r) == title).ToArray();
    }

    /// <summary>How many requests were in flight at once, at the busiest moment.</summary>
    public int PeakConcurrency { get; private set; }

    private int _inFlight;

    /// <summary>Which step this conversation belongs to, from the instruction the engine gave it.</summary>
    private static string? TitleOf(ChatRequest request)
    {
        for (var i = request.Messages.Count - 1; i >= 0; i--)
        {
            var content = request.Messages[i].Content;
            if (content is null)
                continue;

            var at = content.IndexOf(StepMarker, StringComparison.Ordinal);
            if (at < 0)
                continue;

            var from = at + StepMarker.Length;
            var end = content.IndexOf('\n', from);
            return (end < 0 ? content[from..] : content[from..end]).Trim();
        }

        return null;
    }

    public int? ContextWindow(ChatRequest request) => null;

    public async Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        var turn = Next(RequestSnapshot.Of(request));

        // A real provider takes time, and two steps that never overlap prove nothing about a
        // dispatcher meant to overlap them. Leave in a finally: a cancelled delay skipped it.
        try
        {
            await Task.Delay(5, ct);
        }
        finally
        {
            Leave();
        }

        return new ChatCompletion(
            new ChatMessage(ChatRole.Assistant, turn.Text, turn.Calls), turn.FinishReason,
            turn.PromptTokens, turn.CompletionTokens, turn.Thinking);
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
        ChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var turn = Next(RequestSnapshot.Of(request));

        // Left in a finally: a consumer that stops early - a cancelled step, a stream abandoned by
        // RunawayReply - disposes the enumerator after a yield, and Leave after the last one never
        // ran, leaving PeakConcurrency counting a request that had ended.
        try
        {
            await Task.Delay(5, ct);

            if (turn.Text is { Length: > 0 } text)
                yield return new TextDelta(text);

            if (turn.Calls is { Count: > 0 } calls)
                for (var i = 0; i < calls.Count; i++)
                    yield return new ToolCallDelta(i, calls[i].Id, calls[i].Name, calls[i].ArgumentsJson);

            if (turn.PromptTokens is not null || turn.CompletionTokens is not null)
                yield return new UsageDelta(turn.PromptTokens, turn.CompletionTokens);

            yield return new FinishDelta(turn.FinishReason);
        }
        finally
        {
            Leave();
        }
    }

    private Turn Next(ChatRequest request)
    {
        lock (_gate)
        {
            _requests.Add(request);

            _inFlight++;
            if (_inFlight > PeakConcurrency)
                PeakConcurrency = _inFlight;

            // The planner asks first and once, before any step exists.
            if (!_planned)
            {
                _planned = true;
                return Turn.Says(_plan);
            }

            if (TitleOf(request) is { } title
                && _steps.TryGetValue(title, out var script)
                && script.Count > 0)
                return script.Dequeue();

            return WhenExhausted;
        }
    }

    private void Leave()
    {
        lock (_gate)
            _inFlight--;
    }
}

/// <summary>
/// A decision handler that records whether two approval cards were ever on screen at once.
///
/// <para>The orchestrator serialises them behind a semaphore, on the grounds that a person cannot
/// answer two questions at the same moment. Nothing checked it, and the check has to be made from
/// INSIDE the handler: the gate is what a caller waits on, so only the thing being called can say
/// whether two callers were ever inside it together.</para>
/// </summary>
public sealed class ConcurrencyWatchingDecisions : IDecisionHandler
{
    private readonly string _answer;
    private int _inside;

    public ConcurrencyWatchingDecisions(string answer = "allow") => _answer = answer;

    /// <summary>The most cards open at once. More than 1 means the gate did not hold.</summary>
    public int PeakOpenCards;

    /// <summary>Every request that reached the handler.</summary>
    public ConcurrentQueue<DecisionRequest> Requests { get; } = new();

    public async Task<DecisionOutcome> RequestAsync(DecisionRequest request, CancellationToken ct)
    {
        Requests.Enqueue(request);

        var open = Interlocked.Increment(ref _inside);
        InterlockedMax(ref PeakOpenCards, open);

        // Long enough that a second card, if the gate let one through, would overlap this one.
        await Task.Delay(25, ct);

        Interlocked.Decrement(ref _inside);
        return new DecisionOutcome(_answer);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        do
        {
            seen = Volatile.Read(ref target);
            if (value <= seen)
                return;
        }
        while (Interlocked.CompareExchange(ref target, value, seen) != seen);
    }
}

/// <summary>
/// A reviewer that decides by WHICH STEP it is looking at, so two concurrent steps can be given
/// different verdicts deterministically. The review prompt opens with "Step: &lt;title&gt;".
///
/// <para>Thread-safe, and it has to be: with two steps in flight, two reviews are in flight.</para>
/// </summary>
public sealed class VerdictByStepProvider : IChatProvider
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Turn> _verdicts = new(StringComparer.Ordinal);
    private readonly List<string> _asked = new();

    /// <summary>The verdict for a step whose title is not listed.</summary>
    public Turn Otherwise { get; set; } = Verdicts.Pass();

    public VerdictByStepProvider On(string title, Turn verdict)
    {
        lock (_gate)
            _verdicts[title] = verdict;
        return this;
    }

    /// <summary>Step titles this reviewer was asked about, in the order they arrived.</summary>
    public IReadOnlyList<string> Asked
    {
        get { lock (_gate) return _asked.ToArray(); }
    }

    public int? ContextWindow(ChatRequest request) => null;

    public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
    {
        var turn = Next(request);
        return Task.FromResult(new ChatCompletion(
            new ChatMessage(ChatRole.Assistant, turn.Text, turn.Calls), turn.FinishReason,
            turn.PromptTokens, turn.CompletionTokens, turn.Thinking));
    }

    public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
        ChatRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var turn = Next(request);
        await Task.CompletedTask;

        if (turn.Text is { Length: > 0 } text)
            yield return new TextDelta(text);

        yield return new FinishDelta(turn.FinishReason);
    }

    private Turn Next(ChatRequest request)
    {
        var title = TitleOf(request);

        lock (_gate)
        {
            _asked.Add(title ?? "(unknown)");
            return title is not null && _verdicts.TryGetValue(title, out var verdict)
                ? verdict
                : Otherwise;
        }
    }

    private static string? TitleOf(ChatRequest request)
    {
        const string marker = "Step: ";

        foreach (var message in request.Messages)
        {
            var content = message.Content;
            if (content is null || !content.StartsWith(marker, StringComparison.Ordinal))
                continue;

            var end = content.IndexOf('\n');
            return (end < 0 ? content[marker.Length..] : content[marker.Length..end]).Trim();
        }

        return null;
    }
}
