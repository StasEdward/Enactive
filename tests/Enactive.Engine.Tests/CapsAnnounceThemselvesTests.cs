namespace Enactive.Engine.Tests;

using System.Reflection;
using Enactive.Agents;
using Enactive.Core.Artifacts;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// Docs/FIX_PLAN.md §9b, the second prevention item: <b>every cap announces itself</b>.
///
/// <para>Measured on 2026-09-07, that was true of every live limit in the codebase — and true by
/// accident. Nothing kept it true. The next cap somebody adds is silent by default, exactly as
/// <c>MaxReviewFileChars</c> was: it cut a 414-line page to its first 161 lines, said nothing, and
/// the reviewer failed the run over a truncated line that was truncated by us.</para>
///
/// <para>So two things live here. A <b>census</b> that fails when a new size limit appears without a
/// test naming it — the guard — and one <b>behavioural test per limit</b>, each driving the real
/// code past the real cap and asserting the output says so.</para>
///
/// <para>What the census cannot see, and why it is not the whole story: it walks NAMED constants.
/// A limit written as a literal in an expression, or as a default parameter value, is invisible to
/// it. Those are listed by hand in <see cref="LimitsWithNoConstant"/> with the test that covers
/// each. A hand-kept list rots; it is still better than the silence it replaces, and the census
/// covers the shape a new cap is overwhelmingly likely to take.</para>
/// </summary>
public sealed class CapsAnnounceThemselvesTests
{
    private static ToolContext Context(EngineFixture fx, IArtifactStore? store = null)
        => new(TaskId: Guid.NewGuid(), RunId: Guid.NewGuid(), WorkspaceId: fx.Workspace.Id,
               Context: null!, PermissionPolicy: PermissionPolicy.PermissiveDefault,
               WorkspaceRoot: fx.Root, Artifacts: store ?? fx.Artifacts, Services: null!);

    // ── the census ──────────────────────────────────────────────────────────

