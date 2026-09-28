namespace Enactive.Tools;

using System.Diagnostics;
using System.Text.Json;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>
/// Runs a shell command in the workspace directory (Execute level — and policy keeps it behind an
/// approval by default). Captures stdout/stderr and the exit code.
/// </summary>
public sealed class RunCommandTool : ITool
{
    private const int TimeoutSeconds = 60;

    public ToolDefinition Definition { get; } = new(
        Name: "run_command",
        Description: "Run a shell command in the workspace directory and return its stdout/stderr and exit code. "
                   + "Use for builds, tests, git, etc. A non-zero exit code is a FAILURE unless you declared it "
                   + "in 'expectedExitCodes' before running - do that when the exit code is part of the answer "
                   + "you want (a test runner reporting failing tests), never to excuse a command that was "
                   + "supposed to succeed.",
        JsonSchema: Schema, Kind: ToolKind.Command, RunsSuccessChecks: true, CommandPolicy: CommandPolicySyntax.SimpleCommand);

    public PermissionLevel RequiredLevel => PermissionLevel.Execute;

    public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
    {
        string? command;
        bool sentScript;
        IReadOnlyCollection<int>? expected;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            command = doc.RootElement.TryGetProperty("command", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;

            // Read inside the try, while the document is still alive, and used after it.
            sentScript = doc.RootElement.TryGetProperty("script", out var other)
                         && other.ValueKind == JsonValueKind.String
                         && !string.IsNullOrWhiteSpace(other.GetString());

            // Read here, used at the very end - so a malformed declaration is refused BEFORE the
            // command runs rather than after it has had its effect.
            if (!ProcessExec.TryReadExpectedExitCodes(doc.RootElement, out expected, out var badCodes))
                return ToolResults.Unreadable(badCodes!);
        }
        catch (JsonException ex)
        {
            return ToolResults.Unreadable($"Invalid arguments JSON: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(command))
            return ToolResults.Unreadable(ProcessExec.NoCommandGiven(
                sentScript, "command", "script", "run_powershell", "cmd.exe", "PowerShell"));

        var startInfo = new ProcessStartInfo
        {
            WorkingDirectory = ctx.WorkspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,

            // Told explicitly rather than left to fall back to the console's own encoding, which on
            // Windows is an OEM code page, not UTF-8. Reported from a real run, 2026-09-25: a report
            // this very run had written (plain UTF-8, em dashes) came back through `type` and a
            // nested `powershell -Command` with every em dash turned to mojibake - the model read
            // its own correct file as "corrupted", and lost the rest of the step trying to fix
            // damage that was never there. See ShellOutputEncodingTests.
            StandardOutputEncoding = ProcessExec.Utf8NoBom,
            StandardErrorEncoding = ProcessExec.Utf8NoBom
        };
        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName = "cmd.exe";

            // Arguments, NOT ArgumentList - and this is the whole of the difference.
            //
            // .NET joins ArgumentList with the C RUNTIME's rules: wrap anything containing a space
            // in quotes, and escape an inner quote as \". cmd.exe does not speak that language. So
            // `start "" "C:\Program Files\totalcmd\TOTALCMD64.EXE"` - which is exactly how that is
            // written - arrived at cmd as `\"\" \"C:\Program Files\...\"`, cmd read `\"\"` as the
            // name of a program, and Windows put a modal dialog on the user's screen saying it
            // cannot find `\\`. The run then sat for 24 seconds until somebody clicked OK. Measured
            // 2026-09-10, from a real run; `echo` had hidden it for years by printing the
            // backslashes without complaint.
            //
            // /s is what makes this exact rather than lucky: with /s, cmd strips the FIRST and LAST
            // character if both are quotes and runs everything between them verbatim. Without it
            // the rule is conditional on how many quotes are in the string - which is how a command
            // came to depend on its own punctuation.
            //
            // `chcp 65001` first switches THIS cmd session's own code page to UTF-8, so cmd itself
            // and anything it launches (a nested `powershell -Command`, `findstr`, `type`) write
            // UTF-8 bytes on the pipe .NET is now told to decode as UTF-8, rather than the two
            // mismatched re-encodings that produced the mojibake above.
            startInfo.Arguments = $"/s /c \"chcp 65001>nul & {command}\"";
        }
        else
        {
            // A list is right here: execve takes an array, so nothing re-parses the arguments and
            // there is no quoting to get wrong. Same for git and docker in ProcessExec, where the
            // program being run does use the C runtime's rules and .NET's joining is correct.
            startInfo.FileName = "/bin/sh";
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(command);
        }

        using var process = new Process { StartInfo = startInfo };
        // Bounded on purpose - see ProcessExec.CapturedStream. A command's output is not a budget
        // this application should let the command set.
        var stdout = new ProcessExec.CapturedStream();
        var stderr = new ProcessExec.CapturedStream();
        process.OutputDataReceived += (_, e) => stdout.Add(e.Data);
        process.ErrorDataReceived += (_, e) => stderr.Add(e.Data);

        ProcessExec.RunOutcome outcome;
        try
        {
            // Contained: if this call is CANCELLED, the command and everything it started dies with
            // it. A command that finishes normally leaves what it started running - `start "" app`
            // is a thing people ask a shell to do. See ProcessJob for both halves of that.
            outcome = await ProcessExec.RunContainedAsync(process, TimeoutSeconds, ct);
            ct.ThrowIfCancellationRequested();
            if (!outcome.Completed)
                return ToolResults.Fail($"Command timed out after {TimeoutSeconds}s or was cancelled.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return ToolResults.Fail($"Could not run command: {ex.Message}");
        }

        var result = ProcessExec.BuildResult(
            "Command", process.ExitCode, stdout.ToString(), stderr.ToString(), expected,
            declarable: true, outputCutShort: outcome.OutputCutShort, commandLine: command);
        if (result.DidNotRun && OperatingSystem.IsWindows()
            && Environment.GetEnvironmentVariable("NoDefaultCurrentDirectoryInExePath") is not null)
            result = result with { Error = result.Error + " Windows current-directory executable lookup is disabled "
                + "by NoDefaultCurrentDirectoryInExePath. If this is a workspace script, use an explicit "
                + @"relative path such as .\script.cmd (quote it if it contains spaces)." };
        return result;
    }

    private static readonly string Schema = $$"""
    {
      "type": "object",
      "properties": {
        "command": { "type": "string", "description": "The shell command to run in the workspace directory." },
        {{ProcessExec.ExpectedExitCodesSchema}},
        {{ProcessExec.ForceSchema}}
      },
      "required": ["command"]
    }
    """;
}
