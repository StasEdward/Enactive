namespace Enactive.Engine.Tests;

using Enactive.Core.Permissions;
using Xunit;

/// <summary>
/// Two parts of the sandbox work, at the place they meet: a command that appears to write outside the
/// workspace becomes a QUESTION, and the answer decides.
///
/// <para>Every assertion here is on an EFFECT - the file is there, or it is not. Asserting that a
/// DecisionRequest was raised would pass just as well over a gate that asked and then ran the
/// command regardless, which is the failure mode worth fearing: an approval that is collected and
/// not obeyed is worse than no approval, because the timeline says a person agreed.</para>
///
/// <para>The permission gate is left permissive on purpose, so the only question in these runs is
/// the geographic one. Both gates firing would still work - they are asked in order - but a test
/// that answers two questions with one string is not saying which one it tested.</para>
/// </summary>
[Collection("processes")]
public sealed class GeographyGateTests : IDisposable
{
    private readonly string _elsewhere =
        Path.Combine(Path.GetTempPath(), "enactive-geo-gate", Guid.NewGuid().ToString("N"));

    private string Target => Path.Combine(_elsewhere, "out.txt");

    public GeographyGateTests() => Directory.CreateDirectory(_elsewhere);

    public void Dispose()
    {
        try { Directory.Delete(_elsewhere, recursive: true); } catch { /* a temp folder */ }
    }

    private static PermissionPolicy Permissive =>
        new(PermissionLevel.Execute, new[] { "*" }, Array.Empty<string>());

    private static string Writing(string path)
        => System.Text.Json.JsonSerializer.Serialize(new { command = $"echo hello> \"{path}\"" });

    private static IReadOnlyList<DecisionRequest> Geographic(EngineFixture fx)
        => fx.Decisions.Requests
            .Where(r => r.Topic.Contains("outside the workspace", StringComparison.Ordinal))
            .ToArray();

    /// <summary>
    /// The whole feature in one test: the command is asked about, the person says no, and the file
    /// is not there.
    /// </summary>
    [Fact]
    public async Task A_write_outside_the_workspace_is_asked_about_and_a_no_stops_it()
    {
        using var fx = new EngineFixture();
        fx.Decisions.Answer = "deny";

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"Write it","steps":[]}"""),
            Turn.Calls1("run_command", Writing(Target)),
            Turn.Says("Not written."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer"), policy: Permissive), "write the file");

        var asked = Assert.Single(Geographic(fx));

        // The path is IN the question. A card saying only "this writes outside the workspace" asks
        // somebody to approve a place they were not shown - the same defect FullDetail exists for.
        Assert.Contains(Target, asked.FullText, StringComparison.OrdinalIgnoreCase);

        Assert.False(File.Exists(Target),
            "The person kept the command inside the workspace and it wrote outside it anyway.");
    }

    /// <summary>
    /// And a yes lets it through. Without this the previous test passes on a gate that refuses
    /// everything, which is a working feature only in the sense that a disconnected wire is safe.
    /// </summary>
    [Fact]
    public async Task A_yes_lets_the_write_happen()
    {
        using var fx = new EngineFixture();
        fx.Decisions.Answer = "once";

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"Write it","steps":[]}"""),
            Turn.Calls1("run_command", Writing(Target)),
            Turn.Says("Written."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer"), policy: Permissive), "write the file");

        Assert.Single(Geographic(fx));
        Assert.True(File.Exists(Target), "The person allowed the write and it did not happen.");
    }

    /// <summary>
    /// "Allow for this run" is answered ONCE.
    ///
    /// <para>Two writes into the same folder, one question. A permission that has to be re-granted
    /// per call is not the permission the button offered, and the third identical card is where a
    /// person stops reading them - which is how an approval system becomes a formality.</para>
    /// </summary>
    [Fact]
    public async Task Allowing_a_place_for_the_run_is_not_asked_again()
    {
        using var fx = new EngineFixture();
        fx.Decisions.Answer = "run";

        var second = Path.Combine(_elsewhere, "second.txt");

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"Write both","steps":[]}"""),
            Turn.Calls1("run_command", Writing(Target)),
            Turn.Calls1("run_command", Writing(second)),
            Turn.Says("Both written."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer"), policy: Permissive), "write both files");

        Assert.True(File.Exists(Target) && File.Exists(second), "One of the two writes did not happen.");

        var asked = Geographic(fx);
        Assert.True(asked.Count == 1,
            $"The same folder was asked about {asked.Count} times after being allowed for the run.");
    }

    /// <summary>
    /// Ordinary work inside the workspace is not asked about at all - asserted here, at the gate,
    /// and not only of the heuristic. A rule that is right in a library and wired up too eagerly is
    /// still a product that interrupts people.
    /// </summary>
    [Fact]
    public async Task A_write_inside_the_workspace_asks_nothing()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says("""{"disposition":"quick_action","title":"Write it","steps":[]}"""),
            Turn.Calls1("run_command", Writing("inside.txt")),
            Turn.Says("Written."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer"), policy: Permissive), "write the file");

        Assert.Empty(Geographic(fx));
        Assert.True(File.Exists(Path.Combine(fx.Root, "inside.txt")), "The write inside the workspace did not happen.");
    }
}
