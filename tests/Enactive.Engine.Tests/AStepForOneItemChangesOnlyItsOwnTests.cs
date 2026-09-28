namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// Run 4f1d97, 2026-09-28: twelve page steps each edited one shared report. A step for one item changes
/// its item and what it creates; a document the engine assembles is changed by no step; a tool that
/// cannot be checked is not offered where the engine assembles the results.
/// </summary>
public sealed class AStepForOneItemChangesOnlyItsOwnTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("boundary").FullName;
    private readonly HashSet<string> _owned = new(StringComparer.OrdinalIgnoreCase);

    public void Dispose() { try { Directory.Delete(_root, true); } catch (IOException) { } }

    private void Write(string rel, string text = "x")
    {
        var full = Path.Combine(_root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    private WriteBoundary Boundary(IReadOnlyList<string>? items, params string[] reserved)
        => new(_root, reserved, items, () => _owned, p => _owned.Add(p));

    private static readonly ToolDefinition Writer = new("write_file", "w", "{}", WorkspaceEffect.Changed, ["path"]);
    private static readonly ToolDefinition Mover = new("move_file", "m", "{}", WorkspaceEffect.Changed, ["from", "to"]);
    private static readonly ToolDefinition Reader = new("read_file", "r", "{}", WorkspaceEffect.None);

    private static ToolCall Call(string args, string id = "c") => new(id, "write_file", args);

    [Fact]
    public void A_step_for_one_item_changes_its_item_and_what_it_creates_and_nothing_shared()
    {
        Write("wiki/a.md");
        Write("Docs/REPORT.md");
        Write("src/Mod/a.cs");
        var page = Boundary(["wiki/a.md"]);

        Assert.Null(page.Refuse(Call("""{"path":"wiki/a.md"}"""), Writer, _ => false));                 // its own item
        Assert.Contains("is not this step's to change", page.Refuse(Call("""{"path":"Docs/REPORT.md"}"""), Writer, _ => false));
        Assert.Null(page.Refuse(Call("""{"path":"notes/a.md"}""", "n1"), Writer, _ => false));          // new: its to create
        page.Succeeded(new ToolCall("n1", "write_file", """{"path":"notes/a.md"}"""));
        Write("notes/a.md");
        Assert.Null(page.Refuse(Call("""{"path":"./notes\\a.md"}"""), Writer, _ => false));            // and then to change
        Assert.Contains("is not this step's to change",
            page.Refuse(new ToolCall("m", "move_file", """{"from":"Docs/REPORT.md","to":"notes/b.md"}"""), Mover, _ => false));

        var module = Boundary(["src/Mod"]);                                                               // an item that is a folder
        Assert.Null(module.Refuse(Call("""{"path":"src/Mod/a.cs"}"""), Writer, _ => false));
        Assert.Null(page.Refuse(Call("""{"path":"Docs/REPORT.md"}"""), Reader, _ => false));            // declares no path: not this check
    }

    [Fact]
    public void A_document_the_engine_assembles_is_changed_by_no_step()
    {
        var anyStep = Boundary(null, "Docs/REPORT.md");
        Assert.Contains("is written by the engine", anyStep.Refuse(Call("""{"path":"Docs\\REPORT.md"}"""), Writer, _ => false));
        Assert.Null(anyStep.Refuse(Call("""{"path":"Docs/other.md"}"""), Writer, _ => false));
    }

    [Fact]
    public void A_path_through_a_link_is_refused()
    {
        Directory.CreateDirectory(Path.Combine(_root, "outside-target"));
        try { Directory.CreateSymbolicLink(Path.Combine(_root, "wiki"), Path.Combine(_root, "outside-target")); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }   // this machine may not make links

        Assert.Contains("goes through a link", Boundary(["wiki/a.md"]).Refuse(Call("""{"path":"wiki/a.md"}"""), Writer, _ => false));
    }

    [Fact]
    public void What_a_step_created_stays_its_own_across_a_restart()
    {
        var task = Guid.NewGuid();
        var step = Guid.NewGuid();
        new TaskProgress(_root).Own(task, step, "notes/a.md");
        Assert.Contains("notes/a.md", new TaskProgress(_root).OwnedBy(task, step));
        Assert.Empty(new TaskProgress(_root).OwnedBy(task, Guid.NewGuid()));
    }

    // ── through a run ──────────────────────────────────────────────────────────────────

    private const string Plan = """
        {"disposition":"task","title":"review the wiki",
         "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                  {"title":"review page","dependsOn":[0],"forEach":{"step":0,"field":"pages"},"report":"Docs/REPORT.md",
                   "output":{"notes":{"type":"results","description":"a note per page"}}}]}
        """;

    /// <summary>
    /// THE ONE THAT MATTERS: where the engine assembles the results, a page step is not offered a shell,
    /// cannot edit the shared report, and hands its result on instead.
    /// </summary>
    [Fact]
    public async Task A_page_step_cannot_edit_the_shared_report_and_has_no_shell()
    {
        using var fx = new EngineFixture { StepOutputs = true, DynamicSteps = true };
        fx.Write("wiki/a.md", "# A\n");
        fx.Write("Docs/REPORT.md", "| a | Pending |\n");
        var worker = new FakeChatProvider(
            Turn.Says(Plan),
            Turn.Calls1(StepOutputContract.ToolName, """{"pages":["wiki/a.md"]}""", "s0"), Turn.Says("Found one."),
            Turn.Calls1("edit_file", """{"path":"Docs/REPORT.md","old_string":"| a | Pending |","new_string":"| a | Reviewed |"}""", "e1"),
            Turn.Calls1(StepOutputContract.ToolName, """{"notes":{"wiki/a.md":"fine"}}""", "s1"),
            Turn.Says("Reviewed a."));

        var events = await fx.RunAsync(fx.Build(worker, EngineFixture.Role("developer")), "review every page");

        Assert.Contains(events, e => e.Kind == EventKind.ToolResult && e.Summary.StartsWith("edit_file -> refused: 'Docs/REPORT.md' is written by the engine", StringComparison.Ordinal));
        var itemRequest = worker.Requests.First(r => r.Messages.Any(m => (m.Content ?? "").Contains("Proceed with this step of the plan: review page: wiki/a.md", StringComparison.Ordinal)));
        Assert.DoesNotContain(itemRequest.Tools!, t => t.Name is "run_command" or "run_powershell" or "git");
        Assert.Contains(itemRequest.Tools!, t => t.Name == "read_file");
    }
}
