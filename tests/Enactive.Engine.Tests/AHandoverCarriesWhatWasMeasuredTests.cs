namespace Enactive.Engine.Tests;

using System.Diagnostics;
using Enactive.Core.Chat;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// A handover is asked as the same request the step sends, and it carries what the engine measured
/// beside what the model remembers.
///
/// <para><b>Measured 2026-09-24 21:47, run bc3200.</b> A step checking that tests catch a breakage
/// made MonitorClient.cs accept HTTP 404, saw a test fail, and was handed over at that moment. Its
/// note said the file was "in its correct, unbroken state" and nothing was left to do; the next
/// conversation found the failing test and changed the TEST to expect 404 to succeed. And the note
/// cost 46,176 prompt tokens with none of them cached, 29 seconds, because the handover request was
/// sent without the tools the step's requests carry.</para>
/// </summary>
public sealed class AHandoverCarriesWhatWasMeasuredTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"check the tests catch it"}""";

    private const string Production = "public static class Prod\n{\n    public static int Port => 65535;\n}\n";

    private const string Note = "Note: the check is done and Prod.cs is back as it was. Nothing is left to do.";

    private static void Git(string root, params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
    }

    private static void Repository(EngineFixture fx, string file, string content)
    {
        Git(fx.Root, "init", "-q");
        Git(fx.Root, "config", "user.email", "t@example.com");
        Git(fx.Root, "config", "user.name", "t");
        Git(fx.Root, "config", "core.autocrlf", "false");
        fx.Write(file, content);
        Git(fx.Root, "add", "-A");
        Git(fx.Root, "commit", "-q", "-m", "start");
    }

    /// <summary>The run, in miniature: a breakage made, a test run, a handover - and a note that says all is well.</summary>
    private static FakeChatProvider Breakage(string command = "echo 1 test failed")
        => new(
            Turn.Says(QuickAction),
            Turn.Calls1("edit_file", """{"path":"Prod.cs","old_string":"65535","new_string":"65534"}""", "e1").Reporting(prompt: 3_000),
            Turn.Calls1("run_command", $$"""{"command":"{{command}}"}""", "c1").Reporting(prompt: 8_000),
            Turn.Says(Note),
            Turn.Calls1("edit_file", """{"path":"Prod.cs","old_string":"65534","new_string":"65535"}""", "e2"),
            Turn.Says("Put it back."))
        { Window = 10_000, HandoverAt = 75 };

    private static string Resumed(FakeChatProvider provider)
        => provider.Requests.SelectMany(r => r.Messages)
                   .Select(m => m.Content ?? "")
                   .Last(c => c.Contains("started again from your own notes", StringComparison.Ordinal));

    // ── the same request ─────────────────────────────────────────────────────

    [Fact]
    public async Task The_handover_is_asked_as_the_request_the_step_sends()
    {
        using var fx = new EngineFixture();
        fx.Write("Prod.cs", Production);
        var provider = Breakage();

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "check the tests catch it");

        var asked = provider.Requests.FindIndex(r => r.Messages[^1].Content?.Contains(
            "this conversation is being started over", StringComparison.Ordinal) == true);
        Assert.True(asked > 0);
        var handover = provider.Requests[asked];
        var step = provider.Requests[asked - 1];

        // The tools are what a chat template writes first; without them the prompt differs from its
        // first tokens and nothing of it is served from cache.
        Assert.Equal(step.Tools!.Select(t => t.Name), handover.Tools!.Select(t => t.Name));
        Assert.Equal(step.Temperature, handover.Temperature);
        Assert.Equal(step.NumCtx, handover.NumCtx);
        Assert.Equal(step.Think, handover.Think);

        // And the conversation is the step's, from its system prompt, with one question after it.
        Assert.Equal(ChatRole.System, handover.Messages[0].Role);
        Assert.Equal(ChatRole.User, handover.Messages[^1].Role);
    }

    // ── what was measured ────────────────────────────────────────────────────

    [Fact]
    public async Task A_breakage_left_in_is_in_the_note_whatever_the_note_says()
    {
        using var fx = new EngineFixture();
        Repository(fx, "Prod.cs", Production);
        var provider = Breakage();

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "check the tests catch it");

        var resumed = Resumed(provider);
        Assert.Contains(Note, resumed, StringComparison.Ordinal);                         // the model's account
        Assert.Contains("MEASURED BY THE ENGINE", resumed, StringComparison.Ordinal);     // and, beside it, the fact
        Assert.Contains("Prod.cs (modified)", resumed, StringComparison.Ordinal);
        Assert.Contains("+    public static int Port => 65534;", resumed, StringComparison.Ordinal);
        Assert.Contains("still changed until you put it back", resumed, StringComparison.Ordinal);
        Assert.Contains("The last command you ran: echo 1 test failed", resumed, StringComparison.Ordinal);

        Assert.Equal(Production, fx.Read("Prod.cs"));
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    /// <summary>Outside git there is no diff to show, and the file is still named.</summary>
    [Fact]
    public async Task Outside_git_the_changed_file_is_still_named()
    {
        using var fx = new EngineFixture();
        fx.Write("Prod.cs", Production);
        var provider = Breakage();

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "check the tests catch it");

        Assert.Contains("Prod.cs (modified)", Resumed(provider), StringComparison.Ordinal);
    }

    /// <summary>Orchestrator.MaxHandoverDiffChars - a diff too long for the note is cut, and says so.</summary>
    [Fact]
    public async Task A_huge_diff_in_a_handover_is_cut_and_says_so()
    {
        using var fx = new EngineFixture();
        var big = string.Join("\n", Enumerable.Range(1, 300).Select(i => $"line {i} of a long file that is about to change"));
        Repository(fx, "Prod.cs", Production + big + "\n");

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", System.Text.Json.JsonSerializer.Serialize(new
            {
                path = "Prod.cs",
                content = Production + big.Replace("about to change", "now changed") + "\n",
                allow_shrink = true   // most lines change; that is the point here, and 9di asks for it said
            }), "w1").Reporting(prompt: 80_000),
            Turn.Says(Note),
            Turn.Says("Done."))
        // A window the carried note FITS in. At 10,000 the note and its 3,000-character diff did not,
        // the step stopped with "the context window is full", and the resumed message was never
        // sent - which this test did not notice while it read the engine's live list rather than
        // the requests as they went out (Docs/PROVIDERS_AGENTS_TOOLS_TESTS_REVIEW_2026-09-24.md #2).
        { Window = 100_000, HandoverAt = 75 };

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "check the tests catch it");

        var resumed = Resumed(provider);
        Assert.Contains("Prod.cs (modified)", resumed, StringComparison.Ordinal);
        Assert.Contains("diff cut here", resumed, StringComparison.Ordinal);
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    /// <summary>Orchestrator.HandoverOutputTailChars - a long output is carried by its END, where results are.</summary>
    [Fact]
    public async Task A_long_command_output_is_carried_by_its_end()
    {
        using var fx = new EngineFixture();
        fx.Write("Prod.cs", Production);
        var provider = Breakage("for /L %i in (1,1,120) do @echo output line %i");
        // Exercise tail preservation, not the exact prompt-size boundary. Leave room for the
        // handover request itself while still triggering it after the measured 8000-token turn.
        provider.Window = 12_000;
        provider.HandoverAt = 65;

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "check the tests catch it");
        Assert.True(provider.Requests.SelectMany(r => r.Messages).Any(
            m => m.Content?.Contains("started again from your own notes", StringComparison.Ordinal) == true), events.Text());

        var resumed = Resumed(provider);
        Assert.Contains("output line 120", resumed, StringComparison.Ordinal);
        Assert.DoesNotContain("output line 2\n", resumed, StringComparison.Ordinal);
    }
}
