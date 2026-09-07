namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Core.Workers;
using Enactive.Tools;
using Xunit;

/// <summary>
/// Docs/FIX_PLAN.md §9b: the suite exercised a world of its own making, and every defect that
/// reached the user on 2026-09-07 lived in the gap between that world and the real one.
///
/// <para>Two gaps, both structural rather than accidental:</para>
///
/// <para><b>No test ran through a shipping role.</b> Every <c>fx.Build(...)</c> passed a hand-written
/// <c>WorkerWith("write_file", …)</c>, so what a REAL role may do was never exercised — and a tool
/// missing from a real allowlist was invisible by construction. That is exactly how
/// <c>edit_file</c> shipped registered, documented in the developer's own instructions, and
/// reachable by nobody. Writing this file found three more in the same state: <c>search_files</c>,
/// <c>create_directory</c> and <c>move_file</c> were registered by the desktop host and named by no
/// role at all, so the search tool built to stop runs from wandering had never been offered to a
/// model.</para>
///
/// <para><b>The fixture world was LF-only.</b> Every test handed <c>EngineFixture.Write</c> a string
/// full of <c>\n</c>. The product ships on Windows, where most text files are CRLF, so
/// <c>edit_file</c> was never tried against one until a user found it refusing every edit on a real
/// page. The end-to-end tests here run over both.</para>
/// </summary>
public sealed class ShippingRolesTests
{
    // ── the reachability guard ──────────────────────────────────────────────

