namespace Enactive.Engine.Tests;

using Enactive.Application;
using Enactive.Core.Templates;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// The template editor's form and JSON (TemplateEditViewModel), shared by the window's editor and the service's page from
/// the application layer: the two tabs are one template, JSON that does not parse keeps its tab, and a template the
/// validator refuses is not handed to whoever saves it.
/// </summary>
public sealed class TemplateEditorTests
{
    private static readonly string[] ToolNames = ["read_file", "write_file", "run_shell"];

    private static (TemplateEditViewModel Editor, List<(TaskTemplate Template, TemplateScope Scope)> Saved) Open(TaskTemplate draft)
    {
        var saved = new List<(TaskTemplate, TemplateScope)>();
        var editor = new TemplateEditViewModel(draft, idEditable: true, [TemplateScope.Workspace, TemplateScope.Global], ToolNames,
            (template, scope) => saved.Add((template, scope)));
        return (editor, saved);
    }

    [Fact]
    public void What_the_form_says_is_what_the_json_tab_shows_and_what_is_saved()
    {
        var (editor, saved) = Open(new TaskTemplate("", "", ""));
        editor.Id = "digest";
        editor.Name = "Digest";
        editor.Goal = "Write the digest of {topic}.";
        editor.AddParameterCommand.Execute(null);
        editor.Parameters[0].Id = "topic";
        editor.Parameters[0].Name = "Topic";
        editor.Tools.Single(t => t.Name == "run_shell").Deny = true;

        editor.Tab = 1;
        var shown = TemplateStore.FromJson(editor.JsonText, out _)!;
        editor.SaveCommand.Execute(null);

        var (template, scope) = Assert.Single(saved);
        Assert.Equal(TemplateScope.Workspace, scope);
        Assert.Equal(TemplateStore.ToJson(shown), TemplateStore.ToJson(template));
        Assert.Equal(["run_shell"], template.Ceiling.DenyList);
        Assert.Equal("topic", Assert.Single(template.ParameterList).Id);
    }

    [Fact]
    public void Json_that_does_not_parse_keeps_its_tab_and_says_why()
    {
        var (editor, _) = Open(new TaskTemplate("digest", "Digest", "Write it."));
        editor.Tab = 1;
        editor.JsonText = "{ not json";

        editor.Tab = 0;

        Assert.True(editor.IsJson);
        Assert.StartsWith("That JSON could not be read", editor.Status);
    }

    [Fact]
    public void A_template_the_validator_refuses_is_not_saved_and_the_status_says_why()
    {
        var (editor, saved) = Open(new TaskTemplate("", "", ""));
        editor.Id = "../escape";

        editor.SaveCommand.Execute(null);

        Assert.Empty(saved);
        Assert.Contains("must be a slug", editor.Status);
    }
}
