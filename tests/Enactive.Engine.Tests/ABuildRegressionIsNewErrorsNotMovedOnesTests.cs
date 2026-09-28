namespace Enactive.Engine.Tests;

using System.Diagnostics;
using Enactive.Core.Builds;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// "The build did not regress" means the build reports no error it did not report before - not that
/// the build is green, and not that no error moved.
///
/// <para>The plan's aim for a workspace that is already red is "no regression" rather than "completely
/// green": an existing broken build must not block every step after it, and a step must still not be
/// allowed to add a new error. That needs diagnostics compared by what they ARE, not where they are:
/// an edit shifts every line below it, and an error that moved is the one that was already there.</para>
///
/// <para>The output below is verbatim what <c>dotnet build</c> printed on this machine on 2026-09-28,
/// not a description of the format. Two things in it a description would have missed: each diagnostic
/// is printed twice, while building and again in the summary; and a project with two target frameworks
/// prints each one per framework, with a suffix that differs between the copies.</para>
/// </summary>
public sealed class ABuildRegressionIsNewErrorsNotMovedOnesTests
{
    private const string Root = @"C:\Users\user\AppData\Local\Temp\claude\diagprobe";
    private static readonly DotnetEcosystem Dotnet = new();

    private const string MultiTargetOutput = """
        C:\Users\user\AppData\Local\Temp\claude\diagprobe\Probe\Program.cs(2,19): error CS0103: The name 'missingName' does not exist in the current context [C:\Users\user\AppData\Local\Temp\claude\diagprobe\Probe\Probe.csproj::TargetFramework=net8.0]
        C:\Users\user\AppData\Local\Temp\claude\diagprobe\Probe\Program.cs(2,19): error CS0103: The name 'missingName' does not exist in the current context [C:\Users\user\AppData\Local\Temp\claude\diagprobe\Probe\Probe.csproj::TargetFramework=net10.0]
        C:\Users\user\AppData\Local\Temp\claude\diagprobe\Probe\Program.cs(2,19): error CS0103: The name 'missingName' does not exist in the current context [C:\Users\user\AppData\Local\Temp\claude\diagprobe\Probe\Probe.csproj::TargetFramework=net8.0]
        C:\Users\user\AppData\Local\Temp\claude\diagprobe\Probe\Program.cs(2,19): error CS0103: The name 'missingName' does not exist in the current context [C:\Users\user\AppData\Local\Temp\claude\diagprobe\Probe\Probe.csproj::TargetFramework=net10.0]
        """;

    private const string SingleTargetOutput = """
        C:\Users\user\AppData\Local\Temp\claude\diagprobe\Probe\Program.cs(2,19): error CS0103: The name 'missingName' does not exist in the current context [C:\Users\user\AppData\Local\Temp\claude\diagprobe\Probe\Probe.csproj]
        C:\Users\user\AppData\Local\Temp\claude\diagprobe\Probe\Program.cs(3,12): warning CS8600: Converting null literal or possible null value to non-nullable type. [C:\Users\user\AppData\Local\Temp\claude\diagprobe\Probe\Probe.csproj]
        C:\Users\user\AppData\Local\Temp\claude\diagprobe\Probe\Program.cs(1,5): warning CS0219: The variable 'unused' is assigned but its value is never used [C:\Users\user\AppData\Local\Temp\claude\diagprobe\Probe\Probe.csproj]
        C:\Users\user\AppData\Local\Temp\claude\diagprobe\Probe\Program.cs(3,8): warning CS0219: The variable 's' is assigned but its value is never used [C:\Users\user\AppData\Local\Temp\claude\diagprobe\Probe\Probe.csproj]
        C:\Users\user\AppData\Local\Temp\claude\diagprobe\Probe\Program.cs(3,12): warning CS8600: Converting null literal or possible null value to non-nullable type. [C:\Users\user\AppData\Local\Temp\claude\diagprobe\Probe\Probe.csproj]
        C:\Users\user\AppData\Local\Temp\claude\diagprobe\Probe\Program.cs(1,5): warning CS0219: The variable 'unused' is assigned but its value is never used [C:\Users\user\AppData\Local\Temp\claude\diagprobe\Probe\Probe.csproj]
        C:\Users\user\AppData\Local\Temp\claude\diagprobe\Probe\Program.cs(3,8): warning CS0219: The variable 's' is assigned but its value is never used [C:\Users\user\AppData\Local\Temp\claude\diagprobe\Probe\Probe.csproj]
        C:\Users\user\AppData\Local\Temp\claude\diagprobe\Probe\Program.cs(2,19): error CS0103: The name 'missingName' does not exist in the current context [C:\Users\user\AppData\Local\Temp\claude\diagprobe\Probe\Probe.csproj]
          Build FAILED.
        """;

