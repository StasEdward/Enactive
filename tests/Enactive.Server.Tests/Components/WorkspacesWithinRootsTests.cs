namespace Enactive.Server.Tests.Components;

using System.Security.Claims;
using Bunit;
using Enactive.Agents;
using Enactive.Application;
using Enactive.Core.Context;
using Enactive.Core.History;
using Enactive.Core.Inbox;
using Enactive.Server.Accounts;
using Enactive.Server.Components.Runs;
using Enactive.Server.Runs;
using Enactive.Workspace;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;

/// <summary>
/// The browser's workspaces are limited to the service's roots (ADR 0002), not only its runs. The registry the service
/// keeps (workspaces.json) is its account's, which a desktop under the same account writes too, and its entries stay when
/// the roots are narrowed: an entry outside the roots is not listed, read, probed or acted on - by any service a page
/// calls, as the services are registered on the server - and an address naming it lands on "not known". Adding a
/// workspace outside the roots is refused too, at the service.
/// </summary>
public sealed class WorkspacesWithinRootsTests : ComponentHost
{
    private readonly string _folders = Path.Combine(Path.GetTempPath(), "enactive-within-roots", Guid.NewGuid().ToString("N"));
    private readonly string _inside;
    private readonly string _outside;
    private readonly List<string> _probed = [];