    /// <summary>
    /// A tool no role names is a tool that does not exist. The host registers it, the registry holds
    /// it, the permission engine would allow it — and the model is never told it is there, because
    /// the tool list a worker is offered is filtered by its allowlist.
    ///
    /// <para>Both directions are checked: a registered tool nobody can reach is dead code that reads
    /// as a capability, and a role naming a tool no host registers advertises something that is not
    /// there. Both were true of this build.</para>
    /// </summary>
    [Fact]
    public void Every_tool_this_build_registers_is_offered_to_some_role()
    {
        var registered = EngineFixture.ShippedTools().Select(t => t.Definition.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var named = DefaultWorkers.Seed(new Core.Providers.ModelRef("fake", "fake-model"))
            .SelectMany(w => w.ToolAllowlist)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var unreachable = registered.Except(named).OrderBy(n => n).ToArray();
        Assert.True(unreachable.Length == 0,
            "Registered and named by no role, so no model is ever offered it: "
            + string.Join(", ", unreachable)
            + ". Add it to a role in DefaultWorkers, or stop registering it.");

        var missing = named.Except(registered).OrderBy(n => n).ToArray();
        Assert.True(missing.Length == 0,
            "Named by a role and registered by no host, so the role advertises a capability it does "
            + "not have: " + string.Join(", ", missing));
    }

    /// <summary>
    /// The rule the allowlists follow, asserted rather than left to memory: reading is for everyone,
    /// and a role that may write a file may also create a folder and move one.
    /// </summary>
    [Fact]
    public void The_rule_the_allowlists_follow()
    {
        foreach (var worker in DefaultWorkers.Seed(new Core.Providers.ModelRef("fake", "fake-model")))
        {
            Assert.Contains("read_file", worker.ToolAllowlist);
            Assert.Contains("search_files", worker.ToolAllowlist);
            Assert.Contains("list_dir", worker.ToolAllowlist);

            if (!worker.ToolAllowlist.Contains("write_file"))
                continue;

            Assert.Contains("edit_file", worker.ToolAllowlist);
            Assert.Contains("create_directory", worker.ToolAllowlist);
            Assert.Contains("move_file", worker.ToolAllowlist);
        }
    }

    /// <summary>
    /// The reviewer is the one role that must not gain anything from the rule above. Observe means
    /// read and list; every capability added since has to have left it exactly as narrow.
    /// </summary>
    [Fact]
    public void The_reviewer_can_still_only_look()
    {
        var reviewer = EngineFixture.Role("reviewer");

        Assert.Equal(PermissionLevel.Observe, reviewer.DefaultLevel);
        foreach (var write in new[] { "write_file", "edit_file", "create_directory", "move_file", "run_command", "run_powershell", "git", "docker" })
            Assert.DoesNotContain(write, reviewer.ToolAllowlist);
    }

    // ── end to end, through a real role ─────────────────────────────────────

    /// <summary>
    /// Every shipping role, driven through the whole engine with its OWN allowlist, doing the most
    /// ordinary thing it is for. Nothing here is clever: the point is that the role definition the
    /// product ships is the one under test, so a tool taken out of a list — or never put in one —
    /// fails here instead of in a user's workspace.
    /// </summary>
    [Theory]
    [InlineData("developer", "edit_file")]
    [InlineData("writer", "edit_file")]
    public async Task A_role_that_edits_files_can_edit_one_end_to_end(string roleId, string tool)
    {
        using var fx = new EngineFixture();
        fx.Write("doc.md", "# Title\nintro\nCHANGE ME\ntail\n");

        var provider = new FakeChatProvider(
            Plan("fix a line"),
            Turn.Calls1(tool, """{"path":"doc.md","old_string":"CHANGE ME","new_string":"CHANGED"}"""),
            Turn.Says("Replaced the line."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role(roleId)), "fix the line in doc.md");

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
        Assert.Equal("# Title\nintro\nCHANGED\ntail\n", fx.Read("doc.md"));
    }

    /// <summary>
    /// The same run on a CRLF file — the shape of the one that failed for a user. Two things have to
    /// hold together for this to pass: the role must be offered edit_file at all, and the tool must
    /// match a passage the model typed with LFs against a file that has none.
    /// </summary>
    [Theory]
    [MemberData(nameof(BothEndings))]
    public async Task A_developer_can_edit_a_file_whatever_its_line_endings(Newline endings)
    {
        using var fx = new EngineFixture();
        fx.Write("index.html", "<header>\n  <nav>\n    <a>Home</a>\n  </nav>\n</header>\n", endings);

        // The passage as a model types it: LF, because a carriage return is invisible in what
        // read_file returned to it.
        var provider = new FakeChatProvider(
            Plan("add a menu entry"),
            Turn.Calls1("edit_file", System.Text.Json.JsonSerializer.Serialize(new
            {
                path = "index.html",
                old_string = "    <a>Home</a>\n",
                new_string = "    <a>Home</a>\n    <a>Remote</a>\n"
            })),
            Turn.Says("Added the menu entry."));

        var events = await fx.RunAsync(
            fx.Build(provider, EngineFixture.Role("developer")), "add a Remote link to the menu");

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());

        var after = fx.Read("index.html");
        Assert.Contains("<a>Remote</a>", after);

        // And the file keeps the endings it had - an edit must not convert the document.
        Assert.Equal(endings == Newline.Crlf, after.Contains('\r'));
        if (endings == Newline.Crlf)
            Assert.Equal(after.Split('\n').Length - 1, CountCrLf(after));
    }

