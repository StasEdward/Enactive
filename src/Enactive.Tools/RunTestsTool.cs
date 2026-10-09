namespace Enactive.Tools;

using System.Text;
using System.Text.Json;
using Enactive.Core.Builds;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>
/// Runs the workspace's tests the way the workspace's own ecosystem runs them, and answers with what the run came to:
/// the totals and each failed test with what it said and where.
///
/// <para><b>Why.</b> Run 97de74b1, 2026-10-09: a model ran its tests through run_command - `dotnet test ... 2>&1 |
/// findstr ...`, `dotnet clean && dotnet build && dotnet test ...` - and read 30,000 characters of MSBuild and xUnit
/// log in three pieces to find seven failures; a third of its prompt was that log. The engine already knows how this
/// workspace's tests run (IEcosystem: the test projects, the command, how its output reads). Here the model asks for
/// a run, and the ecosystem builds the command and reads the result.</para>
///
/// <para>Offered wherever an ecosystem is known, not only where tests are found when the run starts: a step that makes
/// the test project is often the one before the step that runs it. Where no ecosystem knows the workspace's tests, it
/// says so, and run_command is the way. Failing tests are its finding, not its failure: a run that tested is a run that
/// succeeded, whatever it found. A build that failed is not - the tests did not run.</para>
/// </summary>
public sealed class RunTestsTool(IReadOnlyList<IEcosystem> ecosystems, ITool? runner = null) : ITool
{
    private readonly ITool _runner = runner ?? new RunCommandTool();

    public ToolDefinition Definition { get; } = new(
        Name: "run_tests",
        Description: "Run the workspace's tests and get what they came to: the totals, and each failed test with its message and "
                   + "where it failed. Use this rather than run_command to run tests: it knows the test projects and the "
                   + "flags. 'target' runs one test project (default: all of them); 'filter' runs only the tests whose name "
                   + "contains it. The whole output is kept in the scratch.",
        JsonSchema: Schema, WorkspaceEffect: WorkspaceEffect.Unknown,
        ProgressIdentity: ProgressIdentity.Action, Kind: ToolKind.Command);

    public PermissionLevel RequiredLevel => PermissionLevel.Execute;

    public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
    {
        string? target = null, filter = null;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                target = Text(doc.RootElement, "target");
                filter = Text(doc.RootElement, "filter");
            }
        }
        catch (JsonException ex) { return ToolResults.Unreadable($"Invalid arguments JSON: {ex.Message}"); }

        var found = ecosystems.Select(e => (Ecosystem: e, Targets: e.Detect(ctx.WorkspaceRoot)))
            .Where(f => f.Targets is { Tests.Count: > 0 }).ToArray();
        if (found.Length == 0)
            return ToolResults.Fail("No test project is known here: no ecosystem this engine knows found tests in the workspace. "
                + "If the tests are run some other way, run them with run_command.");

        var runs = new List<(IEcosystem Ecosystem, string Target)>();
        foreach (var (ecosystem, targets) in found)
            foreach (var test in targets!.Tests)
                if (target is null || Same(test, target))
                    runs.Add((ecosystem, test));
        if (runs.Count == 0)
            return ToolResults.Fail($"'{target}' is not a test project here. The test projects are: "
                + string.Join(", ", found.SelectMany(f => f.Targets!.Tests)) + ".");

        var said = new StringBuilder();
        var allRan = true;
        var anyFailed = false;
        foreach (var (ecosystem, test) in runs)
        {
            var command = filter is null ? ecosystem.TestCommand(test) : ecosystem.TestCommand(test, filter);
            if (command is null)
            {
                said.AppendLine($"{test}: this ecosystem cannot run only some of the tests; run without 'filter'.");
                allRan = false;
                continue;
            }
            // Exit 1 is how a test runner says a test failed - the finding this tool is for - so it is declared expected;
            // whether the tests ran at all is read from what the run printed, below.
            var result = await _runner.InvokeAsync(JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["command"] = command, ["expectedExitCodes"] = new[] { 0, 1 }
            }), ctx, ct);
            var whole = CommandOutput.Whole(result, ctx.WorkspaceRoot);
            var kept = CommandOutput.KeptPath(result);

            if (runs.Count > 1) said.AppendLine($"== {test} ==");
            if (whole is not null && ecosystem.DescribeTests(whole) is { } described)
            {
                said.AppendLine(described);
                anyFailed |= ecosystem.ParseTests(whole) is { } report
                             && (report.Summary is { Failed: > 0 } || report.Cases.Any(c => c.Verdict == TestVerdict.Failed));
            }
            else
            {
                // No test run in what it printed: it did not build, or the runner did not start. The build's own errors,
                // read as the build check reads them, say which.
                allRan = false;
                var errors = whole is null ? [] : ecosystem.ParseDiagnostics(whole, ctx.WorkspaceRoot)
                    .Where(d => d.Severity == DiagnosticSeverity.Error).Take(10).ToArray();
                said.AppendLine(errors.Length > 0
                    ? "The tests did not run: the build failed.\n" + string.Join("\n", errors.Select(e => $"- {e.Path}{(e.Line is { } line ? $"({line})" : "")}: {e.Code} {e.Message}"))
                    : "The tests did not run, and what it printed does not say why; the whole output says what happened.");
            }
            if (kept is not null) said.AppendLine($"The whole output is kept in {kept}.");
            said.AppendLine($"(ran: {command})");
        }

        var text = said.ToString().TrimEnd();
        return allRan
            ? ToolResults.Ok(output: text, metadata: new Dictionary<string, object?> { ["testsFailed"] = anyFailed })
            : ToolResults.Fail(text, text);
    }

    private static bool Same(string a, string b)
        => string.Equals(a.Replace('\\', '/').Trim('/'), b.Replace('\\', '/').Trim('/'), StringComparison.OrdinalIgnoreCase)
           || string.Equals(Path.GetFileNameWithoutExtension(a), b, StringComparison.OrdinalIgnoreCase);

    private static string? Text(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s.Trim() : null;

    private const string Schema = """
        {"type":"object","properties":{
          "target":{"type":"string","description":"One test project to run, as the workspace names it; leave out to run every test project."},
          "filter":{"type":"string","description":"Run only the tests whose full name contains this."}}}
        """;
}
