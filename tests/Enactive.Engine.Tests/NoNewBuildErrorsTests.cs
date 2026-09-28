namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Builds;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// The engine's own "no new build errors": the workspace's build before the work, the build again
/// after it, compared by what each diagnostic IS - for any kind of project an ecosystem recognises.
///
/// <para>Deliberately not .NET here. The ecosystem in these tests is a wiki linter: its "build" is
/// its report, its diagnostics are broken links and empty titles, and it owns the wiki's pages. If
/// the orchestrator knew anything about compilers, these could not pass.</para>
/// </summary>
public sealed class NoNewBuildErrorsTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"edit the wiki"}""";

    /// <summary>
    /// A wiki linter, as an ecosystem: present where <c>wiki.lint</c> is, owns <c>.page</c> files,
    /// and "builds" by printing its report - lines of <c>ERROR code path: message</c>.
    /// </summary>
    internal sealed class WikiLint : IEcosystem
    {
        public string Name => "wikilint";

        // Its "tests" are the wiki's link checks: present where links.txt is, printing PASS/FAIL per link.
        public EcosystemTargets? Detect(string workspaceRoot)
            => File.Exists(Path.Combine(workspaceRoot, "wiki.lint"))
                ? new(Name, ["wiki.lint"], File.Exists(Path.Combine(workspaceRoot, "links.txt")) ? ["links"] : [])
                : null;

        public bool Owns(string relativePath) => relativePath.EndsWith(".page", StringComparison.OrdinalIgnoreCase);

        public string BuildCommand(string target) => "type lint-report.txt";

        public string TestCommand(string target) => "type links.txt";

        public TestRunReport? ParseTests(string output)
        {
            var cases = output.Split('\n').Select(l => l.Trim())
                .Where(l => l.StartsWith("PASS ", StringComparison.Ordinal) || l.StartsWith("FAIL ", StringComparison.Ordinal))
                .Select(l => new TestCaseResult(l[5..], l.StartsWith("PASS", StringComparison.Ordinal) ? TestVerdict.Passed : TestVerdict.Failed))
                .ToArray();
            return cases.Length == 0 ? null : new TestRunReport(cases, null);
        }

        public IReadOnlyList<BuildDiagnostic> ParseDiagnostics(string output, string workspaceRoot)
            => output.Split('\n').Select(l => l.Trim())
                .Where(l => l.StartsWith("ERROR ", StringComparison.Ordinal))
                .Select(l =>
                {
                    var parts = l.Split(' ', 3);
                    var (path, message) = (parts[2][..parts[2].IndexOf(':')], parts[2][(parts[2].IndexOf(':') + 1)..].Trim());
                    return new BuildDiagnostic(Name, path, parts[1], DiagnosticSeverity.Error, message);
                }).ToArray();
    }

    internal const string OneBrokenLink = "ERROR W1 pages/home.page: broken link to /old";

    private static EngineFixture Wiki(bool lint = true)
    {
        var fx = new EngineFixture { EcosystemsOverride = [new WikiLint()] };
        if (lint) fx.Write("wiki.lint", "rules");
        fx.Write("lint-report.txt", OneBrokenLink + "\n");
        return fx;
    }

    private static WorkEvent[] BuildChecks(IEnumerable<WorkEvent> events)
        => events.Where(e => e.Kind == EventKind.CriterionEvaluated
                             && e.Summary.Contains(BuildRegression.Name, StringComparison.Ordinal)).ToArray();

    /// <summary>THE ONE THAT MATTERS: an error the work added is named, against the one that was already there.</summary>
    [Fact]
    public async Task An_error_the_work_added_is_named_and_the_old_one_is_not()
    {
        using var fx = Wiki();
        var worker = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"pages/new.page","content":""}""", "w1"),
            // The linter, re-run by the page write: the old error, and one the new page brought.
            Turn.Calls1("write_file", """{"path":"lint-report.txt","content":"ERROR W1 pages/home.page: broken link to /old\nERROR W2 pages/new.page: empty title\n"}""", "w2"),
            Turn.Says("Added the page."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "add a page");

        Assert.Contains(events, e => e.Summary.StartsWith("Build before the work (wikilint, wiki.lint): exit 0, 1 error(s)", StringComparison.Ordinal));
        var check = Assert.Single(BuildChecks(events));
        Assert.StartsWith("FAIL", check.Summary, StringComparison.Ordinal);
        Assert.Contains("1 error(s) not in the build before the work - W2 in pages/new.page: empty title", check.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("W1 in", check.Summary, StringComparison.Ordinal);
        // A report, not a verdict - see BuildRegression for why it does not hold the run back yet.
        Assert.True(events.Has(EventKind.TaskCompleted), events.Text());
    }

    /// <summary>An error that was there before the work is not the work's: a run is not asked for a green build it was not given.</summary>
    [Fact]
    public async Task An_error_that_was_already_there_is_not_a_regression()
    {
        using var fx = Wiki();
        var worker = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"pages/new.page","content":"# Title"}""", "w1"),
            Turn.Says("Added the page."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "add a page");

        var check = Assert.Single(BuildChecks(events));
        Assert.StartsWith("PASS", check.Summary, StringComparison.Ordinal);
        Assert.Contains("no error that was not there before the work", check.Summary, StringComparison.Ordinal);
    }

    /// <summary>A run that changed nothing the build depends on is not built again - documentation does not trigger a build.</summary>
    [Fact]
    public async Task A_run_that_touched_nothing_the_build_reads_is_not_rebuilt()
    {
        using var fx = Wiki();
        var worker = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"notes.txt","content":"a note"}""", "w1"),
            Turn.Says("Wrote a note."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "write a note");

        Assert.Empty(BuildChecks(events));
    }

    /// <summary>A workspace no ecosystem recognises pays nothing: no baseline, no build.</summary>
    [Fact]
    public async Task A_workspace_no_ecosystem_recognises_gets_no_build_at_all()
    {
        using var fx = Wiki(lint: false);
        var worker = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"pages/new.page","content":"# Title"}""", "w1"),
            Turn.Says("Added the page."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "add a page");

        Assert.DoesNotContain(events, e => e.Summary.Contains("Build before the work", StringComparison.Ordinal));
        Assert.Empty(BuildChecks(events));
    }

    /// <summary>
    /// Where a command needs an approval, the engine does not ask for one for its own check - nobody
    /// asked for it - and says why it did not build, rather than interrupting every run.
    /// </summary>
    [Fact]
    public async Task The_engine_asks_nobody_for_its_own_build()
    {
        using var fx = Wiki();
        var worker = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("write_file", """{"path":"pages/new.page","content":"# Title"}""", "w1"),
            Turn.Says("Added the page."));
        var asking = new PermissionPolicy(PermissionLevel.Execute, ["*"], ["run_command"]);

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer"), policy: asking), "add a page");

        Assert.Empty(fx.Decisions.Requests);
        Assert.Contains(events, e => e.Summary.Contains("was not taken: running it would need an approval", StringComparison.Ordinal));
        var check = Assert.Single(BuildChecks(events));
        Assert.StartsWith("NOT CHECKED", check.Summary, StringComparison.Ordinal);
    }

    // ── the comparison ──────────────────────────────────────────────────────────────────

    private static CriterionResult After(int? exit, string output)
        => new(BuildRegression.Name, "build", false, exit is null ? CriterionOutcome.Unknown : CriterionOutcome.Passed, exit,
            exit is null ? "the shell would not start it" : null, CriterionOrigin.System) { Output = output };

    private static BuildBaseline Before(int exit, string output)
        => BuildRegression.Baseline(new WikiLint(), "wiki.lint", After(exit, output), "C:/w");

    /// <summary>Passed before, fails now, and nothing in the output reads as the reason: still a broken build.</summary>
    [Fact]
    public void A_build_that_passed_and_now_fails_without_a_readable_reason_is_still_a_regression()
    {
        var result = BuildRegression.Compare(Before(0, ""), After(1, "something went wrong"), "C:/w");
        Assert.Equal(CriterionOutcome.Failed, result.Outcome);
        Assert.Contains("passed before the work and fails now", result.Detail!, StringComparison.Ordinal);
    }

    [Fact]
    public void A_build_that_could_not_run_afterwards_is_not_checked_not_passed()
    {
        var result = BuildRegression.Compare(Before(0, ""), After(null, ""), "C:/w");
        Assert.Equal(CriterionOutcome.Unknown, result.Outcome);
    }
}
