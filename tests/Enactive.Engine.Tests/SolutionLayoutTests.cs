namespace Enactive.Engine.Tests;

using Xunit;

/// <summary>
/// The solution has to list every project the build needs.
///
/// <para>A project reached only through a ProjectReference still builds — but a SOLUTION build does
/// not map its configuration onto it, so it builds Debug while everything around it builds Release.
/// `Enactive.Mcp.TestServer` was outside the solution for exactly that reason, and a clean
/// `dotnet build Enactive.sln` in Release put it in bin/Debug while this project's copy step looked
/// in bin/Release: MSB3030, on every push, since build.yml builds the solution in Release. Nobody
/// saw it locally because a working folder already had the Release artefacts from an earlier
/// build.</para>
///
/// <para>The copy step no longer assembles that path by hand, so the failure cannot come back in the
/// same shape. This is the other half: a project that quietly leaves the solution is what made it
/// possible at all, and the check costs nothing.</para>
/// </summary>
public sealed class SolutionLayoutTests
{
    [Fact]
    public void Every_project_is_listed_in_the_solution()
    {
        var root = RepositoryRoot();
        var solution = File.ReadAllText(Path.Combine(root, "Enactive.sln"));

        // Only src/ and tests/ — those are what the solution is for, and a project that lands there
        // is meant to build with everything else. Anything else at the top level is work in progress
        // or scratch (work/ holds probes and throwaway review harnesses) and joins the solution when
        // its author says so, not because a test found a file.
        var projects = new[] { "src", "tests" }
            .Select(folder => Path.Combine(root, folder))
            .Where(Directory.Exists)
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.csproj", SearchOption.AllDirectories))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.NotEmpty(projects);

        var missing = projects.Where(name => !solution.Contains(name!, StringComparison.OrdinalIgnoreCase)).ToArray();

        Assert.True(missing.Length == 0,
            "These projects are not in Enactive.sln, so a solution build will not apply its "
            + "configuration to them: " + string.Join(", ", missing));
    }

    /// <summary>Uses the checkout recorded when the tests were built.</summary>
    private static string RepositoryRoot() => TestRepository.Root;
}
