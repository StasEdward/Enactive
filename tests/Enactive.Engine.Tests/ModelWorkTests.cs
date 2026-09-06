namespace Enactive.Engine.Tests;

using Enactive.Core.Events;
using Enactive.Core.Providers;
using Enactive.Core.Permissions;
using Enactive.Core.Workers;
using Xunit;

/// <summary>
/// Attributing tokens to the provider that produced them.
///
/// A run's totals said how much work there was and nothing about WHO did it, which is the only
/// question the light/heavy routing exists to answer: a plan that sends trivial steps to the local
/// model is worth nothing if every step still ends up at the expensive one, and there was no way to
/// see that short of reading the log.
///
/// The engine records the provider ID and stops there — it has no idea of a provider's address, so
/// local-versus-cloud is the app's decision, made from the settings.
/// </summary>
public sealed class ModelWorkTests
{
    private const string Light = "ollama-local";
    private const string Heavy = "anthropic";

    private static Worker WorkerOn(string providerId)
        => new("developer", "Developer", "You are a developer.",
               new[] { "write_file", "read_file", "list_dir", "run_command" },
               PermissionLevel.Execute,
               new ModelPolicy(new ModelRef(providerId, "a-model")));

    /// <summary>A provider that reports token usage, so the split has something to divide.</summary>
    private static FakeChatProvider Reporting(params Turn[] script)
        => new(script.Select(t => t.Reporting()).ToArray())
        {
            WhenExhausted = Turn.Says("done").Reporting()
        };

    // ── the payload carries who did the work ──────────────────────────────────────────

    [Fact]
    public void The_usage_payload_names_the_provider_and_model()
    {
        var payload = WorkEventPayload.UsagePayload(120, 45, stepNo: 2, "ollama-local", "qwen3:14b");

        var ev = new WorkEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
                               EventKind.UsageReported, "tokens", payload);