    public WorkspacesWithinRootsTests()
    {
        var roots = Path.Combine(_folders, "work");
        Services.AddSingleton(new WorkspaceRoots([roots]));
        Services.AddSingleton<ProbeEnvironment>(Probe);

        _inside = Path.Combine(roots, "project");
        _outside = Path.Combine(_folders, "outsider");
        Directory.CreateDirectory(_inside);
        Directory.CreateDirectory(_outside);
        File.WriteAllText(Path.Combine(_outside, "secret.md"), "the server's own notes");
        // Known to the registry - opened by a desktop of the same account, or under roots since narrowed.
        var engine = Services.GetRequiredService<ServerEngine>();
        engine.Workspaces.Touch(_outside);
        engine.Workspaces.Touch(_inside);
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

    private Task<(EnvironmentInfo Info, EnvironmentSnapshot Snapshot)> Probe(WorkspaceInfo workspace, CancellationToken ct)
    {
        _probed.Add(workspace.RootPath);
        var info = new EnvironmentInfo(Guid.NewGuid(), workspace.Id, new HostInfo("Windows 11", "X64", "build-01", "svc", 8, null, null), [], [], [], []);
        return Task.FromResult((info, new EnvironmentSnapshot(Guid.NewGuid(), info.Id, DateTimeOffset.UtcNow, 0, 40, [], new Dictionary<string, object?>())));
    }

    private ClaimsPrincipal As(string name) => ServerAuth.PrincipalOf(Users.Find(name)!);

    private T Service<T>() where T : notnull => Services.GetRequiredService<T>();

    private const string NotKnown = "does not know that folder as a workspace";

    [Fact]
    public async Task A_known_folder_outside_the_roots_is_not_listed_or_read()
    {
        var record = new RunRecord(Guid.NewGuid(), Guid.NewGuid(), "digest the inbox", "qwen", DateTimeOffset.Now, DateTimeOffset.Now,
            "Completed", [], ["secret.md"], []);
        await RunStoreFactory.Create(WorkspaceInfo.For(_outside)).SaveAsync(record, CancellationToken.None);
        var item = new InboxItem(Guid.NewGuid(), WorkspaceInfo.For(_outside).Id, "result", "digest the inbox", "done", record.RunId,
            "unread", DateTimeOffset.UtcNow);
        await InboxStoreFactory.Create(WorkspaceInfo.For(_outside)).AppendAsync(item, CancellationToken.None);
        var set = Path.GetFileName(Service<ServerStaging>().Begin(_outside, Guid.NewGuid(), "alice", "tidy the notes"));
        var vera = As("vera");

        var history = Service<ServerHistory>();
        Assert.Equal([Path.GetFullPath(_inside)], history.Workspaces(vera).Select(w => w.RootPath));
        Assert.Null(history.Workspace(vera, _outside));
        Assert.Contains(NotKnown, history.File(vera, _outside, "secret.md").Problem);
        Assert.Contains(NotKnown, (await history.ListAsync(vera, _outside, CancellationToken.None)).Problem);
        Assert.Contains(NotKnown, (await history.OpenAsync(vera, _outside, record.RunId, CancellationToken.None)).Problem);

        var reports = Service<ServerReports>();
        Assert.Contains(NotKnown, (await reports.TimelineAsync(vera, _outside, CancellationToken.None)).Problem);
        Assert.Contains(NotKnown, (await reports.EnvironmentAsync(vera, _outside, CancellationToken.None)).Problem);
        Assert.Empty(_probed);

        Assert.Contains(NotKnown, Service<ServerLibrary>().Library(vera, _outside).Problem);
        Assert.Contains(NotKnown, Service<ServerLibrary>().Schedules(vera, _outside).Problem);

        var inbox = Service<ServerInbox>();
        Assert.DoesNotContain(await inbox.ListAsync(vera, CancellationToken.None), e => e.Item.Id == item.Id);
        Assert.Null(await inbox.TimelineAsync(vera, _outside, record.RunId, CancellationToken.None));

        var staging = Service<ServerStaging>();
        Assert.DoesNotContain(staging.List(vera), s => s.Id == set);
        Assert.Null(staging.Open(vera, _outside, set).Set);

        Assert.Null(Service<WorkspaceIds>().Find(WorkspaceInfo.For(_outside).Id));
        Assert.NotNull(Service<WorkspaceIds>().Find(WorkspaceInfo.For(_inside).Id));
    }

    [Fact]
    public async Task A_known_folder_outside_the_roots_is_not_acted_on()
    {
        var record = new RunRecord(Guid.NewGuid(), Guid.NewGuid(), "digest the inbox", "qwen", DateTimeOffset.Now, DateTimeOffset.Now,
            "Completed", [], ["secret.md"], []);
        await RunStoreFactory.Create(WorkspaceInfo.For(_outside)).SaveAsync(record, CancellationToken.None);
        var item = new InboxItem(Guid.NewGuid(), WorkspaceInfo.For(_outside).Id, "result", "digest the inbox", "done", record.RunId,
            "unread", DateTimeOffset.UtcNow);
        await InboxStoreFactory.Create(WorkspaceInfo.For(_outside)).AppendAsync(item, CancellationToken.None);
        var alice = As("alice");

        var history = Service<ServerHistory>();
        var (status, done) = history.DeleteFile(alice, _outside, "secret.md");
        Assert.False(done);
        Assert.Contains(NotKnown, status);
        Assert.True(File.Exists(Path.Combine(_outside, "secret.md")));
        Assert.Contains(NotKnown, (await history.DoAsync(alice, _outside, ServerHistory.Act.DeleteRun, record.RunId, null, "confirm",
            CancellationToken.None)).Said);
        Assert.Single((await RunStoreFactory.Create(WorkspaceInfo.For(_outside)).LoadSummariesAsync(CancellationToken.None)));
        Assert.Contains(NotKnown, history.SaveSettings(alice, _outside, AutonomyTiers.Highest, null, false));
        Assert.NotEqual(AutonomyTiers.Highest, Service<ServerEngine>().Workspaces.Find(_outside)!.Autonomy);

        var library = Service<ServerLibrary>();
        var (saved, said) = library.SaveTemplate(alice, _outside, """{ "id": "weekly", "name": "Weekly", "goal": "Write the week." }""",
            TemplateScope.Workspace);
        Assert.False(saved);
        Assert.Contains(NotKnown, said);
        Assert.Null(new TemplateStore(_outside).Inventory().FirstOrDefault(t => t.Template.Id == "weekly" && t.Scope == TemplateScope.Workspace));

        Assert.Contains(NotKnown, await Service<ServerInbox>().MarkReadAsync(alice, _outside, item.Id, CancellationToken.None));
        Assert.Contains(NotKnown, (await Service<ServerInbox>().AnswerAsync(alice, _outside, item.Id, Guid.NewGuid(), "allow", CancellationToken.None)).Said);
        await Service<ServerInbox>().MarkAllReadAsync(alice, CancellationToken.None);
        Assert.Equal("unread", (await InboxStoreFactory.Create(WorkspaceInfo.For(_outside)).LoadAllAsync(CancellationToken.None)).Single().Status);

        var set = Path.GetFileName(Service<ServerStaging>().Begin(_outside, Guid.NewGuid(), "alice", "tidy the notes"));
        Assert.Equal("These proposals are no longer kept.",
            await Service<ServerStaging>().ActAsync(alice, _outside, set, Guid.NewGuid(), apply: true, CancellationToken.None));
    }

    /// <summary>
    /// A folder is made a workspace only under the roots, whoever asks and however the path is typed: browsing outside them
    /// lists nothing, and adding a folder there is refused before the registry is asked.
    /// </summary>
    [Fact]
    public void Browsing_and_adding_stay_under_the_roots()
    {
        var history = Service<ServerHistory>();
        var alice = As("alice");

        var (folders, problem) = history.Browse(alice, _folders);
        Assert.Empty(folders);
        Assert.Contains("is not under the folders the service runs tasks in", problem);
        Assert.Equal([Path.GetFullPath(_inside)], history.Browse(alice, Path.Combine(_folders, "work")).Folders);

        var elsewhere = Path.Combine(_folders, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        Assert.Contains("is not under the folders the service runs tasks in", history.AddWorkspace(alice, elsewhere));
        Assert.Null(Service<ServerEngine>().Workspaces.Find(elsewhere));

        Assert.Contains("role does not include adding workspaces", history.Browse(As("vera"), _inside).Problem);
        Assert.Contains("role does not include adding workspaces", history.AddWorkspace(As("vera"), _inside));
    }

    /// <summary>The Runs screen neither offers the folder in its switcher nor opens it from an address naming it.</summary>
    [Fact]
    public void An_address_naming_a_folder_outside_the_roots_lands_on_not_known()
    {
        SignIn("vera");
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo("/");
        var screen = Render<RunsScreen>();
        screen.WaitForAssertion(() => Assert.Contains("project", screen.Markup));
        Assert.DoesNotContain("outsider", screen.Markup);

        navigation.NavigateTo($"/?ws={WorkspaceInfo.For(_outside).Id}");
        screen.WaitForAssertion(() => Assert.Contains("does not know that workspace", screen.Markup));
        Assert.DoesNotContain("outsider", screen.Markup);
    }
}
