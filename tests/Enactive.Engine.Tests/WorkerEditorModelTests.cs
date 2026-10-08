namespace Enactive.Engine.Tests;

using Enactive.App.Ui.ViewModels;
using Enactive.Core.Guidance;
using Enactive.Settings;
using Xunit;

/// <summary>
/// The team member editor writes back the model a person chose, and nothing else.
///
/// <para><b>What went wrong:</b> a worker whose model was empty opened with the first model of the
/// catalog already selected, and Apply wrote the selection back. Changing only the role or the
/// instructions therefore assigned a provider and a model nobody had picked - the first one in the
/// list, which may well be a hosted, paid one. An empty model is not a gap to fill in: it is
/// "nobody has chosen yet", and the dialog had no way to show that state or to return to it.</para>
/// </summary>
public sealed class WorkerEditorModelTests
{
    private static readonly string[] Catalog = ["hosted/large", "local/small"];

    private static WorkerEditViewModel Open(WorkerConfig config)
        => new(config, Catalog, toolCatalog: [], onSaved: () => { });

    /// <summary>
    /// A tool saved in another case is ticked, and survives opening the role and pressing Save. The rows are told
    /// apart ignoring case and the ticks were read with it, so Write_File showed an unticked write_file - and Save
    /// took the tool from the role.
    /// </summary>
    [Fact]
    public void A_tool_saved_in_another_case_survives_the_dialog()
    {
        var config = new WorkerConfig { Id = "developer", Role = "Developer", Tools = ["Write_File", "read_file"] };
        var editor = new WorkerEditViewModel(config, Catalog, toolCatalog: ["read_file", "write_file"], onSaved: () => { });

        Assert.True(editor.Tools.Single(t => t.Name == "write_file").IsSelected);
        editor.SaveCommand.Execute(null);

        Assert.Contains("write_file", config.Tools, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The hint under the tools says what the ticks permit: every tool when "*" is ticked, whatever else is; nothing
    /// when nothing is; otherwise the tools by name. The silent states are the dangerous ones.
    /// </summary>
    [Theory]
    [InlineData(new[] { "*", "read_file" }, "may call EVERY tool")]
    [InlineData(new string[0], "Nothing selected")]
    [InlineData(new[] { "read_file", "write_file" }, "2 tool(s): read_file, write_file.")]
    public void The_tools_hint_says_what_the_ticks_permit(string[] ticked, string said)
    {
        var config = new WorkerConfig { Id = "developer", Role = "Developer", Tools = [.. ticked] };
        var editor = new WorkerEditViewModel(config, Catalog, toolCatalog: ["read_file", "write_file"], onSaved: () => { });

        Assert.Contains(said, editor.ToolsHint);
    }

    /// <summary>
    /// Whether a list grants every tool is asked of ToolAllowlist. The role editor's hint tested its list for "*" by
    /// itself - the same answer today, and a second copy of the rule the gate and every other screen read. The two
    /// that remain read a PERMISSION POLICY, which ToolAllowlist is not for (its own summary says so): the permission
    /// engine, and the words a schedule is described in.
    /// </summary>
    [Fact]
    public void Only_the_allowlist_asks_whether_a_list_grants_every_tool()
    {
        string[] policies = ["PermissionEngine.cs", "ScheduleWords.cs"];
        var src = Path.Combine(TestRepository.Root, "src");
        var askers = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                        && Path.GetFileName(f) != "ToolAllowlist.cs" && !policies.Contains(Path.GetFileName(f)))
            .SelectMany(f => File.ReadLines(f).Select(line => (f, line)))
            .Where(x => System.Text.RegularExpressions.Regex.IsMatch(x.line, @"Contains\([^)]*""\*""\)|==\s*""\*""|""\*""\s*=="))
            .Select(x => $"{Path.GetFileName(x.f)}: {x.line.Trim()}")
            .ToArray();

        Assert.True(askers.Length == 0, "Asks for \"*\" outside ToolAllowlist:\n" + string.Join("\n", askers));
    }

    [Fact]
    public void A_worker_nobody_chose_a_model_for_is_still_unassigned_after_Apply()
    {
        var config = new WorkerConfig { Id = "developer", Role = "Developer", Model = string.Empty };
        var editor = Open(config);

        editor.Role = "Developer, renamed";
        editor.SaveCommand.Execute(null);

        Assert.Equal("Developer, renamed", config.Role);
        Assert.Equal(string.Empty, config.Model);
    }

    /// <summary>
    /// The state has to be on the screen as an item, not as an empty box: a ComboBox with nothing
    /// selected cannot be returned to once something has been picked.
    /// </summary>
    [Fact]
    public void Unassigned_is_an_item_in_the_list_and_is_the_one_selected()
    {
        var editor = Open(new WorkerConfig { Id = "developer" });

        Assert.Equal("(none)", editor.Models[0]);
        Assert.Equal("(none)", editor.Model);
        Assert.Equal(Catalog, editor.Models.Skip(1));
    }

    [Fact]
    public void A_model_that_is_chosen_is_saved()
    {
        var config = new WorkerConfig { Id = "developer" };
        var editor = Open(config);

        editor.Model = "local/small";
        editor.SaveCommand.Execute(null);

        Assert.Equal("local/small", config.Model);
    }

    [Fact]
    public void Choosing_the_unassigned_item_clears_a_model_the_worker_had()
    {
        var config = new WorkerConfig { Id = "developer", Model = "hosted/large" };
        var editor = Open(config);
        Assert.Equal("hosted/large", editor.Model);

        editor.Model = editor.Models[0];
        editor.SaveCommand.Execute(null);

        Assert.Equal(string.Empty, config.Model);
    }

    /// <summary>
    /// A ComboBox whose selection is cleared hands null through the binding. That is no choice
    /// either, and it is saved as one rather than as whatever the list happens to start with.
    /// </summary>
    [Fact]
    public void A_cleared_selection_is_saved_as_unassigned()
    {
        var config = new WorkerConfig { Id = "developer", Model = "hosted/large" };
        var editor = Open(config);

        editor.Model = null;
        editor.SaveCommand.Execute(null);

        Assert.Equal(string.Empty, config.Model);
    }

    [Fact]
    public void A_saved_model_the_catalog_no_longer_offers_survives_the_dialog()
    {
        var config = new WorkerConfig { Id = "developer", Model = "gone/model" };
        var editor = Open(config);

        Assert.Contains("gone/model", editor.Models);
        editor.SaveCommand.Execute(null);

        Assert.Equal("gone/model", config.Model);
    }

    /// <summary>
    /// (none) on the model is not "this worker will not run": the engine runs it on the first model
    /// of the first provider. The hint beside the field is the only place a person can learn that
    /// before a run shows it.
    /// </summary>
    [Fact]
    public void The_model_hint_says_what_runs_when_none_is_chosen()
        => Assert.Contains("first model of the first provider", RoleAdvice.Model, StringComparison.Ordinal);
}