    /// <summary>
    /// Every size limit in the engine, and the test that proves it speaks up.
    ///
    /// <para>Keyed by "Type.Field". A limit with no entry here fails the census; an entry naming a
    /// test that does not exist fails too, so the mapping cannot rot into a list of good intentions.</para>
    /// </summary>
    private static readonly Dictionary<string, string> Covered = new(StringComparer.Ordinal)
    {
        ["Orchestrator.MaxReviewFileChars"] = nameof(A_files_real_size_reaches_the_reviewer_from_a_real_run),
        ["Reviewer.MaxContentCharsPerFile"] = "An_excerpt_says_so_even_when_the_caller_did_the_cutting",
        ["Reviewer.MaxContentCharsTotal"] = nameof(Files_dropped_for_the_review_budget_are_announced),
        ["SuccessEvaluator.MaxDetailChars"] = nameof(A_criterions_output_says_how_much_of_it_is_shown),
        ["ProcessExec.MaxOutputChars"] = nameof(Command_output_past_the_cap_says_it_was_truncated),
        ["RunPowerShellTool.MaxEncodedChars"] = nameof(A_script_too_long_for_a_command_line_is_refused),
        ["ProcessExec.MaxCapturedChars"] = "Captured_output_stops_at_the_ceiling_however_much_is_produced",
        ["ReadFileTool.MaxChars"] = nameof(A_line_too_long_for_the_window_says_where_it_was_cut),
        ["ReadFileTool.DefaultLines"] = nameof(A_window_says_which_lines_it_is_and_how_to_get_the_rest),
        ["SearchFilesTool.MaxMatches"] = nameof(A_search_that_stops_early_says_it_stopped),
        ["SearchFilesTool.MaxOutputChars"] = nameof(A_search_that_stops_early_says_it_stopped),
        ["SearchFilesTool.MaxLineChars"] = nameof(A_very_long_matching_line_is_shown_cut),
        // Moved out of SearchFilesTool when count_matches and file_stats had to walk the workspace
        // the same way. One skip list and one size ceiling for every tool that scans, so a count and
        // a search can never disagree about which files exist.
        ["WorkspaceScan.MaxFileBytes"] = nameof(A_file_too_large_to_search_is_reported_not_skipped_in_silence),
        ["CompareFilesTool.MaxShownChars"] = "A_very_long_differing_line_is_shown_cut",
        // Not a cap on an ANSWER but on what the transcript remembers of a call already made. It
        // announces itself the same way - the head, then the exact length - because a remembered
        // argument that looked whole would have the model believe it wrote 200 characters.
        ["Transcript.MaxRememberedValueChars"] = "A_file_sized_argument_is_remembered_by_its_head_and_its_length",
        // The census the planner is sized against. The scan ceiling says when it stopped; the floor
        // below it decides which folders are worth a line, and a folder left out is not a truncated
        // answer - it is one loose file nobody plans against.
        ["WorkspaceCensus.MaxFilesScanned"] = "A_census_that_stopped_counting_says_so",
        ["WorkspaceCensus.MinFiles"] = "A_folder_with_almost_nothing_in_it_gets_no_line",
        ["Transcript.RememberedHeadChars"] = "A_file_sized_argument_is_remembered_by_its_head_and_its_length",
        // A cap on how many FILES are listed, not how many characters. The totals above the list
        // stay complete - which is the whole difference between this and a search that stops early,
        // and the reason the notice has to say so rather than just trailing off.
        ["CountMatchesTool.MaxFilesListed"] = "A_count_over_many_files_lists_some_and_says_the_totals_still_hold",
        ["FileStatsTool.MaxFilesListed"] = "Stats_over_many_files_list_the_largest_and_say_the_totals_still_hold",
        ["WriteFileTool.ShrinkGuardFloorBytes"] = "The_rule_itself",
        // Caught by this census the day it was written, which is what the census is for: a new
        // constant with "Chars" in its name and nothing driving it past its limit.
        ["LogAnalyst.CharsPerToken"] = "The_missing_middle_announces_itself_and_says_how_much",
        ["RunTitle.MaxChars"] = "A_long_title_is_cut_and_says_it_was",
        // The floor under a shared budget: with enough calls each gets very little, and what must
        // never be lost is the LIST of them.
        ["ExecutionJournal.MinOutputChars"] = "A_list_too_long_to_show_says_how_much_is_missing",
        ["ExecutionJournal.ShortenedNoticeChars"] = "A_shortened_result_says_so_and_says_the_call_happened",
        // The digest that replaced the excerpt for logs too large to hold. Its own caps: how much
        // of one record's message survives, and how many timeline entries it will list.
        ["LogDigest.MessageChars"] = "A_message_too_long_for_the_digest_is_cut_and_says_so"
    };

    /// <summary>
    /// The limits written as literals rather than named constants, which the census cannot find.
    /// Listed so they are at least accounted for, with the test that covers each.
    /// </summary>
    private static readonly Dictionary<string, string> LimitsWithNoConstant = new(StringComparer.Ordinal)
    {
        // Not a whole-text cut any more: the calls are always listed and only the outputs share a
        // budget, because cutting the tail lost the LAST calls and the reviewer failed work it
        // could not see.
        ["ExecutionJournal.Describe(maxChars: 6000, shared between outputs)"]
            = "A_shortened_result_says_so_and_says_the_call_happened",
        ["McpConnection content (32000)"] = "(none — a remote server's own output, cut with '… (truncated)')",
        ["Transcript.Elide"] = "(EventKind.ContextTrimmed — ContextWindowTests)"
    };

    /// <summary>
    /// The guard itself. A new <c>private const int MaxSomethingChars</c> lands in the engine and
    /// this test fails until somebody either writes the test that proves it announces itself, or
    /// says in the table above which existing test does.
    /// </summary>
    [Fact]
    public void Every_size_limit_in_the_engine_is_covered_by_a_test_that_drives_it()
    {
        var found = SizeLimits().Select(f => $"{f.DeclaringType!.Name}.{f.Name}").OrderBy(n => n).ToArray();

        var uncovered = found.Where(n => !Covered.ContainsKey(n)).ToArray();
        Assert.True(uncovered.Length == 0,
            "A size limit with no test driving it past the cap: " + string.Join(", ", uncovered)
            + ". Add a test asserting the output SAYS it was cut, and name it in Covered. "
            + "A cap nobody is told about produces a truncated answer that reads as a complete one, "
            + "which is worse than either the whole thing or a refusal.");

        var stale = Covered.Keys.Where(n => !found.Contains(n)).ToArray();
        Assert.True(stale.Length == 0,
            "Covered names a limit that no longer exists: " + string.Join(", ", stale));
    }

