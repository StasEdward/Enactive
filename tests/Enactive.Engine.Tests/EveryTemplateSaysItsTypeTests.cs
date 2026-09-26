namespace Enactive.Engine.Tests;

using System.Text.RegularExpressions;
using Xunit;

/// <summary>
/// Every <c>DataTemplate</c> in the UI declares <c>x:DataType</c>.
///
/// <para><b>Why this is a test and not a style rule.</b> The project builds with
/// <c>AvaloniaUseCompiledBindingsByDefault</c> on, which is what turns a mistyped binding path into
/// a build error rather than an empty control. A <c>DataTemplate</c> with no declared type opts
/// OUT of that for everything inside it: the bindings fall back to reflection, resolved at runtime,
/// and a failure is a silent no-op.</para>
///
/// <para>Reported 2026-09-23: the "Who may send" ticks in Settings → SMTP did not stick. Every
/// other row in that window — providers, workers, MCP servers, templates, writable roots —
/// declares its type. That one template did not, and it is the only control in the application
/// whose whole job is to carry a value BACK.</para>
///
/// <para>A missing type costs nothing visible in a template that only displays. It costs the
/// feature in one that edits, and it never says so — which is why it is checked here instead of
/// being noticed the next time.</para>
/// </summary>
public sealed class EveryTemplateSaysItsTypeTests
{
    [Fact]
    public void Every_data_template_declares_the_type_it_binds_against()
    {
        var views = Path.Combine(RepositoryRoot(), "src", "Enactive.App.Ui");
        Assert.True(Directory.Exists(views), views + " is not there");

        var untyped = new List<string>();

        foreach (var file in Directory.EnumerateFiles(views, "*.axaml", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);

            // Each opening tag, up to the end of its attributes. A template that names its type
            // with x:DataType is compiled and checked; one that only has Avalonia's DataType is
            // matched by type but still binds by reflection, so both are required to say it.
            foreach (Match tag in Regex.Matches(text, @"<DataTemplate\b[^>]*>"))
                if (!tag.Value.Contains("x:DataType", StringComparison.Ordinal))
                    untyped.Add($"{Path.GetFileName(file)}: {Line(text, tag.Index)}");
        }

        Assert.True(untyped.Count == 0,
            "A DataTemplate without x:DataType opts its bindings out of compiled bindings, so a "
            + "wrong path is a silent no-op at runtime instead of a build error - and in a template "
            + "that EDITS, the value never travels back. Add x:DataType to: "
            + string.Join("; ", untyped));
    }

    private static int Line(string text, int index)
        => text.Take(index).Count(c => c == '\n') + 1;

    private static string RepositoryRoot() => TestRepository.Root;
}
