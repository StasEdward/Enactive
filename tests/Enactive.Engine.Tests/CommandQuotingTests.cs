namespace Enactive.Engine.Tests;

using System.Text.Json;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// A command with quotes in it, arriving at cmd.exe as the caller wrote it.
///
/// <para><b>From a real run, 2026-09-10.</b> The agent found Total Commander and ran
/// <c>start "" "C:\Program Files\totalcmd\TOTALCMD64.EXE"</c> - which is exactly how that is
/// written. cmd received something else, tried to launch it, and Windows put a MODAL DIALOG on the
/// developer's screen saying it cannot find <c>\\</c>. The run sat for 24 seconds until somebody
/// clicked OK, then reported "Access is denied" and exit 1.</para>
///
/// <para><b>Why.</b> The command was passed as one element of <c>ArgumentList</c>. .NET joins that
/// list using the C runtime's rules - wrap in quotes, escape inner quotes with backslashes - and
/// <c>cmd.exe</c> does not use the C runtime's rules. It has its own, and under them a
/// backslash-quote is not an escaped quote. So a command containing a quote was rewritten on the
/// way in.</para>
///
/// <para>The system prompt already told the model "do NOT fight cmd quoting with run_command" -
/// advice that reads as a limitation of shells and was really a bug in this tool.</para>
///
/// <para>Windows only: this is about cmd.exe.</para>
/// </summary>
public sealed class CommandQuotingTests
{
    private static async Task<ToolResult> Run(EngineFixture fx, string command)
        => await new RunCommandTool().InvokeAsync(
            $$"""{"command": {{JsonSerializer.Serialize(command)}} }""",
            fx.ContextFor(),
            CancellationToken.None);

    /// <summary>
    /// The decisive one, and deliberately an ECHO rather than a launch: the failing command started
    /// a program, and a test that starts programs on somebody's machine to prove a point about
    /// quoting is a bad trade. The shape is the same - an empty quoted token, then a quoted path
    /// with a space - and it is the shape that broke.
    /// </summary>
    [WindowsFact]
    public async Task A_command_with_quotes_reaches_cmd_as_it_was_written()
    {

        using var fx = new EngineFixture();

        var result = await Run(fx, """echo "" "C:\Program Files\totalcmd\TOTALCMD64.EXE" """.TrimEnd());

        Assert.True(result.Success, "A command containing quotes failed: " + result.Error);

        // The EXACT line, not a substring of it. A first version of this test asserted only that
        // the path was in there - and the path is still in there when every quote around it has
        // been turned into \", which is precisely the corruption being tested for. Green for the
        // wrong reason, again.
        var expected = "\"\" \"C:\\Program Files\\totalcmd\\TOTALCMD64.EXE\"";

        // The message carries what actually came back. A bare Contains prints a truncated slice of
        // a string with escapes in it, which is unreadable exactly when it matters.
        Assert.True(
            (result.Output ?? "").Contains(expected, StringComparison.Ordinal),
            $"cmd did not get the command as written.\nExpected to contain:\n{expected}\nGot:\n{result.Output}");
    }

    /// <summary>
    /// A quoted argument with a space survives as ONE argument, which is the whole reason anybody
    /// writes the quotes.
    /// </summary>
    [WindowsFact]
    public async Task A_quoted_argument_keeps_its_quotes()
    {

        using var fx = new EngineFixture();

        var result = await Run(fx, """echo "hello there" """.TrimEnd());

        Assert.True(result.Success, "A quoted argument failed: " + result.Error);
        Assert.Contains("hello there", result.Output ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// And the ordinary case still works. A fix to quoting that broke every command without quotes
    /// would be a worse bug than the one it replaced.
    /// </summary>
    [WindowsFact]
    public async Task A_command_with_no_quotes_still_works()
    {

        using var fx = new EngineFixture();

        var result = await Run(fx, "echo plain-text-marker");

        Assert.True(result.Success, "A plain command failed: " + result.Error);
        Assert.Contains("plain-text-marker", result.Output ?? "", StringComparison.Ordinal);
    }

    /// <summary>
    /// Redirection, pipes and &amp; are cmd's own syntax and must keep working - they are most of why
    /// somebody reaches for run_command instead of a file tool.
    /// </summary>
    [WindowsFact]
    public async Task Cmd_syntax_still_works()
    {

        using var fx = new EngineFixture();

        var result = await Run(fx, "echo one & echo two");

        Assert.True(result.Success, "A command using cmd syntax failed: " + result.Error);
        Assert.Contains("one", result.Output ?? "", StringComparison.Ordinal);
        Assert.Contains("two", result.Output ?? "", StringComparison.Ordinal);
    }
}
