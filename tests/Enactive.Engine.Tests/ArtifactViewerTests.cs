namespace Enactive.Engine.Tests;

using Enactive.Core.Text;
using Xunit;

/// <summary>
/// Which viewer shows a file, and where a viewer is allowed to come from.
///
/// <para>A run's deliverable is often one Markdown document, and every one of them arrived in the
/// viewer as monospaced source in a box that does not wrap — the same box as an environment probe,
/// while the renderer for it had been sitting in the log analysis window since it was written.
/// </para>
///
/// <para>Everything about this that can be WRONG is a matter of names, and none of it needs a
/// window: the same split <c>MarkdownRender</c> already makes against <c>Markdown</c>.</para>
/// </summary>
public sealed class ArtifactViewerTests
{
    [Theory]
    [InlineData("REPORT.md")]
    [InlineData("docs/REPORT.md")]
    [InlineData("REPORT.MD")]
    [InlineData("a.markdown")]
    public void Markdown_is_recognised_however_it_is_written(string path)
        => Assert.Equal(ViewerRegistry.Markdown, ViewerRegistry.Builtin().Choose(path));

    /// <summary>
    /// Plain text is a real answer, not a failure: most of what a run writes is text and reading
    /// it verbatim is correct.
    /// </summary>
    [Theory]
    [InlineData("Program.cs")]
    [InlineData("data.txt")]
    [InlineData("Makefile")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_unclaimed_is_shown_as_itself(string? path)
        => Assert.Equal(ViewerRegistry.PlainText, ViewerRegistry.Builtin().Choose(path));

    /// <summary>
    /// The extension is the FILE's. A folder called <c>docs.md</c> says nothing about what is
    /// inside it, and taking the last dot in the whole string would say it did.
    /// </summary>
    [Fact]
    public void A_folder_with_a_dot_is_not_the_file_s_extension()
        => Assert.Equal(ViewerRegistry.PlainText, ViewerRegistry.Builtin().Choose("docs.md/notes"));

    /// <summary>
    /// A plugin registers after the built-ins and therefore wins. First-wins would make the
    /// folder decoration: every interesting extension is claimed before it is read.
    /// </summary>
    [Fact]
    public void A_plugin_may_replace_a_built_in()
    {
        var registry = ViewerRegistry.Builtin();
        registry.Register("fancy-markdown", ".md");

        Assert.Equal("fancy-markdown", registry.Choose("REPORT.md"));

        // And only the extension it claimed: .markdown is still the built-in's.
        Assert.Equal(ViewerRegistry.Markdown, registry.Choose("REPORT.markdown"));
    }

    /// <summary>A leading dot is optional, because half the people writing one will leave it out.</summary>
    [Fact]
    public void An_extension_may_be_registered_with_or_without_its_dot()
    {
        var registry = new ViewerRegistry();
        registry.Register("csv", "csv", ".tsv");

        Assert.Equal("csv", registry.Choose("rows.csv"));
        Assert.Equal("csv", registry.Choose("rows.tsv"));
    }

    /// <summary>
    /// A plugin's typo must not take the window down with it. Nothing here throws; the extension
    /// is simply not claimed.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData(".")]
    public void A_malformed_registration_is_ignored_rather_than_fatal(string extension)
    {
        var registry = new ViewerRegistry();
        registry.Register("broken", extension);

        Assert.Empty(registry.Claimed);
        Assert.Equal(ViewerRegistry.PlainText, registry.Choose("anything.md"));
    }

    [Fact]
    public void A_viewer_with_no_id_claims_nothing()
    {
        var registry = new ViewerRegistry();
        registry.Register("  ", ".md");

        Assert.Empty(registry.Claimed);
    }

    // ── where a plugin may come from ────────────────────────────────────────

    /// <summary>
    /// THE SECURITY OF THE WHOLE FEATURE, and it is a location rather than a check.
    ///
    /// <para>A viewer plugin is code in this process — the process holding the provider API keys,
    /// able to write wherever the person has allowed. An agent writes into a workspace by design
    /// and all day long. A plugin folder inside a workspace would therefore be a way for a model
    /// to give itself arbitrary code execution in its own supervisor by writing a file, which is
    /// the one boundary <c>WorkspaceGuard</c> exists to hold.</para>
    ///
    /// <para>So the folder is fixed beside the app's own settings and takes a path from nowhere.
    /// Putting a DLL there is a deliberate act by the person at the keyboard.</para>
    /// </summary>
    [Fact]
    public void Plugins_live_beside_the_app_settings_and_never_in_a_workspace()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);

        Assert.StartsWith(appData, ViewerPlugins.Folder, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(ViewerPlugins.FolderName, ViewerPlugins.Folder, StringComparison.Ordinal);
    }

    /// <summary>
    /// And it is not reachable from a workspace, which is the claim that actually matters. Tested
    /// against a workspace the fixture really made, with the guard the engine really uses.
    /// </summary>
    [Fact]
    public void No_workspace_can_reach_the_plugin_folder()
    {
        using var fx = new EngineFixture();

        Assert.False(
            Enactive.Core.Context.WorkspaceGuard.IsInside(
                Path.GetFullPath(fx.Root), Path.GetFullPath(ViewerPlugins.Folder)),
            "a run could write a viewer plugin");
    }
}