    // ── reading real output ───────────────────────────────────────────────────────────────

    [Fact]
    public void One_error_printed_four_times_across_two_frameworks_is_one_error()
    {
        var parsed = Dotnet.ParseDiagnostics(MultiTargetOutput, Root);

        Assert.Equal(4, parsed.Count);                     // what was printed
        var set = DiagnosticSet.Of(parsed);
        Assert.Equal(1, set.Total(DiagnosticSeverity.Error)); // what is actually wrong

        var only = parsed[0];
        Assert.Equal("Probe/Program.cs", only.Path);
        Assert.Equal("CS0103", only.Code);
        Assert.Equal("The name 'missingName' does not exist in the current context", only.Message);
        Assert.Equal((2, 19), (only.Line!.Value, only.Column!.Value));
    }

    [Fact]
    public void Each_diagnostic_printed_again_in_the_summary_is_counted_once()
    {
        var set = DiagnosticSet.Of(Dotnet.ParseDiagnostics(SingleTargetOutput, Root));

        Assert.Equal(1, set.Total(DiagnosticSeverity.Error));
        // Three warnings, two of them CS0219 about different variables - different messages, so two.
        Assert.Equal(3, set.Total(DiagnosticSeverity.Warning));
    }

    [Fact]
    public void A_build_level_error_with_no_file_is_read_too()
    {
        var parsed = Dotnet.ParseDiagnostics("MSBUILD : error MSB1009: Project file does not exist.", Root);

        var only = Assert.Single(parsed);
        Assert.Null(only.Path);
        Assert.Equal("MSB1009", only.Code);
        Assert.Equal(DiagnosticSeverity.Error, only.Severity);
        Assert.Null(only.Line);
    }

    [Fact]
    public void Lines_that_are_not_diagnostics_are_left_alone()
        => Assert.Empty(Dotnet.ParseDiagnostics("  Build FAILED.\n    0 Warning(s)\n  Restore complete (0,4s)\n", Root));

    // ── what counts as a regression ───────────────────────────────────────────────────────

    private static BuildDiagnostic Error(string message, int line, string path = "A.cs", string code = "CS0103")
        => new("dotnet", path, code, DiagnosticSeverity.Error, message, line, 1);

    [Fact]
    public void An_error_that_only_moved_is_not_a_regression()
    {
        var before = DiagnosticSet.Of([Error("x missing", 10)]);
        var after = DiagnosticSet.Of([Error("x missing", 13)]);

        Assert.Empty(after.NewSince(before));
    }

    /// <summary>The reason identity is counted rather than set: the same error, once more, IS new.</summary>
    [Fact]
    public void A_second_occurrence_of_an_error_already_there_is_a_regression()
    {
        var before = DiagnosticSet.Of([Error("x missing", 10)]);
        var after = DiagnosticSet.Of([Error("x missing", 13), Error("x missing", 40)]);

        var regression = Assert.Single(after.NewSince(before));
        Assert.Equal(1, regression.Added);
    }

    [Fact]
    public void Fixing_an_error_is_not_a_regression_and_a_warning_turned_error_is()
    {
        var before = DiagnosticSet.Of([
            Error("fixed later", 5),
            new BuildDiagnostic("dotnet", "A.cs", "CS0219", DiagnosticSeverity.Warning, "unused 'v'", 7, 1)]);
        var after = DiagnosticSet.Of([
            new BuildDiagnostic("dotnet", "A.cs", "CS0219", DiagnosticSeverity.Error, "unused 'v'", 7, 1)]);

        var regression = Assert.Single(after.NewSince(before));
        Assert.Equal("CS0219", regression.Identity.Code);
    }

