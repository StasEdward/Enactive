namespace Enactive.Engine.Tests;

using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Providers;
using Xunit;

/// <summary>
/// Reported with a log, 2026-09-07 19:36. A retry that could not be won.
///
/// <para>Attempt 1 of "Analyze codebase functionality" made nine calls — five listings and three
/// reads (README.md, MainViewModel.cs, StartupManager.cs). The reviewer rejected it for one thing:
/// <c>"it never actually read the ArcMapConditions.App.csproj file"</c>.</para>
///
/// <para>Attempt 2 did exactly that, read the csproj, and summarised what it had found across all
/// the files. The reviewer rejected it again, for the mirror image of the first reason:</para>
///
/// <para><c>"The agent claimed to have verified numerous details across multiple files (README.md,
/// MainViewModel.cs, SubscriptionManager.cs, NotificationService.cs, StartupManager.cs,
/// ConditionsParser.cs), but the tool execution evidence shows it only read one file:
/// ArcMapConditions.App.csproj."</c></para>
///
/// <para>Step rejected, second step skipped, run failed. Every word the agent said was true.</para>
///
/// <para><b>The transcript spanned both attempts and the evidence spanned only the last one.</b>
/// This is an EXECUTION review, where the transcript is deliberately kept — the model is asked to
/// fix a point, not to start again — so its answer legitimately draws on everything it has done.
/// The evidence, though, restarted at the retry's mark. Claim and evidence were being taken from
/// different windows, and no correct answer exists in that gap: doing only what the reviewer asked
/// leaves one call in the evidence and a paragraph of findings above it.</para>
///
/// <para>The rule now: <b>the evidence window is the transcript window.</b> A CONTENT review
/// discards the rejected draft from the transcript, so the evidence restarts with it — that is
/// still true and <c>ReviewerTests</c> pins the transcript half of it. An EXECUTION review keeps
/// the transcript, so the evidence keeps the calls.</para>
///
/// <para>Same failure as §8i and §9e in a new place: the record somebody is judged on was
/// shortened, and nothing said so.</para>
/// </summary>
public sealed class RetryEvidenceTests
{
    private const string QuickAction =
        """{"disposition":"quick_action","title":"analyze the codebase"}""";

    /// <summary>
    /// The EVIDENCE section of a review prompt, and nothing else.
    ///
    /// <para>Asserting on the whole prompt would be worthless here: the agent's own summary is in
    /// it too, and that summary names every file. Reading the claim and calling it the evidence is
    /// the mistake this defect is made of, and a test can make it just as easily.</para>
    /// </summary>
    private static string EvidenceIn(ChatRequest request)
    {
        var prompt = request.Messages.Last().Content ?? "";
        // The short step review: its calls, to the end of the prompt.
        const string calls = "TOOL CALLS (cite them by [n]):";
        if (prompt.IndexOf(calls, StringComparison.Ordinal) is var at and >= 0)
            return prompt[(at + calls.Length)..];
        const string opens = "the agent's own words above may be wrong or invented):";
        const string closes = "Files changed:";

        var from = prompt.IndexOf(opens, StringComparison.Ordinal);
        Assert.True(from >= 0, "not an execution review prompt:\n" + prompt);
        from += opens.Length;

        var to = prompt.IndexOf(closes, from, StringComparison.Ordinal);
        return to < 0 ? prompt[from..] : prompt[from..to];
    }

    /// <summary>
    /// The reported run, to scale: read two files, get rejected for not reading a third, read the
    /// third and report what was found in all three.
    /// </summary>
    private static FakeChatProvider TheAgent()
        => new(
            Turn.Says(QuickAction),
            // Attempt 1.
            Turn.Calls1("read_file", """{"path":"README.md"}"""),
            Turn.Calls1("read_file", """{"path":"MainViewModel.cs"}""", "c2"),
            Turn.Says("I read README.md and MainViewModel.cs."),
            // Attempt 2 — exactly what the reviewer asked for, and nothing else.
            Turn.Calls1("read_file", """{"path":"App.csproj"}""", "c3"),
            Turn.Says("I read README.md, MainViewModel.cs and App.csproj. Target is net10.0."));

    private static void WriteTheFiles(EngineFixture fx)
    {
        fx.Write("README.md", "# ARC Map Conditions\n");
        fx.Write("MainViewModel.cs", "// the view model\n");
        fx.Write("App.csproj", "<TargetFramework>net10.0-windows</TargetFramework>\n");
    }

    private static Task<List<WorkEvent>> RunAsync(
        EngineFixture fx, FakeChatProvider agent, IChatProvider reviewer)
        => fx.RunAsync(
            fx.Build(agent, EngineFixture.Role("reviewer"),
                     router: Routers.WithReviewer(), reviewProvider: reviewer),
            "analyze the codebase");

    // ── the defect ──────────────────────────────────────────────────────────

