namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Core.Memory;
using Xunit;

/// <summary>
/// A run somebody stopped is not written into what the project has decided.
///
/// <para><b>Measured 2026-09-24 10:29, run 1942b0.</b> Every step of every run opens with a block
/// headed "What this project has already decided and done". In that run it held eighteen entries
/// for a single prompt, and thirteen of them were the same sentence:</para>
///
/// <code>
/// - [outcome] "Check the claims in Docs/wiki/ against the code in src/. Work one page…"
///     — Cancelled: the run was stopped before it finished
/// </code>
///
/// <para>Indistinguishable from one another, because the titles truncate to the same words. Memory
/// is read into the prompt of every later step, so each of those is a permanent charge on the
/// window and on the bill — and none of them says anything about the project. Pressing stop is a
/// fact about the person.</para>
///
/// <para>The sibling rule was already here for a run with no terminal event at all: <i>"Saying
/// nothing is better than recording a conclusion it never reached."</i> A cancellation reaches a
/// terminal event and still no conclusion, so it slipped past.</para>
/// </summary>
public sealed class ACancelledRunConcludedNothingTests
{
    private sealed class Nowhere : IRunStore
    {
        public Task SaveAsync(RunRecord record, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteAsync(Guid runId, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<RunSummary>> LoadSummariesAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RunSummary>>(Array.Empty<RunSummary>());
        public Task<RunRecord?> LoadAsync(Guid runId, CancellationToken ct)
            => Task.FromResult<RunRecord?>(null);
        public Task<IReadOnlyList<RunRecord>> LoadAllAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<RunRecord>>(Array.Empty<RunRecord>());
    }

    private sealed class Remembering : IMemoryStore
    {
        private readonly List<MemoryEntry> _entries = new();
        public IReadOnlyList<MemoryEntry> Entries { get { lock (_entries) return _entries.ToArray(); } }

        public Task AppendAsync(MemoryEntry entry, CancellationToken ct)
        {
            lock (_entries) _entries.Add(entry);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<MemoryEntry>> LoadAllAsync(CancellationToken ct)
            => Task.FromResult(Entries);
    }

    private static readonly Guid Task1 = Guid.NewGuid();
    private static readonly Guid Run1 = Guid.NewGuid();

    private static WorkEvent Event(EventKind kind, string summary, string? payload = null)
        => new(Guid.NewGuid(), Task1, Run1, DateTimeOffset.Now, kind, summary, payload);

    /// <summary>
    /// A run that wrote a file and then ended however the caller says. The artifact matters: it is
    /// what makes the run worth remembering at all, and it is exactly the shape the real
    /// cancellations had — "Cancelled … Changed: Docs/DRIFT_ollama.md".
    /// </summary>
    private static async IAsyncEnumerable<WorkEvent> ARunThatWroteAFile(EventKind ending, RunOutcomeKind outcome, string reason)
    {
        yield return Event(EventKind.IntentReceived, "Intent: check the claims in Docs/wiki/");
        yield return Event(EventKind.ArtifactProduced, "Docs/DRIFT_ollama.md");
        yield return Event(ending, reason, WorkEventPayload.OutcomePayload(outcome, reason));
        await Task.CompletedTask;
    }

    private static async Task<IReadOnlyList<MemoryEntry>> RememberedAsync(IAsyncEnumerable<WorkEvent> stream)
    {
        var memory = new Remembering();
        var recorder = new RunRecorder(new Nowhere(), memory, Guid.NewGuid());

        await foreach (var _ in recorder.RecordAsync(stream, CancellationToken.None)) { }

        return memory.Entries;
    }

    /// <summary>THE ONE THAT MATTERS.</summary>
    [Fact]
    public async Task A_stopped_run_leaves_nothing_behind_for_the_next_one_to_read()
    {
        var remembered = await RememberedAsync(ARunThatWroteAFile(
            EventKind.TaskFailed, RunOutcomeKind.Cancelled, "the run was stopped before it finished"));

        Assert.DoesNotContain(remembered, e => string.Equals(e.Kind, MemoryKind.Outcome, StringComparison.Ordinal));
    }

    /// <summary>
    /// THE BOUNDARY, and the half that matters more. What a run CONCLUDED is the thing a later run
    /// most wants to know, and it was the one thing never written down until 2026-09-08. Dropping
    /// cancellations must not take it with them.
    /// </summary>
    [Fact]
    public async Task A_run_that_finished_is_still_remembered()
    {
        var remembered = await RememberedAsync(ARunThatWroteAFile(
            EventKind.TaskCompleted, RunOutcomeKind.Completed, "done"));

        var outcome = Assert.Single(remembered,
            e => string.Equals(e.Kind, MemoryKind.Outcome, StringComparison.Ordinal));

        Assert.Contains("Docs/DRIFT_ollama.md", outcome.Content, StringComparison.Ordinal);
    }

    /// <summary>
    /// And a run that FAILED is remembered too. A failure concluded something — it found out that
    /// this does not work — which is the difference between it and a cancellation.
    /// </summary>
    [Fact]
    public async Task A_run_that_failed_is_still_remembered()
    {
        var remembered = await RememberedAsync(ARunThatWroteAFile(
            EventKind.TaskFailed, RunOutcomeKind.Failed, "the context window filled up"));

        Assert.Contains(remembered, e => string.Equals(e.Kind, MemoryKind.Outcome, StringComparison.Ordinal));
    }
}
