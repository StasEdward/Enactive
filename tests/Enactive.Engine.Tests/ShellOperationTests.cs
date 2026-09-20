namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Xunit;

/// <summary>
/// What a command line DOES, as distinct from how it was spelled.
///
/// <para>A failed tool call is held open until it is made good, and for a shell command the only
/// way was to run the byte-identical string again. Measured 2026-09-20: a run wrote 63 passing
/// tests, found a real defect in the code under test, and was reported <c>Incomplete</c> — its
/// first <c>dotnet test … 2>&amp;1</c> failed for a missing package, it added the package, and it
/// verified with <c>dotnet test … --nologo 2>&amp;1 | …</c>. Cause fixed, result proved,
/// different string, failure left open.</para>
/// </summary>
public sealed class ShellOperationTests
{
    /// <summary>The reported case, to the character.</summary>
    [Fact]
    public void The_same_test_run_is_the_same_operation_however_it_is_dressed()
    {
        var failed = ShellOperation.Of(
            "dotnet test TicTacToe/TicTacToe.Tests/TicTacToe.Tests.csproj 2>&1");
        var verified = ShellOperation.Of(
            "dotnet test TicTacToe/TicTacToe.Tests/TicTacToe.Tests.csproj --nologo 2>&1 | findstr /C:\"Passed\"");

        Assert.Equal(failed, verified);
        Assert.Equal("dotnet test TicTacToe/TicTacToe.Tests/TicTacToe.Tests.csproj", failed);
    }

    /// <summary>
    /// The guard against it. Two different targets are two different things, and a success
    /// against one must never clear a failure against the other — that is the whole risk of
    /// loosening this, so it is the test that matters most.
    /// </summary>
    [Theory]
    [InlineData("dotnet build ProjectA.csproj", "dotnet build ProjectB.csproj")]
    [InlineData("dotnet test A/A.csproj", "dotnet build A/A.csproj")]
    [InlineData("dotnet test A/A.csproj", "npm test A/A.csproj")]
    [InlineData("git push", "git status")]
    public void Different_things_stay_different(string one, string other)
    {
        Assert.NotEqual(ShellOperation.Of(one), ShellOperation.Of(other));
    }

    /// <summary>Separators are a spelling: a model writes a/b in one call and a\b in the next.</summary>
    [Fact]
    public void A_path_written_either_way_is_the_same_operation()
    {
        Assert.Equal(
            ShellOperation.Of(@"dotnet build src\Thing\Thing.csproj"),
            ShellOperation.Of("dotnet build src/Thing/Thing.csproj"));
    }

    [Theory]
    [InlineData("dotnet test X --nologo", "dotnet test X")]
    [InlineData("dotnet test X > out.txt", "dotnet test X")]
    [InlineData("dotnet test X && echo done", "dotnet test X")]
    [InlineData("dotnet test X; echo done", "dotnet test X")]
    [InlineData("rmdir /s /q obj", "rmdir obj")]
    public void Flags_redirection_and_what_follows_a_pipe_are_not_the_operation(
        string dressed, string plain)
    {
        Assert.Equal(ShellOperation.Of(plain), ShellOperation.Of(dressed));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("| findstr x")]
    public void Nothing_to_name_is_null(string? command)
        => Assert.Null(ShellOperation.Of(command));

    /// <summary>Only the shells. A write is made good by writing the FILE, which is its own rule.</summary>
    [Fact]
    public void Only_a_shell_call_has_an_operation()
    {
        Assert.Null(ShellOperation.For("write_file", """{"path":"a.txt","content":"x"}"""));
        Assert.NotNull(ShellOperation.For("run_command", """{"command":"dotnet test X"}"""));
        Assert.NotNull(ShellOperation.For("run_powershell", """{"script":"dotnet test X"}"""));
    }

    /// <summary>
    /// A command and a script that say the same thing are the same operation. The agent switches
    /// between the two tools freely - it is told to prefer run_powershell for pipes - and a fix
    /// verified through the other tool is still the fix.
    /// </summary>
    [Fact]
    public void The_two_shell_tools_name_operations_the_same_way()
    {
        Assert.Equal(
            ShellOperation.For("run_command", """{"command":"dotnet build A.csproj 2>&1"}"""),
            ShellOperation.For("run_powershell", """{"script":"dotnet build A.csproj -v minimal"}"""));
    }

    [Fact]
    public void Unreadable_arguments_name_no_operation()
        => Assert.Null(ShellOperation.For("run_command", "not json"));
}