    /// <summary>
    /// The one that matters. What the first attempt read is still in the EVIDENCE the second
    /// attempt is judged on, because it is still in the answer the second attempt gives.
    /// </summary>
    [Fact]
    public async Task An_execution_retry_is_judged_on_everything_the_step_did()
    {
        using var fx = new EngineFixture();
        WriteTheFiles(fx);
        var reviewer = new FakeChatProvider(
            Verdicts.Fail("it never actually read the App.csproj file"), Verdicts.Pass());

        await RunAsync(fx, TheAgent(), reviewer);

        var evidence = EvidenceIn(reviewer.Requests[1]);

        Assert.Contains("README.md", evidence, StringComparison.Ordinal);
        Assert.Contains("MainViewModel.cs", evidence, StringComparison.Ordinal);
        Assert.Contains("App.csproj", evidence, StringComparison.Ordinal);
        Assert.Contains("3 tool call(s)", evidence, StringComparison.Ordinal);
    }

    /// <summary>
    /// And with a reviewer that applies the rule the real one applied — every file the answer
    /// claims must appear in the evidence — the reported run passes instead of being rejected
    /// twice. This is the test the log actually wrote; a scripted PASS proves only the wiring.
    /// </summary>
    [Fact]
    public async Task The_reported_run_now_survives_the_reviewer_that_rejected_it()
    {
        using var fx = new EngineFixture();
        WriteTheFiles(fx);
        // Both of the real reviewer's rules: the csproj had to be read at all (its first
        // rejection), and nothing may be claimed that the evidence does not show (its second).
        var reviewer = new EvidenceChecker(
            required: "App.csproj",
            claimable: new[] { "README.md", "MainViewModel.cs", "App.csproj" });

        var events = await RunAsync(fx, TheAgent(), reviewer);

        Assert.True(events.Has(EventKind.ReviewPassed), events.Text());
        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
    }

    // ── what must not change ────────────────────────────────────────────────

    /// <summary>
    /// The FIRST review still sees the first attempt and only that. Stated because the fix moves
    /// where a mark is taken, and a mark taken too early would show a review the calls of an
    /// earlier step.
    /// </summary>
    [Fact]
    public async Task The_first_review_sees_the_first_attempt_and_nothing_else()
    {
        using var fx = new EngineFixture();
        WriteTheFiles(fx);
        var reviewer = new FakeChatProvider(Verdicts.Fail("read the csproj"), Verdicts.Pass());

        await RunAsync(fx, TheAgent(), reviewer);

        var evidence = EvidenceIn(reviewer.Requests[0]);

        Assert.Contains("2 tool call(s)", evidence, StringComparison.Ordinal);
        Assert.DoesNotContain("App.csproj", evidence, StringComparison.Ordinal);
    }

    /// <summary>
    /// A reviewer that applies both of the rules the one in the log applied, reading the prompt
    /// rather than following a script — so it cannot be satisfied by the fix merely having run.
    ///
    /// <para><paramref name="required"/> must appear in the evidence at all; that is the first
    /// rejection ("it never actually read the csproj"). And nothing the agent's summary names may
    /// be absent from the evidence; that is the second one.</para>
    /// </summary>
    private sealed class EvidenceChecker : IChatProvider
    {
        private readonly string _required;
        private readonly string[] _claimable;

        public EvidenceChecker(string required, string[] claimable)
        {
            _required = required;
            _claimable = claimable;
        }

        public List<ChatRequest> Requests { get; } = new();

        public int? ContextWindow(ChatRequest request) => null;

        public Task<ChatCompletion> CompleteAsync(ChatRequest request, CancellationToken ct)
        {
            Requests.Add(request);

            var prompt = request.Messages.Last().Content ?? "";
            var evidence = EvidenceIn(request);

            // Only what the agent actually claimed THIS time is held against it.
            var missing = _claimable
                .Where(f => prompt.Contains(f, StringComparison.Ordinal))
                .Where(f => !evidence.Contains(f, StringComparison.Ordinal))
                .ToArray();

            var fail = !evidence.Contains(_required, StringComparison.Ordinal)
                ? $"it never actually read {_required}"
                : missing.Length == 0 ? null : $"claimed but not shown in the evidence: {string.Join(", ", missing)}";
            var verdict = Verdicts.IsStepVerdict(request)
                ? fail is null ? Verdicts.ShortPass(request, "the evidence is there") : Verdicts.ShortVerdict("fail", fail, [], [])
                : fail is null ? """{"verdict":"pass","notes":"the evidence is there"}""" : $$"""{"verdict":"fail","notes":"{{fail}}"}""";

            return Task.FromResult(new ChatCompletion(
                new ChatMessage(ChatRole.Assistant, verdict, null), "stop", 100, 20, null));
        }

        public async IAsyncEnumerable<ChatStreamEvent> StreamChatAsync(
            ChatRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var completion = await CompleteAsync(request, ct);
            yield return new TextDelta(completion.Message.Content ?? "");
            yield return new FinishDelta("stop");
        }
    }
}
