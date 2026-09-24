namespace Enactive.Tools;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Enactive.Core.Context;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;

/// <summary>
/// Runs a PowerShell script in the workspace and returns stdout/stderr + exit code. Unlike
/// <see cref="RunCommandTool"/> (which goes through cmd.exe and makes PowerShell one-liners a
/// quote-escaping nightmare), this passes the script via <c>-EncodedCommand</c> (base64 of the UTF-16
/// script), so NO shell quoting is involved at all — the model just writes the script. Windows uses
/// powershell.exe; elsewhere it tries pwsh.
/// </summary>
public sealed class RunPowerShellTool : ITool
{
    private const int TimeoutSeconds = 90;

    public ToolDefinition Definition { get; } = new(
        Name: "run_powershell",
        Description: "Run a PowerShell script on Windows and return its stdout/stderr and exit code. "
                   + "PREFER this over run_command for anything using PowerShell (Get-WmiObject/Get-CimInstance, "
                   + "Get-PSDrive, pipes, quotes): write the script plainly — NO shell quote-escaping is needed. "
                   + "To save results: write the returned output with write_file when it came back WHOLE, or - when "
                   + "the result says it was cut - redirect into '" + WorkspaceGuard.ScratchPrefix
                   + "/' and copy_file that into place, which keeps every byte. Output you redirected and did not "
                   + "read is output you have not seen. "
                   + "A non-zero exit code is a FAILURE unless you declared it in 'expectedExitCodes' before running - "
                   + "do that when the exit code is part of the answer you want (a test runner reporting failing tests), "
                   + "never to excuse a script that was supposed to succeed.",
        JsonSchema: Schema);

    public PermissionLevel RequiredLevel => PermissionLevel.Execute;

    /// <summary>
    /// The most base64 a Windows command line will take. Windows' documented limit is 32,767
    /// characters for the whole line; this leaves room for the executable, the switches and the
    /// quoting around them.
    ///
    /// <para>A limit worth naming rather than discovering: the error Windows returns for crossing
    /// it is "The filename or extension is too long", which says nothing true about a script.</para>
    /// </summary>
    internal const int MaxEncodedChars = 32_000;

    public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
    {
        string? script;
        bool sentCommand;
        IReadOnlyCollection<int>? expected;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            script = doc.RootElement.TryGetProperty("script", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;

            // Read inside the try, while the document is still alive, and used after it.
            sentCommand = doc.RootElement.TryGetProperty("command", out var other)
                          && other.ValueKind == JsonValueKind.String
                          && !string.IsNullOrWhiteSpace(other.GetString());

            // Refused before the script runs, not after it has had its effect.
            if (!ProcessExec.TryReadExpectedExitCodes(doc.RootElement, out expected, out var badCodes))
                return ToolResults.Unreadable(badCodes!);
        }
        catch (JsonException ex)
        {
            return ToolResults.Unreadable($"Invalid arguments JSON: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(script))
            return ToolResults.Unreadable(ProcessExec.NoCommandGiven(
                sentCommand, "script", "command", "run_command", "PowerShell", "cmd.exe"));

        // Progress records go to the ERROR stream, and a redirected error stream is where the model
        // reads failures from. `Get-ChildItem -Recurse` alone put two "Preparing modules for first
        // use" records in front of every answer. Silenced first, so a script that says nothing
        // really says nothing - which is what the exit-code rule below depends on.
        //
        // Two separate mismatches, both real, both needed:
        //
        // - READING: a script's own Get-Content (or any cmdlet with an -Encoding parameter) defaults
        //   to the system's ANSI code page for a file with no BOM - not UTF-8 - so a UTF-8 file with
        //   any non-ASCII character is misread before the script ever touches it. $PSDefaultParameterValues
        //   makes UTF-8 the default for every such cmdlet without requiring the model to name it.
        // - WRITING: Windows PowerShell's own default for a REDIRECTED console is an OEM code page,
        //   so even correctly-read text is re-encoded wrong on the way out. [Console]::OutputEncoding
        //   pairs this with ProcessExec.Utf8NoBom on the .NET side of the same pipe.
        //
        // Reported from a real run, 2026-09-25: a report this run had itself written (UTF-8, em
        // dashes) came back through Get-Content looking corrupted, and the model - reading its own
        // correct file as garbage - spent the rest of the step trying to fix damage that was never
        // there. See ShellOutputEncodingTests.
        var prepared = "$PSDefaultParameterValues['*:Encoding'] = 'utf8'\r\n"
                     + "[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\r\n"
                     + "$ProgressPreference = 'SilentlyContinue'\r\n" + script;

        // -EncodedCommand takes base64 of the UTF-16LE script text — bypasses ALL cmd/shell quoting.
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(prepared));

        // And that is what puts a ceiling on how long a script may be. Base64 of UTF-16 is roughly
        // 2.7x the characters, and Windows takes about 32,000 on a command line - so a script of
        // twelve thousand characters does not fit, and the answer Windows gives for that is "The
        // filename or extension is too long", about a script with no filename in it.
        //
        // Measured 2026-09-24 01:18: a step blocked from write_file by the shrink guard tried to
        // write its 12 KB report through a here-string instead, and died on this with a message
        // that pointed nowhere. Refused here, before the attempt, with the route it should have
        // taken - a long document is a FILE, not a command line.
        if (encoded.Length > MaxEncodedChars)
            return ToolResults.Unreadable(
                $"This script is too long to run: {script.Length:N0} characters becomes "
                + $"{encoded.Length:N0} once encoded, and Windows takes about {MaxEncodedChars:N0} "
                + "on a command line. Nothing ran. If it is long because it CARRIES content - a "
                + "document in a here-string, a file being written - that content belongs in a "
                + "file: use write_file, or edit_file to change part of one. If the script itself "
                + "is long, write it to '" + WorkspaceGuard.ScratchPrefix + "/' and run that path.");

        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
            WorkingDirectory = ctx.WorkspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,

            // Paired with [Console]::OutputEncoding above - see ProcessExec.Utf8NoBom.
            StandardOutputEncoding = ProcessExec.Utf8NoBom,
            StandardErrorEncoding = ProcessExec.Utf8NoBom
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        // Pins the OUTPUT stream to text. It does NOT help the error stream: -EncodedCommand makes
        // this a minishell, and a minishell serialises its non-output streams as CLIXML whatever
        // this says - measured, both ways round. That is what CliXml is for.
        startInfo.ArgumentList.Add("-OutputFormat");
        startInfo.ArgumentList.Add("Text");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encoded);

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
            // Contained, like run_command: Start-Process is exactly the shape that used to walk
            // away from a cancelled run. See ProcessJob.
            outcome = await ProcessExec.RunContainedAsync(process, TimeoutSeconds, ct);
            if (!outcome.Completed)
                return ToolResults.Fail($"PowerShell timed out after {TimeoutSeconds}s or was cancelled.");
        }
        catch (Exception ex)
        {
            return ToolResults.Fail($"Could not run PowerShell: {ex.Message}");
        }

