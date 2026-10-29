namespace Enactive.Server.Tests.Components;

using Bunit;
using Enactive.Core.Context;
using Enactive.Core.Rules;
using Enactive.Server.Components.Pages;
using Enactive.Server.Runs;
using Enactive.Workspace;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// A workspace's lessons from the browser (Plan Phase 5.2), as the window shows them beside a run's artifacts: read by
/// anybody signed in; a workspace's rules an Operator's to keep, dismiss and write, the Global ones an Admin's - in the
/// service's data folder, the one its engine reads - and every change audited, as a check's is.
/// </summary>
public sealed class LessonsTests : ComponentHost
{
    private readonly string _folders = Path.Combine(Path.GetTempPath(), "enactive-lessons-web", Guid.NewGuid().ToString("N"));
    private readonly string _root;

    public LessonsTests()
    {
        Services.AddSingleton(new WorkspaceRoots([_folders]));
        _root = Path.Combine(_folders, "project");
        Directory.CreateDirectory(_root);
        Services.GetRequiredService<ServerEngine>().Workspaces.Touch(_root);
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing)
        {
            Enactive.Tests.SqlitePools.ClearUnder(_folders);
            try { Directory.Delete(_folders, recursive: true); } catch { /* a temp folder */ }
        }
    }

    private const string Seen = "the report names line 11 for the untested branch; it is line 12";

    private IRuleStore WorkspaceRules => RuleStoreFactory.Create(WorkspaceInfo.For(_root));
    private JsonRuleStore GlobalRules => JsonRuleStore.Global(Services.GetRequiredService<ServerFolders>().Data);

    private async Task<RuleEntry> CandidateAsync()
    {
        var candidate = new RuleEntry(Guid.NewGuid(), RuleScope.Workspace, RuleHeading.Deviations, Seen, Guid.NewGuid(),
            DateTimeOffset.UtcNow, RuleStatus.Candidate) { Origin = "run \"write the report\", step 1: [1] FAIL (step review)", Source = "review:x" };
        await WorkspaceRules.SaveAsync(candidate, CancellationToken.None);
        return candidate;
    }

    private IRenderedComponent<LessonsScreen> Screen(string who)
    {
        SignIn(who);
        Services.GetRequiredService<NavigationManager>().NavigateTo($"/lessons?ws={WorkspaceInfo.For(_root).Id}");
        var screen = Render<LessonsScreen>();
        screen.WaitForAssertion(() => Assert.NotEmpty(screen.FindAll(".lesson-row, .lessons-empty, .said")));
        return screen;
    }

    private (DateTimeOffset At, string Account, string Action, string? Detail, string? Remote)[] Audited(string action)
        => Users.AuditTrail().Where(a => a.Action == action).ToArray();

    [Fact]
    public async Task An_operator_keeps_a_candidate_in_their_own_words_and_the_next_run_would_be_given_it()
    {
        await CandidateAsync();
        var screen = Screen("alice");
        Assert.Contains("FAIL (step review)", screen.Find(".candidate .lesson-origin").TextContent);

        screen.Find(".candidate .lesson-text").Input("Name a source line by the number the file shows.");
        screen.Find(".candidate .lesson-keep").Click();

        screen.WaitForAssertion(() => Assert.NotEmpty(screen.FindAll(".rule")));
        var kept = Assert.Single(await WorkspaceRules.ListAsync(CancellationToken.None));
        Assert.Equal((RuleStatus.Confirmed, "Name a source line by the number the file shows."), (kept.Status, kept.Text));
        Assert.Equal("alice", Assert.Single(Audited("rule kept")).Account);
        Assert.Single(AppliedRules.Compose([], [kept]).Rules);
    }

    [Fact]
    public async Task An_operator_may_not_make_a_rule_global_and_an_admin_may_in_the_service_s_data_folder()
    {
        await CandidateAsync();
        var screen = Screen("alice");
        screen.Find(".candidate input[type=checkbox]").Change(true);
        screen.Find(".candidate .lesson-keep").Click();
        screen.WaitForAssertion(() => Assert.Contains("every workspace", screen.Find(".lessons-said").TextContent));
        Assert.Equal(RuleStatus.Candidate, Assert.Single(await WorkspaceRules.ListAsync(CancellationToken.None)).Status);
        Assert.Empty(Audited("rule kept"));

        screen = Screen("ada");
        screen.Find(".candidate input[type=checkbox]").Change(true);
        screen.Find(".candidate .lesson-keep").Click();
        screen.WaitForAssertion(() => Assert.NotEmpty(screen.FindAll(".rule")));
        Assert.Empty(await WorkspaceRules.ListAsync(CancellationToken.None));
        Assert.Equal(RuleScope.Global, Assert.Single(await GlobalRules.ListAsync(CancellationToken.None)).Scope);
        Assert.Equal("ada", Assert.Single(Audited("rule kept")).Account);
    }

    [Fact]
    public async Task A_viewer_reads_the_lessons_and_is_offered_no_change()
    {
        await CandidateAsync();
        var screen = Screen("vera");
        Assert.Contains(Seen, screen.Find(".candidate .lesson-text").GetAttribute("value") ?? screen.Find(".candidate .lesson-text").TextContent);
        Assert.Empty(screen.FindAll(".lesson-keep, .lesson-dismiss, .lesson-add"));
        Assert.Equal(RuleStatus.Candidate, Assert.Single(await WorkspaceRules.ListAsync(CancellationToken.None)).Status);
    }

    [Fact]
    public async Task An_operator_dismisses_a_candidate_and_writes_a_rule_of_their_own()
    {
        var candidate = await CandidateAsync();
        var screen = Screen("alice");
        screen.Find(".candidate .lesson-dismiss").Click();
        screen.WaitForAssertion(() => Assert.Empty(screen.FindAll(".candidate")));
        Assert.Equal(RuleStatus.Dismissed, (await WorkspaceRules.ListAsync(CancellationToken.None)).Single(r => r.Id == candidate.Id).Status);

        screen.Find(".lesson-new").Input("Leave the project file alone.");
        screen.Find(".lesson-add").Click();
        screen.WaitForAssertion(() => Assert.NotEmpty(screen.FindAll(".rule")));
        Assert.Contains((await WorkspaceRules.ListAsync(CancellationToken.None)),
            r => r.Status == RuleStatus.Confirmed && r.Text == "Leave the project file alone.");
        Assert.Single(Audited("lesson dismissed"));
        Assert.Single(Audited("rule added"));
    }
}
