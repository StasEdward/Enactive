namespace Enactive.Core.Builds;

/// <summary>What an ecosystem found to build and to test in a workspace, as workspace-relative paths.</summary>
public sealed record EcosystemTargets(string Ecosystem, IReadOnlyList<string> Build, IReadOnlyList<string> Tests);

/// <summary>
/// One kind of project - .NET, Node, and so on - as the engine needs to know it: whether a workspace
/// is one, which of its files a change to can break the build, how to build and test it, and how to
/// read what its build reported.
///
/// <para>This is the boundary the plan draws: the orchestrator holds no .NET-specific logic. It asks
/// each ecosystem, and an ecosystem it has never heard of simply is not detected. Adding Node is a new
/// implementation of this, and nothing in the orchestrator.</para>
/// </summary>
public interface IEcosystem
{
    /// <summary>A short stable name, and the <see cref="BuildDiagnostic.Provider"/> of what it reports.</summary>
    string Name { get; }

    /// <summary>What there is to build and test here, or null when this workspace is not this ecosystem.</summary>
    EcosystemTargets? Detect(string workspaceRoot);

    /// <summary>
    /// Whether a change to this workspace-relative path can change what the build reports. A step
    /// that changed only documentation has no reason to trigger a build, and the plan says not to.
    /// </summary>
    bool Owns(string relativePath);

    string BuildCommand(string target);

    string TestCommand(string target);

    /// <summary>
    /// The command that runs only the tests of a target whose name contains <paramref name="filter"/> - or null when this
    /// ecosystem cannot run part of a target's tests (run_tests then says so).
    /// </summary>
    string? TestCommand(string target, string filter) => null;

    /// <summary>
    /// Whether a command runs this ecosystem's tests - the one a person gives as "the test command", in whatever form:
    /// its output is then read by <see cref="ParseTests"/>, and a run that names no test has verified nothing. Defaulted to
    /// no, so an ecosystem that cannot tell changes nothing.
    /// </summary>
    bool RunsTests(string command) => false;

    /// <summary>
    /// When a test command ran no test, what in the workspace explains it, in this ecosystem's terms - or null when it
    /// cannot say. Said beside "ran no tests", so a person is told what to change, not only that nothing ran.
    /// </summary>
    string? WhyNoTests(string command, string workspaceRoot) => null;

    /// <summary>
    /// What a run of this ecosystem's tests came to, for the model that ran it: the totals, and each failed test with what
    /// it said and where - or null when the output does not read as a test run. Given to the model in place of the
    /// output itself; the whole output is kept, and every check of the engine still reads that, not this.
    /// </summary>
    string? DescribeTests(string output) => null;

    /// <summary>What the build printed, as diagnostics with workspace-relative paths.</summary>
    IReadOnlyList<BuildDiagnostic> ParseDiagnostics(string output, string workspaceRoot);

    /// <summary>
    /// What a run of <see cref="TestCommand"/> printed, as the tests it named and its totals - or
    /// null when this ecosystem cannot read its own test output, and then its tests are not part of
    /// the baseline. Defaulted so an ecosystem that knows only its build still is one.
    /// </summary>
    TestRunReport? ParseTests(string output) => null;

    /// <summary>
    /// Whether a diagnostic is about the MACHINE rather than the code: a file another process holds, a
    /// path the build could not write. Such an error says nothing about what the work did, and is not
    /// counted as the work's regression. Defaulted to no, so an ecosystem that cannot tell counts all.
    /// </summary>
    bool IsEnvironmental(DiagnosticIdentity identity) => false;
}
