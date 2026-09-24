namespace Enactive.Engine.Tests;

using System.Text;
using Enactive.Tools;
using Xunit;

/// <summary>
/// A file's own non-ASCII characters, read back through a shell tool rather than through
/// <c>read_file</c>.
///
/// <para><b>From a real run, 2026-09-25.</b> A report the worker itself had written (plain UTF-8,
/// containing em dashes) came back through a shell tool with every em dash replaced by mojibake
/// ("ƒ?”" in place of "—"). The model, reading its OWN file report back as garbage, concluded the
/// file was "corrupted by a previous partial write" and spent the rest of the step trying to fix
/// content that was never broken: three failed <c>edit_file</c> calls built from the mangled text
/// (which therefore never matched the real, correct bytes on disk), an unresolved-tool-call step
/// failure, and the whole task marked incomplete. <c>read_file</c> on the same path, moments
/// earlier, showed the correct text.</para>
///
/// <para><b>Two separate bugs, not one.</b> <see cref="RunCommandTool"/> never told .NET what
/// encoding <c>cmd.exe</c>'s redirected output is in, so it fell back to the console's default -
/// not UTF-8 - and neither did the console codepage <c>cmd.exe</c> itself passed to whatever it ran.
/// <see cref="RunPowerShellTool"/> is a second, independent mismatch even after that: Windows
/// PowerShell's own file-reading cmdlets (<c>Get-Content</c> among them) default to the system ANSI
/// code page for a file with no byte-order mark, regardless of the console's codepage, so the script
/// misreads the file before it ever writes anything back out.</para>
///
/// <para><b>Not fixed here, and not fixable this way:</b> a model-authored <c>command</c> string
/// that itself shells out to a NESTED <c>powershell -Command …</c> from inside <c>run_command</c>
/// carries none of <see cref="RunPowerShellTool"/>'s preamble - there is no arbitrary shell text this
/// tool could safely rewrite to inject it. This is exactly what the system prompt's "prefer
/// run_powershell for anything using PowerShell" is for.</para>
/// </summary>
public sealed class ShellOutputEncodingTests
{
    private const string Marker = "before—after";

    [Fact]
    public async Task A_command_that_types_a_utf8_file_gets_its_real_characters_back()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var fx = new EngineFixture();
        var path = Path.Combine(fx.Root, "marker.txt");
        await File.WriteAllTextAsync(path, Marker, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var result = await new RunCommandTool().InvokeAsync(
            $$"""{"command": "type marker.txt"}""", fx.ContextFor(), CancellationToken.None);

        Assert.True(result.Success, "the command failed: " + result.Error);
        Assert.Contains(Marker, result.Output ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_powershell_script_that_reads_a_utf8_file_gets_its_real_characters_back()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var fx = new EngineFixture();
        var path = Path.Combine(fx.Root, "marker.txt");
        await File.WriteAllTextAsync(path, Marker, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var result = await new RunPowerShellTool().InvokeAsync(
            $$"""{"script": "Get-Content marker.txt -Raw"}""", fx.ContextFor(), CancellationToken.None);

        Assert.True(result.Success, "the script failed: " + result.Error);
        Assert.Contains(Marker, result.Output ?? "", StringComparison.Ordinal);
    }
}
