namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Enactive.Core.Permissions;
using Enactive.Core.Schedules;
using Xunit;

/// <summary>
/// Where schedules live, and what a folder can and cannot bring with it.
///
/// <para>A schedule is permission to run commands in a folder, unattended, at a time nobody is
/// watching - so the rules `ApprovalStore` is built on apply here more sharply, not less. The
/// clone test below is the same test as the one for approvals, and for the same attacker.</para>
/// </summary>
public sealed class ScheduleStoreTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "enactive-schedules", Guid.NewGuid().ToString("N"));

    private readonly ScheduleStore _store;

    public ScheduleStoreTests()
    {
        Directory.CreateDirectory(_root);
        _store = new ScheduleStore(Path.Combine(_root, "schedules.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private string Workspace(string name)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static Schedule A(string workspace, string name = "nightly", ScheduledWork? work = null)
        => new(Guid.NewGuid(), workspace, name,
               work ?? ScheduledWork.FromTemplate("improve-tests", new Dictionary<string, string> { ["scope"] = "all" }),
               ScheduleTiming.Daily(new TimeOnly(3, 0), "Europe/Berlin"),
               new PermissionPolicy(PermissionLevel.Execute, new[] { "*" }, Array.Empty<string>()),
               CreatedAt: DateTimeOffset.UtcNow);

    /// <summary>
    /// A schedule survives the round trip WHOLE. Everything on it decides what runs and what it may
    /// do, so a field that quietly does not persist is a run at the wrong time, or with permissions
    /// nobody granted.
    /// </summary>
    [Fact]
    public void A_saved_schedule_comes_back_entire()
    {
        var ws = Workspace("round-trip");
        var saved = A(ws) with
        {
            Missed = MissedRun.RunLate,
            Permissions = new PermissionPolicy(PermissionLevel.Execute, new[] { "*" }, new[] { "run_command" })
                              { Deny = new[] { "git" } }
        };

        _store.Save(saved);
        var read = Assert.Single(_store.For(ws));

        Assert.Equal(saved.Id, read.Id);
        Assert.Equal("nightly", read.Name);
        Assert.Equal(MissedRun.RunLate, read.Missed);
        Assert.Equal(ScheduleRepeat.Daily, read.Timing.Repeat);
        Assert.Equal(new TimeOnly(3, 0), read.Timing.AtLocal);
        Assert.Equal("Europe/Berlin", read.Timing.TimeZoneId);
        Assert.Equal("improve-tests", read.Work.TemplateId);
        Assert.Equal("all", read.Work.Parameters["scope"]);

        // The permission policy above all: this is what the schedule authorises in advance.
        Assert.Equal(PermissionLevel.Execute, read.Permissions.Level);
        Assert.Contains("run_command", read.Permissions.AskBefore);
        Assert.Contains("git", read.Permissions.Deny);
    }

    [Fact]
    public void Saving_the_same_id_replaces_rather_than_duplicates()
    {
        var ws = Workspace("replace");
        var first = A(ws, "morning");

        _store.Save(first);
        _store.Save(first with { Name = "evening", Timing = ScheduleTiming.Daily(new TimeOnly(18, 0), "Europe/Berlin") });

        var read = Assert.Single(_store.For(ws));
        Assert.Equal("evening", read.Name);
        Assert.Equal(new TimeOnly(18, 0), read.Timing.AtLocal);
    }

    [Fact]
    public void Schedules_belong_to_one_workspace()
    {
        var mine = Workspace("mine");
        var other = Workspace("other");

        _store.Save(A(mine));

        Assert.Single(_store.For(mine));
        Assert.Empty(_store.For(other));
    }

    [Fact]
    public void Removing_one_leaves_the_rest()
    {
        var ws = Workspace("remove");
        var keep = A(ws, "keep");
        var drop = A(ws, "drop");

        _store.Save(keep);
        _store.Save(drop);
        _store.Remove(ws, drop.Id);

        Assert.Equal("keep", Assert.Single(_store.For(ws)).Name);

        // Twice is not an error: a UI that deletes and then refreshes must not throw on the second.
        _store.Remove(ws, drop.Id);
        Assert.Single(_store.For(ws));
    }

    /// <summary>
    /// A folder that arrives carrying somebody else's <c>.enactive/workspace.json</c> does NOT
    /// arrive carrying their schedules — the same rule, and the same attacker, as for approvals. A
    /// cloned repository that could bring a nightly `run_command` with it would be a way to run
    /// anything on this machine by being cloned.
    /// </summary>
    [Fact]
    public void A_folder_carrying_another_workspaces_id_does_not_inherit_its_schedules()
    {
        var original = Workspace("original");
        WorkspaceInfo.Adopt(original);
        _store.Save(A(original));

        var clone = Workspace("clone");
        Directory.CreateDirectory(Path.Combine(clone, WorkspaceGuard.ReservedFolder));
        File.Copy(WorkspaceIdentity.PathFor(original), WorkspaceIdentity.PathFor(clone));

        // The premise: the two folders really do claim the same identity.
        Assert.Equal(WorkspaceInfo.For(original).Id, WorkspaceInfo.For(clone).Id);

        Assert.Empty(_store.For(clone));
    }

    /// <summary>
    /// An unreadable file means NO schedules. The other direction — guessing what a corrupt file
    /// meant — would run something nobody asked for at a time nobody chose.
    /// </summary>
    [Fact]
    public void A_corrupt_file_schedules_nothing()
    {
        var ws = Workspace("corrupt");
        File.WriteAllText(Path.Combine(_root, "schedules.json"), "{ this is not json");

        Assert.Empty(_store.For(ws));
    }

    /// <summary>
    /// Work that names neither a template nor a past run, or both, is not saved. A schedule that
    /// cannot say what it runs is a row that fails at 3am instead of at the moment it was made.
    /// </summary>
    [Fact]
    public void A_schedule_that_cannot_say_what_it_runs_is_refused()
    {
        var ws = Workspace("shapeless");

        _store.Save(A(ws, work: new ScheduledWork(null, new Dictionary<string, string>(), null)));
        _store.Save(A(ws, work: new ScheduledWork("t", new Dictionary<string, string>(), "{\"Goal\":\"x\"}")));

        Assert.Empty(_store.For(ws));
    }
}
