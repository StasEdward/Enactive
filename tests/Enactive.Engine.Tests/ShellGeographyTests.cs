namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Xunit;

/// <summary>
/// What a command line says about where it writes.
///
/// <para>Two halves, and the second is the one that decides whether this feature survives contact
/// with a real workspace. A check that catches every escape and also stops <c>dotnet build</c> is
/// not a working check - it is a question the person learns to click through without reading, and
/// after that it protects nothing at all. So the false-positive cases below are not a courtesy;
/// they are the requirement.</para>
///
/// <para>Every path here is under a temp root that really exists, because the rule resolves
/// relative paths against the workspace and follows what the filesystem says.</para>
/// </summary>
public sealed class ShellGeographyTests
{
    private static readonly string Root =
        Path.Combine(Path.GetTempPath(), "enactive-geo", "workspace");

    private static IReadOnlyList<OutsideWrite> Outside(string command, params string[] alsoWritable)
        => ShellGeography.WritesOutside(command, Root, alsoWritable);

    private static string Elsewhere(string tail)
        => Path.Combine(Path.GetTempPath(), "enactive-geo", "elsewhere", tail);

    /// <summary>
    /// The shapes a write to somewhere else actually takes. {0} is the outside path.
    ///
    /// <para>Each one is a real way of spelling it rather than a variation on one way: a
    /// redirection, a tool's own output flag, an MSBuild property, a copy destination, a delete.
    /// The MSBuild property is here because <c>-p:PublishDir=</c> writes exactly where <c>-o</c>
    /// does and looks nothing like it - a table built by staring at <c>-o</c> would miss it.</para>
    /// </summary>
    [Theory]
    [InlineData("echo hello > {0}")]
    [InlineData("echo hello>{0}")]
    [InlineData("dir /s /b >> {0}")]
    [InlineData("dotnet publish -o {0}")]
    [InlineData("dotnet publish -p:PublishDir={0}")]
    [InlineData("dotnet pack /p:PackageOutputPath={0}")]
    [InlineData("copy report.txt {0}")]
    [InlineData("del {0}")]
    [InlineData("Remove-Item -Path {0}")]
    [InlineData("git log | Out-File {0}")]
    public void A_write_that_lands_outside_is_named(string template)
    {
        var target = Elsewhere("out.txt");
        var found = Outside(string.Format(template, target));

        var one = Assert.Single(found);
        Assert.Equal(target, one.Path, ignoreCase: true);
        Assert.True(one.Known);
        Assert.False(string.IsNullOrWhiteSpace(one.Because));
    }

    /// <summary>
    /// A relative path can leave the workspace too, and this is the ONE case where the answer is
    /// exact rather than a guess: the path is resolved, so where it lands is known.
    /// </summary>
    [Fact]
    public void A_relative_path_that_climbs_out_is_resolved_and_named()
    {
        var found = Outside(@"echo hello > ..\..\escaped.txt");

        var one = Assert.Single(found);
        Assert.False(WorkspaceGuard.IsInside(Path.GetFullPath(Root), one.Path),
            $"'{one.Path}' was reported as outside the workspace but is inside it.");
    }

    /// <summary>
    /// A variable is reported, and reported as UNKNOWN.
    ///
    /// <para>Saying nothing would be the dishonest answer - the command plainly writes somewhere,
    /// and the one thing that can be said about it is that the command line does not say where.
    /// Claiming a resolved path would be worse: this process's %TEMP% is not necessarily the
    /// shell's.</para>
    /// </summary>
    [Fact]
    public void A_path_the_shell_expands_is_reported_as_unknown()
    {
        var found = Outside(@"echo hello > %TEMP%\out.txt");

        var one = Assert.Single(found);
        Assert.False(one.Known, "A path built from a variable was reported as though it were known.");
        Assert.Contains("%TEMP%", one.Token, StringComparison.Ordinal);
    }

    /// <summary>A root the user has already said yes to is not asked about again.</summary>
    [Fact]
    public void A_writable_root_the_user_granted_is_not_reported()
    {
        var target = Elsewhere("out.txt");

        Assert.Single(Outside($"echo hello > {target}"));
        Assert.Empty(Outside($"echo hello > {target}", Path.GetDirectoryName(target)!));
    }

    /// <summary>
    /// The half that decides whether this feature is usable.
    ///
    /// <para>Every line here is an ordinary day's work. If any of them raises a question, the
    /// person is answering questions about their own build, and the answer they learn is Allow -
    /// after which the check protects nothing and costs a click each time. Reads from outside are
    /// in this list on purpose: <c>copy C:\vendor\lib.dll .</c> brings a file IN, which is not the
    /// mistake being looked for.</para>
    /// </summary>
    [Theory]
    [InlineData("dotnet build")]
    [InlineData("dotnet build -c Release")]
    [InlineData("dotnet test tests/Some.Tests/Some.Tests.csproj")]
    [InlineData("dotnet publish -o publish")]
    [InlineData("git status")]
    [InlineData("git diff 2>&1")]
    [InlineData("echo hello > out.txt")]
    [InlineData(@"echo hello > .\logs\out.txt")]
    [InlineData("echo done >nul")]
    [InlineData("dir /s /b > files.txt")]
    [InlineData("npm run build -- --output-dir dist")]
    [InlineData("docker build -t enactive/app .")]
    public void Ordinary_work_inside_the_workspace_raises_nothing(string command)
    {
        var found = Outside(command);

        Assert.True(found.Count == 0,
            $"'{command}' was reported as writing outside the workspace, to "
            + $"'{(found.Count > 0 ? found[0].Path : "")}' because of "
            + $"{(found.Count > 0 ? found[0].Because : "")}. A check that asks about ordinary work "
            + "is one the person learns to click through.");
    }

    /// <summary>
    /// Reading from outside is not writing to outside, and the verb is what says which.
    ///
    /// <para>Separate from the list above because it is the rule most likely to be broken by a
    /// later, well-meant addition to the tables - somebody adds `-Path`, or adds `type` to the
    /// verbs, and every listing of a system folder becomes a question.</para>
    /// </summary>
    [Theory]
    [InlineData("type {0}")]
    [InlineData("Get-Content {0}")]
    [InlineData("Get-ChildItem -Path {0}")]
    [InlineData("copy {0} .")]
    [InlineData("robocopy {0} local /E")]
    public void Reading_from_outside_is_not_a_write(string template)
    {
        var found = Outside(string.Format(template, Elsewhere("input.txt")));

        Assert.True(found.Count == 0,
            $"A read from outside the workspace was reported as a write to "
            + $"'{(found.Count > 0 ? found[0].Path : "")}'.");
    }

    /// <summary>One path named twice is one question.</summary>
    [Fact]
    public void The_same_place_named_twice_is_reported_once()
    {
        var target = Elsewhere("out.txt");

        Assert.Single(Outside($"echo one > {target} && echo two >> {target}"));
    }
}