    /// <summary>
    /// Every test the table names has to be a real test. Otherwise the census degrades into a list
    /// of names — which is precisely the failure mode of the deny list that denied nothing.
    /// </summary>
    [Fact]
    public void Every_test_the_table_names_exists()
    {
        var tests = typeof(CapsAnnounceThemselvesTests).Assembly
            .GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Select(m => m.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (limit, test) in Covered)
            Assert.True(tests.Contains(test), $"'{limit}' names the test '{test}', which does not exist.");
    }

    /// <summary>
    /// Every field in the shipping assemblies that looks like a size limit: an integer constant
    /// counted in characters, bytes, lines or matches. The vocabulary is deliberately broad — a name
    /// that slips through it is a name nobody would read as a limit either.
    /// </summary>
    private static IEnumerable<FieldInfo> SizeLimits()
    {
        var assemblies = new[]
        {
            typeof(WriteFileTool).Assembly,          // Enactive.Tools
            typeof(Orchestrator).Assembly,           // Enactive.Agents
            typeof(Core.Events.EventKind).Assembly,  // Enactive.Core
            typeof(Workspace.AtomicWrite).Assembly   // Enactive.Workspace
        };

        foreach (var assembly in assemblies)
        foreach (var type in assembly.GetTypes())
        foreach (var field in type.GetFields(
                     BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly))
        {
            if (field.FieldType != typeof(int) || !(field.IsLiteral || field.IsInitOnly))
                continue;
            if (!Vocabulary.Any(w => field.Name.Contains(w, StringComparison.Ordinal)))
                continue;

            yield return field;
        }
    }

    // "Files" joined the vocabulary with the counting tools, which cap how many FILES they list
    // rather than how many characters they print - the same class of limit (how much of the answer
    // is shown) in a unit the census could not see. Widening it was measured rather than assumed:
    // it surfaced exactly the two new constants, so it costs nothing and closes the hole a
    // count-shaped cap would otherwise slip through.
    private static readonly string[] Vocabulary = { "Chars", "Bytes", "Lines", "Matches", "Files" };

    // ── one per limit: the code, past the cap, saying so ────────────────────

    /// <summary>
    /// RunPowerShellTool.MaxEncodedChars — a script that cannot fit on a Windows command line.
    ///
    /// <para>The cap is Windows', not ours: run_powershell sends its script as -EncodedCommand,
    /// base64 of UTF-16 is about 2.7x the characters, and the line takes roughly 32,000. Crossing
    /// it got "The filename or extension is too long" — a message about a filename, for a script
    /// that has none — and the process never started at all (2026-09-24 01:18).</para>
    /// </summary>
    [Fact]
    public async Task A_script_too_long_for_a_command_line_is_refused()
    {
        using var fx = new EngineFixture();

        var script = "$c = '" + new string('x', RunPowerShellTool.MaxEncodedChars) + "'";

        var result = await fx.Invoke(new RunPowerShellTool(),
                                     System.Text.Json.JsonSerializer.Serialize(new { script }));

        Assert.False(result.Success);
        Assert.True(result.DidNotRun, result.Error);
        Assert.Contains($"{RunPowerShellTool.MaxEncodedChars:N0}", result.Error, StringComparison.Ordinal);
    }

    /// <summary>
    /// ProcessExec.MaxOutputChars — a build log longer than the model is shown, and the part it
    /// is shown.
    ///
    /// <para>This used to assert only that the word "truncated" appeared, which the head-only cut
    /// satisfied while throwing away the answer. A program reports its outcome LAST: on 2026-09-20
    /// a step wrote its tests, ran them, and could not tell whether they had passed, because the
    /// summary was past the cut. So the assertion is now on what survives.</para>
    /// </summary>
    [Fact]
    public void Command_output_past_the_cap_says_it_was_truncated()
    {
        var log = string.Join('\n', Enumerable.Range(0, 2_000).Select(i => $"line {i} of a long build"))
                + "\nPassed!  - Failed: 0, Passed: 22, Skipped: 0";

        var result = ProcessExec.BuildResult("Command", 0, log, "");

        Assert.True(result.Success);
        Assert.Contains("not shown here", result.Output, StringComparison.Ordinal);

        // The verdict is the whole reason a command was run, and it is the last thing printed.
        Assert.Contains("Passed!  - Failed: 0, Passed: 22", result.Output, StringComparison.Ordinal);

        // And the start, so the model can still tell what it is looking at.
        Assert.Contains("line 0 of a long build", result.Output, StringComparison.Ordinal);
    }