        // Decoded BEFORE anything is decided from it. A blob of progress records is not an error
        // report, and reading it as one is how a script that had nothing to say was called a
        // failure - see CliXml.
        var errors = CliXml.ToText(stderr.ToString());

        return ProcessExec.BuildResult(
            "PowerShell", process.ExitCode, CliXml.ToText(stdout.ToString()), errors,
            Tolerated(process.ExitCode, errors, expected), declarable: true,
            outputCutShort: outcome.OutputCutShort, commandLine: script);
    }

    /// <summary>
    /// The exit codes that count as success for THIS run, given what the script actually said.
    ///
    /// <para><b>PowerShell's exit code is not a verdict on the script.</b> <c>powershell.exe</c>
    /// returns 1 when the last statement left <c>$?</c> false - and <c>-ErrorAction
    /// SilentlyContinue</c> silences the DISPLAY of an error, not the error. So a script doing
    /// exactly what it was asked returns 1:</para>
    ///
    /// <list type="bullet">
    /// <item><c>Get-ChildItem C:\ -Recurse -ErrorAction SilentlyContinue</c> - meets a folder it may
    /// not read, which is the case that flag exists for. Exit 1.</item>
    /// <item><c>Get-ItemProperty HKLM:\...\NotThere -ErrorAction SilentlyContinue</c> - the key is
    /// absent, which was the question. Exit 1.</item>
    /// <item><c>Get-Command whatever*</c> matching nothing - no error to silence. Exit 0.</item>
    /// </list>
    ///
    /// <para>Measured, all three. So "found nothing" and "the script broke" arrived identically, a
    /// run that answered its question correctly was recorded Incomplete, and the failure text told
    /// the model to look at output that named no cause - or to declare 1 expected, which is the one
    /// thing <see cref="ProcessExec.ExpectedExitCodes"/> says never to do.</para>
    ///
    /// <para><b>The discriminator is the error stream, not the number.</b> With progress silenced
    /// and <c>-OutputFormat Text</c>, a script with nothing to report says NOTHING; a script that
    /// really failed says why, in words. So a non-zero code with an empty error stream is a script
    /// that ran, and the real code is still shown in the output and the metadata - nothing is
    /// hidden, only re-read.</para>
    ///
    /// <para>Scoped to PowerShell on purpose. For <c>run_command</c>, git and docker the exit code
    /// is the program's own and does mean something; there, <c>expectedExitCodes</c> is the answer.
    /// </para>
    /// </summary>
    private static IReadOnlyCollection<int>? Tolerated(
        int exitCode, string errors, IReadOnlyCollection<int>? declared)
    {
        if (exitCode == 0 || errors.Trim().Length > 0)
            return declared;

        var allowed = new HashSet<int>(declared ?? Array.Empty<int>()) { 0, exitCode };
        return allowed;
    }

    private static readonly string Schema = $$"""
    {
      "type": "object",
      "properties": {
        "script": { "type": "string", "description": "The PowerShell script to run. Write it plainly; no shell quote-escaping is needed." },
        {{ProcessExec.ExpectedExitCodesSchema}},
        {{ProcessExec.ForceSchema}}
      },
      "required": ["script"]
    }
    """;
}