        Assert.Equal((120, 45), ev.Usage());
        Assert.Equal(2, ev.StepNo());
        Assert.Equal("ollama-local", ev.ProviderId());
        Assert.Equal("qwen3:14b", ev.ModelName());
    }

    // A run recorded before this existed has no provider in its payload. Unknown is a different fact
    // from "local" or "cloud" and must not be quietly turned into either.
    [Fact]
    public void An_older_payload_still_reads_its_tokens_and_reports_no_provider()
    {
        var ev = new WorkEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
                               EventKind.UsageReported, "tokens", """{"step":1,"in":10,"out":20}""");

        Assert.Equal((10, 20), ev.Usage());
        Assert.Null(ev.ProviderId());
        Assert.Null(ev.ModelName());
    }

    // Model names carry colons and slashes; an id could carry a quote. The payload has to survive it.
    [Fact]
    public void Awkward_names_survive_the_round_trip()
    {
        var payload = WorkEventPayload.UsagePayload(1, 1, null, "weird\"id\\", "vendor/model:8b-q4_K_M");

        var ev = new WorkEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
                               EventKind.UsageReported, "tokens", payload);

        Assert.Equal("weird\"id\\", ev.ProviderId());
        Assert.Equal("vendor/model:8b-q4_K_M", ev.ModelName());
    }

    // ── the engine actually stamps it ─────────────────────────────────────────────────

    [Fact]
    public async Task A_quick_action_attributes_its_tokens_to_the_provider_that_ran_it()
    {
        using var fx = new EngineFixture();

        var provider = Reporting(
            Turn.Says("""{"disposition":"quick_action","title":"say something"}"""),
            Turn.Says("Said it."));

        var events = await fx.RunAsync(
            fx.Build(provider, worker: WorkerOn(Light)), "say something");

        var usage = events.OfKind(EventKind.UsageReported).ToArray();
        Assert.All(usage, e => Assert.Equal(Light, e.ProviderId()));
        Assert.All(usage, e => Assert.Equal("a-model", e.ModelName()));
    }

    // The whole point: when a plan routes steps to different providers, the tokens have to land
    // under the provider that actually served each one.
    [Fact]
    public async Task Steps_routed_to_different_providers_are_attributed_separately()
    {
        using var fx = new EngineFixture();

        const string plan = """
            {"disposition":"task","title":"two steps",
             "steps":[{"title":"heavy work","dependsOn":[],"complexity":"complex"},
                      {"title":"light work","dependsOn":[],"complexity":"trivial"}]}
            """;

        var planner = new FakeChatProvider(Turn.Says(plan));
        var light = Reporting();
        var heavy = Reporting();

        var providers = new MapProviderFactory(
            planner,
            (Routers.LightProviderId, light),
            (Routers.HeavyProviderId, heavy));

        var events = await fx.RunAsync(
            fx.Build(providers, router: Routers.WithComplexityRouting()), "do two things");

        var byProvider = events.OfKind(EventKind.UsageReported)
            .Select(e => e.ProviderId())
            .Where(p => p is not null)
            .Distinct()
            .ToArray();

        Assert.Contains(Routers.LightProviderId, byProvider);
        Assert.Contains(Routers.HeavyProviderId, byProvider);
    }

    // A run that switched to the fallback must say so in the attribution too, or the numbers claim
    // work for a provider that was never reached.
    [Fact]
    public async Task Tokens_after_a_fallback_belong_to_the_fallback_provider()
    {
        using var fx = new EngineFixture();

        var worker = new Worker("developer", "Developer", "You are a developer.",
            new[] { "write_file", "read_file" }, PermissionLevel.Execute,
            new ModelPolicy(new ModelRef("primary", "m1"), Fallback: new ModelRef(Heavy, "m2")));

        var providers = new MapProviderFactory(
            new FakeChatProvider(Turn.Says("""{"disposition":"quick_action","title":"x"}""")),
            (Routers.PlannerProviderId,
             new FakeChatProvider(Turn.Says("""{"disposition":"quick_action","title":"x"}"""))),
            ("primary", new ThrowingChatProvider("down")),
            (Heavy, Reporting()));

        var events = await fx.RunAsync(
            fx.Build(providers, worker: worker, router: Routers.WithPlannerOn()), "do it");

        var usage = events.OfKind(EventKind.UsageReported).Select(e => e.ProviderId()).ToArray();
        Assert.DoesNotContain("primary", usage);
    }

    // ── a stored run reads the same way a live one does ───────────────────────────────

    // The point of keeping the payload with the record: reading a past run must not mean parsing the
    // English summary, which is the habit the typed payloads were introduced to end.
    [Fact]
    public async Task A_recorded_run_keeps_the_payload_so_it_can_be_read_back()
    {
        using var fx = new EngineFixture();

        var provider = Reporting(
            Turn.Says("""{"disposition":"quick_action","title":"say something"}"""),
            Turn.Says("Said it."));

        // Through the real recorder, not a shortcut: what matters is what actually reaches the store.
        var store = new CapturingRunStore();
        var recorder = new Enactive.Agents.RunRecorder(store, workspaceId: fx.Workspace.Id);

        await foreach (var _ in recorder.RecordAsync(Stream(fx, provider), CancellationToken.None)) { }

        var record = Assert.Single(store.Saved);
        var stored = record.Events.Where(e => e.Kind == nameof(EventKind.UsageReported)).ToArray();
        Assert.NotEmpty(stored);
        Assert.All(stored, e => Assert.False(string.IsNullOrEmpty(e.Payload)));

        // Read back through exactly the same accessor a live event uses.
        foreach (var e in stored)
        {
            var replayed = new WorkEvent(Guid.Empty, record.TaskId, record.RunId, e.At,
                                         EventKind.UsageReported, e.Summary, e.Payload);
            Assert.Equal(Light, replayed.ProviderId());
        }
    }

    private static IAsyncEnumerable<WorkEvent> Stream(EngineFixture fx, FakeChatProvider provider)
    {
        var orchestrator = fx.Build(provider, worker: WorkerOn(Light));
        var context = new Enactive.Core.Context.WorkContext(
            fx.Workspace.Id, fx.Workspace.Name, null, null, null,
            Array.Empty<string>(), Array.Empty<string>());

        return orchestrator.SubmitIntentAsync(
            new Enactive.Core.Intents.Intent(
                Guid.NewGuid(), "say something", Enactive.Core.Intents.IntentSource.CommandBar,
                context, DateTimeOffset.UtcNow),
            CancellationToken.None);
    }

    /// <summary>Keeps what the recorder saved, which is the only thing a past run can be built from.</summary>
    private sealed class CapturingRunStore : Enactive.Core.History.IRunStore
    {
        public List<Enactive.Core.History.RunRecord> Saved { get; } = new();

        public Task SaveAsync(Enactive.Core.History.RunRecord record, CancellationToken ct)
        {
            Saved.Add(record);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<Enactive.Core.History.RunRecord>> LoadAllAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyList<Enactive.Core.History.RunRecord>>(Saved);
    }
}