    /// <summary>And not when it fits — a notice on ordinary output would teach the model to distrust all of it.</summary>
    [Fact]
    public void Ordinary_command_output_carries_no_notice()
        => Assert.DoesNotContain("truncated",
            ProcessExec.BuildResult("Command", 0, "Build succeeded.\n0 Error(s)", "").Output,
            StringComparison.Ordinal);

    /// <summary>ReadFileTool.DefaultLines — the window, and how to ask for the rest of the file.</summary>
    [Fact]
    public async Task A_window_says_which_lines_it_is_and_how_to_get_the_rest()
    {
        using var fx = new EngineFixture();
        fx.Write("long.txt", string.Join('\n', Enumerable.Range(1, 900).Select(i => $"line {i}")));

        var result = await new ReadFileTool().InvokeAsync(
            """{"path":"long.txt"}""", Context(fx), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("of 900", result.Output);
        Assert.Contains("Read on with offset 401", result.Output);
        Assert.True((bool)result.Metadata!["truncated"]!);
    }

    /// <summary>ReadFileTool.MaxChars — a window of few lines can still be too big to send.</summary>
    [Fact]
    public async Task A_line_too_long_for_the_window_says_where_it_was_cut()
    {
        using var fx = new EngineFixture();
        fx.Write("minified.js", new string('x', 20_000));

        var result = await new ReadFileTool().InvokeAsync(
            """{"path":"minified.js"}""", Context(fx), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("truncated at 8000 characters", result.Output);
        Assert.True((bool)result.Metadata!["truncated"]!);

        // And the real size is still reported, so "8000 characters" is never mistaken for the file.
        Assert.Equal(20_000, Convert.ToInt32(result.Metadata!["bytes"]));
    }

    /// <summary>SearchFilesTool.MaxMatches / MaxOutputChars — a search that gave up before the end.</summary>
    [Fact]
    public async Task A_search_that_stops_early_says_it_stopped()
    {
        using var fx = new EngineFixture();
        for (var file = 0; file < 5; file++)
            fx.Write($"f{file}.txt", string.Join('\n', Enumerable.Range(0, 60).Select(i => $"needle {i}")));

        var result = await new SearchFilesTool().InvokeAsync(
            """{"pattern":"needle"}""", Context(fx), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("stopped at", result.Output);
        Assert.True((bool)result.Metadata!["truncated"]!);
    }

    /// <summary>SearchFilesTool.MaxLineChars — a matching line longer than the result should carry.</summary>
    [Fact]
    public async Task A_very_long_matching_line_is_shown_cut()
    {
        using var fx = new EngineFixture();
        fx.Write("bundle.js", "needle" + new string('y', 5_000));

        var result = await new SearchFilesTool().InvokeAsync(
            """{"pattern":"needle"}""", Context(fx), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("…", result.Output, StringComparison.Ordinal);
        Assert.True(result.Output!.Length < 1_000, $"the whole line came back: {result.Output.Length} characters");
    }

    /// <summary>
    /// SearchFilesTool.MaxFileBytes — the one the audit got wrong.
    ///
    /// <para>§9b's own table listed this as announced "by a capped flag in the output AND in
    /// metadata". It was not: the size ceiling and the binary check were a bare <c>continue</c>, and
    /// <c>capped</c> is set only by the match and output limits. A search that stepped over the one
    /// file holding the answer reported "No matches for /X/ in 40 file(s)" — a statement about forty
    /// files, presented as a fact about the workspace, and a generated 3 MB file is exactly the kind
    /// that holds the string somebody is looking for.</para>
    /// </summary>
    [Fact]
    public async Task A_file_too_large_to_search_is_reported_not_skipped_in_silence()
    {
        using var fx = new EngineFixture();

        // Just over the 2 MB ceiling, and it contains the pattern.
        fx.Write("generated.sql", "needle\n" + new string('z', 2 * 1024 * 1024));
        fx.Write("small.txt", "nothing here\n");

        var result = await new SearchFilesTool().InvokeAsync(
            """{"pattern":"needle"}""", Context(fx), CancellationToken.None);

        Assert.True(result.Success, result.Error);
        Assert.Contains("not searched", result.Output);
        Assert.Contains("larger than 2 MB", result.Output);
        Assert.Equal(1, Convert.ToInt32(result.Metadata!["skippedTooLarge"]));

        // The wording that made it dangerous: it read as a fact about the workspace.
        Assert.Contains("No matches", result.Output);
    }

    /// <summary>A search that read everything says nothing about skipping — the notice has to mean something.</summary>
    [Fact]
    public async Task A_search_that_read_everything_says_nothing_about_skipping()
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "needle\n");

        var result = await new SearchFilesTool().InvokeAsync(
            """{"pattern":"needle"}""", Context(fx), CancellationToken.None);

        Assert.DoesNotContain("not searched", result.Output);
        Assert.Equal(0, Convert.ToInt32(result.Metadata!["skippedTooLarge"]));
    }

    /// <summary>SuccessEvaluator.MaxDetailChars — a failing build's log inside a run report.</summary>
    [Fact]
    public void A_criterions_output_says_how_much_of_it_is_shown()
    {
        var detail = SuccessEvaluator.Trim(new string('e', 5_000));

        Assert.Contains("showing the first 1200 of 5000 characters", detail);
        Assert.DoesNotContain("showing the first", SuccessEvaluator.Trim("error CS1002: ; expected"));
    }

    /// <summary>Reviewer.MaxContentCharsTotal — the files that did not fit in the prompt at all.</summary>
    [Fact]
    public void Files_dropped_for_the_review_budget_are_announced()
    {
        var files = Enumerable.Range(0, 6)
            .Select(i => new WrittenFile($"file{i}.md", new string('m', 7_000)))
            .ToArray();

        var prompt = Reviewer.BuildContentUserPrompt("Write six documents", "done", files);

        Assert.Contains("further files omitted — the review budget was reached", prompt);

        // A file left out entirely must not be quietly absent: the last one shown is announced as an
        // excerpt, and the omission is stated. Silence here is a reviewer approving work it never saw.
        Assert.DoesNotContain("file5.md", prompt);
    }

    /// <summary>
    /// Orchestrator.MaxReviewFileChars, end to end — the second half of the 2026-09-07 defect and
    /// the half no test covered. The prompt-side notice was fixed and tested; nothing checked that
    /// the ORCHESTRATOR still hands over the file's real size, and it was the orchestrator's own cut
    /// that made the notice impossible to fire in the first place.
    /// </summary>
    [Fact]
    public async Task A_files_real_size_reaches_the_reviewer_from_a_real_run()
    {
        using var fx = new EngineFixture();

        var page = string.Join('\n', Enumerable.Range(0, 500)
            .Select(i => $"  <li class=\"nav-item\" data-index=\"{i}\">Menu entry number {i}</li>"));
        Assert.True(page.Length > 20_000, "the page has to be past the review cap for this to mean anything");

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"write the page"}"""),
            Turn.Calls1("write_file", System.Text.Json.JsonSerializer.Serialize(new
            {
                path = "index.html",
                content = page
            })),
            Turn.Says("Wrote the page."));

        var reviewer = new FakeChatProvider { WhenExhausted = Verdicts.Pass() };
        var orchestrator = fx.Build(provider, EngineFixture.Role("developer"),
            router: Routers.WithReviewer(), reviewProvider: reviewer);

        await fx.RunAsync(orchestrator, "write the menu page");

        var prompt = reviewer.Requests.Last().Messages.Last().Content ?? "";

        Assert.Contains("END OF EXCERPT", prompt);
        Assert.Contains(page.Length.ToString(), prompt);
        // Reworded 2026-09-24 with the slice it describes: the excerpt is the start AND the
        // end now, so "the rest was not shown" became "what is missing is the MIDDLE".
        Assert.Contains("Nothing here is missing from the file itself", prompt);
        Assert.Contains("the end of the file IS above", prompt);
    }
}
