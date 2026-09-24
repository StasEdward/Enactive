namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Xunit;

/// <summary>
/// The text a command writes is not the place it writes it.
///
/// <para><b>Measured 2026-09-24, run dc9133f8</b> - the first run with the worker on a hosted
/// model, and the one that was supposed to be quicker. Four times in it the person was asked to
/// let a command "write outside the workspace":</para>
///
/// <code>
/// 13:30:50  run_powershell appears to write outside the workspace: $content   (answered 13:36:48)
/// 13:44:02  run_powershell appears to write outside the workspace: $append    (answered 13:44:11)
/// 13:46:40  run_powershell appears to write outside the workspace: $append    (answered 13:48:20)
/// 13:50:03  run_powershell appears to write outside the workspace: $content   (answered 13:50:07)
/// </code>
///
/// <para>Almost eight minutes of a twenty-minute run spent waiting for a click, over commands
/// that wrote <c>Docs\DRIFT_ollama.md</c>. The script was the ordinary PowerShell way to write a
/// report - <c>$append = @' …Markdown… '@</c> and then
/// <c>Add-Content -Path "Docs\DRIFT_ollama.md" -Value $append</c> - and two things read it wrong:
/// the VALUE of <c>-Value</c> was walked as a positional argument of a verb whose every argument is
/// a path, and the here-string's body, forty lines of Markdown tables and arrows, was tokenized as
/// if it were commands.</para>
/// </summary>
public sealed class WhatIsWrittenIsNotWhereTests
{
    private const string Root = @"C:\ws";

    /// <summary>THE ONE THAT MATTERS: the script as it was sent.</summary>
    [Fact]
    public void A_report_appended_from_a_here_string_is_not_a_write_outside()
    {
        var script = "\n$append = @'\n\n---\n\n## Running-Tasks.md\n\n"
                   + "The implementation references `[Approval persistence](../src/Enactive.App.Ui/ApprovalStore.cs)`.\n\n"
                   + "| DRIFT-10 | Settings.md | \"schema version is 5\" | -> `CurrentSchemaVersion = 6` |\n"
                   + "| Move | ../../elsewhere | > C:\\Windows\\x.txt |\n"
                   + "'@\n\nAdd-Content -Path \"Docs\\DRIFT_ollama.md\" -Value $append\nWrite-Host \"Appended.\"\n";

        Assert.Empty(ShellGeography.WritesOutside(script, Root));
    }

    [Fact]
    public void The_value_of_Set_Content_is_not_a_place()
        => Assert.Empty(ShellGeography.WritesOutside(@"Set-Content -Path Docs\report.md -Value $content", Root));

    /// <summary>The same thing written without the parameter names.</summary>
    [Fact]
    public void The_second_positional_of_Set_Content_is_what_is_written()
        => Assert.Empty(ShellGeography.WritesOutside(@"Set-Content Docs\report.md $lines", Root));

    /// <summary>The form in the screenshot that asked the question: an array, then Set-Content.</summary>
    [Fact]
    public void An_array_of_lines_then_Set_Content_is_not_a_write_outside()
        => Assert.Empty(ShellGeography.WritesOutside(
            "$lines = @(\n'# DRIFT -- Wiki vs. Code',\n'',\n'Checked from scratch'\n)\n"
            + "Set-Content -Path 'Docs\\DRIFT_ollama.md' -Value $lines -Encoding utf8", Root));

    // ── the boundaries: none of this may stop asking ────────────────────────

    /// <summary>A real destination outside is still a destination outside.</summary>
    [Fact]
    public void Writing_to_a_real_outside_path_still_asks()
    {
        var writes = ShellGeography.WritesOutside(@"Set-Content -Path C:\Windows\x.txt -Value $v", Root);

        Assert.Single(writes);
    }

    /// <summary>A DESTINATION that is a variable is still unknown, and still asked about.</summary>
    [Fact]
    public void A_destination_that_is_a_variable_still_asks()
    {
        var writes = ShellGeography.WritesOutside("Set-Content $target -Value 'x'", Root);

        var write = Assert.Single(writes);
        Assert.False(write.Known);
    }

    /// <summary>
    /// A here-string's variable is left unresolved on purpose: used as the PATH, it is unknown, and
    /// the person is asked - rather than it resolving to some harmless-looking word.
    /// </summary>
    [Fact]
    public void A_here_string_used_as_the_destination_still_asks()
    {
        var script = "$where = @'\nC:\\Windows\\x.txt\n'@\nSet-Content -Path $where -Value 'x'";

        Assert.NotEmpty(ShellGeography.WritesOutside(script, Root));
    }

    /// <summary>And a redirection outside a here-string is still a redirection.</summary>
    [Fact]
    public void A_redirection_after_a_here_string_is_still_read()
    {
        var script = "$t = @'\nhello\n'@\n$t > C:\\Windows\\out.txt";

        Assert.NotEmpty(ShellGeography.WritesOutside(script, Root));
    }
}