    // ── the live compiler ─────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE ONE THAT MATTERS, against the real compiler rather than recorded text. A project that is
    /// already red is built for a baseline. Then lines are inserted ABOVE its existing error, moving
    /// it, and one new error is added. Rebuilt, exactly one regression is reported - the new one - and
    /// the error that moved is recognised as the one that was there.
    /// </summary>
    [Fact]
    public void Against_the_real_compiler_only_the_error_a_change_added_is_a_regression()
    {
        var root = Directory.CreateTempSubdirectory("dotnet-regression").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "Probe.csproj"), """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework>
                  <ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable></PropertyGroup>
                </Project>
                """);
            var program = Path.Combine(root, "Program.cs");
            File.WriteAllText(program, "Console.WriteLine(alreadyBroken);\n");

            var targets = Assert.IsType<EcosystemTargets>(Dotnet.Detect(root));
            var target = Assert.Single(targets.Build);
            var baseline = DiagnosticSet.Of(Dotnet.ParseDiagnostics(Build(root, target), root));
            Assert.Equal(1, baseline.Total(DiagnosticSeverity.Error));

            File.WriteAllText(program,
                "// a comment\n// and another\n// pushing the old error down\n"
                + "Console.WriteLine(alreadyBroken);\nConsole.WriteLine(newlyBroken);\n");
            var after = DiagnosticSet.Of(Dotnet.ParseDiagnostics(Build(root, target), root));

            var regression = Assert.Single(after.NewSince(baseline));
            Assert.Contains("newlyBroken", regression.Identity.Message, StringComparison.Ordinal);
            Assert.Equal("Program.cs", regression.Identity.Path);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string Build(string root, string target)
    {
        var start = new ProcessStartInfo("dotnet", $"build \"{target}\" -nologo")
        {
            WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false
        };
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit(180_000);
        return output.Result + "\n" + error.Result;
    }

    // ── finding what to build ─────────────────────────────────────────────────────────────

    [Fact]
    public void A_solution_at_the_root_is_the_target_and_test_projects_are_found_beneath_it()
    {
        var root = Directory.CreateTempSubdirectory("dotnet-detect").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "App.sln"), "");
            Directory.CreateDirectory(Path.Combine(root, "App"));
            File.WriteAllText(Path.Combine(root, "App", "App.csproj"), "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Directory.CreateDirectory(Path.Combine(root, "App.Tests"));
            File.WriteAllText(Path.Combine(root, "App.Tests", "App.Tests.csproj"),
                "<Project><ItemGroup><PackageReference Include=\"Microsoft.NET.Test.Sdk\" Version=\"17\" /></ItemGroup></Project>");
            // Build output is never a project the workspace owns.
            Directory.CreateDirectory(Path.Combine(root, "App", "obj"));
            File.WriteAllText(Path.Combine(root, "App", "obj", "Stray.csproj"), "<Project />");

            var targets = Assert.IsType<EcosystemTargets>(Dotnet.Detect(root));
            Assert.Equal(["App.sln"], targets.Build);
            Assert.Equal(["App.Tests/App.Tests.csproj"], targets.Tests);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void A_folder_with_no_dotnet_project_is_not_dotnet()
    {
        var root = Directory.CreateTempSubdirectory("dotnet-none").FullName;
        try
        {
            File.WriteAllText(Path.Combine(root, "README.md"), "# docs");
            Assert.Null(Dotnet.Detect(root));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("src/App/Program.cs", true)]
    [InlineData("App.csproj", true)]
    [InlineData("Directory.Build.props", true)]
    [InlineData("global.json", true)]
    [InlineData("Views/Main.axaml", true)]
    [InlineData("Docs/README.md", false)]
    [InlineData("notes.txt", false)]
    public void A_change_to_documentation_is_no_reason_to_build(string path, bool owned)
        => Assert.Equal(owned, Dotnet.Owns(path));
}
