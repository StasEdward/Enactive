namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.History;
using Xunit;

/// <summary>
/// The last two things read out of an English sentence.
///
/// <para>`PlanCreated` announced itself as "&lt;title&gt; — N steps: a | b", and everything that
/// wanted the plan took it apart again: the live step cards, the replayed ones, the window header. A
/// step whose own title contains " | " became two cards — and since events are attributed to cards
/// by position, everything after it landed on the wrong one. `ArtifactProduced` said
/// "FileSet: path", and the path was recovered by splitting on the first ": ", which is a guess
/// about a sentence rather than a fact about a file.</para>
///
/// <para>Both carry values now, and the sentences stay for display. The prose readers remain for
/// runs recorded before the payloads existed, which is the only reason they still work.</para>
/// </summary>
public sealed class PlanPayloadTests
{
    private static WorkEvent Ev(EventKind kind, string summary, string? payload = null)
        => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, kind, summary, payload);

    private static RunEventRecord Stored(string kind, string summary, int? step = null, string? payload = null)
        => new(DateTimeOffset.UtcNow, kind, summary, step, payload);

    private static RunRecord Record(params RunEventRecord[] events)
        => new(Guid.NewGuid(), Guid.NewGuid(), "a run", "a-model",
               DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow, "Completed",
               events, Array.Empty<string>(), Array.Empty<string>());

    // ── the plan ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_step_title_containing_the_old_separator_is_still_one_step()
    {
        var steps = new[] { "Fix the parser | and its tests", "Ship it" };

        var ev = Ev(EventKind.PlanCreated,
            "Two things — 2 steps: " + string.Join(" | ", steps),
            WorkEventPayload.PlanPayload("Two things", steps));

        Assert.Equal(steps, ev.PlanSteps());
        Assert.Equal("Two things", ev.PlanTitle());
    }

    // The same event read the old way, to show what the payload is for: three "steps" out of two.
    [Fact]
    public void The_sentence_alone_would_have_split_it_into_three()
    {
        var steps = new[] { "Fix the parser | and its tests", "Ship it" };
        var summary = "Two things — 2 steps: " + string.Join(" | ", steps);

        var fromProse = summary["Two things — 2 steps: ".Length..].Split(" | ");

        Assert.Equal(3, fromProse.Length);
    }

    [Fact]
    public void A_plan_title_containing_a_dash_keeps_all_of_it()
    {
        var ev = Ev(EventKind.PlanCreated,
            "Build — and test — the thing — 1 steps: do it",
            WorkEventPayload.PlanPayload("Build — and test — the thing", new[] { "do it" }));

        Assert.Equal("Build — and test — the thing", ev.PlanTitle());
    }

    [Fact]
    public void Replay_builds_its_cards_from_the_payload()
    {
        var steps = new[] { "Write the guide | in two parts", "Check it" };

        var record = Record(
            Stored(nameof(EventKind.PlanCreated),
                   "Guides — 2 steps: " + string.Join(" | ", steps),
                   payload: WorkEventPayload.PlanPayload("Guides", steps)),
            Stored(nameof(EventKind.StepStarted), "[1/2]", 1),
            Stored(nameof(EventKind.StepStarted), "[2/2]", 2));

        var segments = RunReplayPlan.Segments(record);

        Assert.Equal(2, segments.Count);
        Assert.Equal(steps, segments.Select(s => s.Title).ToArray());
    }

    // A run from before the payload has only the sentence, and still rebuilds.
    [Fact]
    public void Replay_still_reads_a_run_recorded_before_the_payload()
    {
        var record = Record(
            Stored(nameof(EventKind.PlanCreated), "Guides — 2 steps: first | second"),
            Stored(nameof(EventKind.StepStarted), "[1/2]", 1),
            Stored(nameof(EventKind.StepStarted), "[2/2]", 2));

        Assert.Equal(new[] { "first", "second" }, RunReplayPlan.Segments(record).Select(s => s.Title).ToArray());
    }

    // ── the artifact ──────────────────────────────────────────────────────────────────

    [Fact]
    public void An_artifact_carries_its_path_its_kind_and_its_step()
    {
        var ev = Ev(EventKind.ArtifactProduced, "FileSet: notes/a: b.md",
                    WorkEventPayload.ArtifactPayload("FileSet", "notes/a: b.md", 3));

        Assert.Equal("notes/a: b.md", ev.ArtifactPath());
        Assert.Equal("FileSet", ev.ArtifactKindName());

        // The step still rides along — an artifact belongs to the step that produced it, and that is
        // how every consumer attributes one.
        Assert.Equal(3, ev.StepNo());
    }

    // ── and the engine actually attaches them ─────────────────────────────────────────

    [Fact]
    public async Task The_engine_puts_the_plan_titles_in_the_event()
    {
        using var fx = new EngineFixture();

        const string plan = """
            {"disposition":"task","title":"Two things",
             "steps":[{"title":"Fix the parser | and its tests","dependsOn":[]},
                      {"title":"Ship it","dependsOn":[0]}]}
            """;

        var events = await fx.RunAsync(fx.Build(new FakeChatProvider(Turn.Says(plan))), "do two things");

        var created = Assert.Single(events.OfKind(EventKind.PlanCreated));
        Assert.Equal("Two things", created.PlanTitle());
        Assert.Equal(new[] { "Fix the parser | and its tests", "Ship it" }, created.PlanSteps());
    }

    [Fact]
    public async Task The_engine_puts_the_artifact_path_in_the_event()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write the notes"}"""),
            Turn.Calls1("write_file", """{"path":"notes/a.md","content":"the notes"}""", "c1"),
            Turn.Says("Done."));

        var events = await fx.RunAsync(fx.Build(provider), "write the notes");

        var produced = Assert.Single(events.OfKind(EventKind.ArtifactProduced));
        Assert.Equal("notes/a.md", produced.ArtifactPath());
        Assert.Equal("FileSet", produced.ArtifactKindName());
    }

    [Fact]
    public void An_event_with_no_payload_names_nothing_rather_than_guessing()
    {
        var ev = Ev(EventKind.ArtifactProduced, "FileSet: doc.md");

        Assert.Null(ev.ArtifactPath());
        Assert.Null(ev.PlanSteps());
    }
}