    /// <summary>
    /// A writer creating a document where one did not exist, folder and all, through its real
    /// allowlist. create_directory was registered and named by no role, so this could not have run.
    /// </summary>
    [Fact]
    public async Task A_writer_can_make_a_folder_and_put_a_document_in_it()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Plan("start a document"),
            Turn.Calls1("create_directory", """{"path":"docs"}"""),
            Turn.Calls1("write_file", """{"path":"docs/notes.md","content":"# Notes\n\nFirst draft.\n"}""", "call_2"),
            Turn.Says("Created docs/notes.md."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("writer")), "start a notes document");

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());
        Assert.True(fx.Exists("docs/notes.md"));
    }

    /// <summary>
    /// The reviewer, investigating: it may read and search, and the search tool it was never given
    /// until now is the difference between finding a string and reading the whole workspace to look
    /// for it.
    /// </summary>
    [Fact]
    public async Task A_reviewer_can_search_the_workspace_end_to_end()
    {
        using var fx = new EngineFixture();
        fx.Write("src/a.cs", "class A { void Broken() { } }\n");
        fx.Write("src/b.cs", "class B { }\n");

        var provider = new FakeChatProvider(
            Plan("find a symbol"),
            Turn.Calls1("search_files", """{"pattern":"Broken"}"""),
            Turn.Says("Found it in src/a.cs."));

        var events = await fx.RunAsync(fx.Build(provider, EngineFixture.Role("reviewer")), "where is Broken defined");

        Assert.False(events.Has(EventKind.TaskFailed), events.Text());

        // The search actually ran and actually found it - not merely "was not refused".
        var results = events.OfKind(EventKind.ToolResult).Select(e => e.Summary).ToArray();
        Assert.Contains(results, s => s.StartsWith("search_files -> ok", StringComparison.Ordinal)
                                   && s.Contains("src/a.cs", StringComparison.Ordinal));
    }

    /// <summary>
    /// And the other half of a role: what it may NOT do. A reviewer asked to write is refused by the
    /// engine, not by the model's good manners — this is the assertion that would catch a widening
    /// of the Observe role by accident.
    /// </summary>
    [Fact]
    public async Task A_reviewer_asked_to_write_is_refused_by_the_engine()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Plan("write a file"),
            Turn.Calls1("write_file", """{"path":"notes.md","content":"I was here"}"""),
            Turn.Says("I cannot write."));

        await fx.RunAsync(
            fx.Build(provider, EngineFixture.Role("reviewer"), policy: PermissionPolicy.PermissiveDefault),
            "write notes.md");

        Assert.False(fx.Exists("notes.md"));
    }

    /// <summary>
    /// Ops runs things and reads things, and must not be able to rewrite the source it is looking at.
    /// The role's own instructions say "avoid editing source files"; an instruction is not an
    /// enforcement, and this is the enforcement.
    /// </summary>
    [Fact]
    public async Task Ops_may_run_a_command_but_not_rewrite_a_file()
    {
        using var fx = new EngineFixture();
        fx.Write("app.config", "<config />\n");

        var ops = EngineFixture.Role("ops");
        Assert.Contains("run_command", ops.ToolAllowlist);
        Assert.DoesNotContain("write_file", ops.ToolAllowlist);

        var provider = new FakeChatProvider(
            Plan("change a config"),
            Turn.Calls1("write_file", """{"path":"app.config","content":"<config changed=\"true\" />"}"""),
            Turn.Says("Not something I can do."));

        await fx.RunAsync(fx.Build(provider, ops), "change app.config");

        Assert.Equal("<config />\n", fx.Read("app.config"));
    }

    /// <summary>
    /// What a role is OFFERED is the thing that decides what it can attempt, and it is filtered from
    /// the allowlist before the model ever sees it. Asserting on the request rather than on the
    /// outcome is what makes "registered but unreachable" visible at all: an unreachable tool
    /// produces no failure anywhere, it simply never appears.
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryRole))]
    public async Task A_role_is_offered_exactly_the_tools_it_names(string roleId)
    {
        using var fx = new EngineFixture();
        var role = EngineFixture.Role(roleId);
        var provider = new FakeChatProvider(Plan("look around"), Turn.Says("nothing to do"));

        await fx.RunAsync(fx.Build(provider, role), "look around");

        var offered = provider.Requests
            .SelectMany(r => r.Tools ?? Array.Empty<ToolDefinition>())
            .Select(t => t.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Only the tools this build actually registers can be offered, so the expectation is the
        // intersection - a role naming git in a host without git is a different test, above.
        var registered = EngineFixture.ShippedTools().Select(t => t.Definition.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var expected = role.ToolAllowlist.Where(registered.Contains).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Equal(expected.OrderBy(n => n), offered.OrderBy(n => n));
    }

    /// <summary>The planner's turn. Every run starts with one, so every script has to supply it.</summary>
    private static Turn Plan(string title)
        => Turn.Says($$"""{"disposition":"quick_action","title":"{{title}}"}""");

    public static TheoryData<Newline> BothEndings => EngineFixture.Endings;
    public static TheoryData<string> EveryRole => EngineFixture.ShippingRoles;

    private static int CountCrLf(string text)
    {
        var n = 0;
        for (var i = 0; i + 1 < text.Length; i++)
            if (text[i] == '\r' && text[i + 1] == '\n')
                n++;
        return n;
    }
}
