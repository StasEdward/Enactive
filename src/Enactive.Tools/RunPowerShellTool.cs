namespace Enactive.Tools;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
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
                   + "To save results, take the returned output and write it with write_file; do not redirect to a file here. "
                   + "A non-zero exit code is a FAILURE unless you declared it in 'expectedExitCodes' before running - "
                   + "do that when the exit code is part of the answer you want (a test runner reporting failing tests), "
                   + "never to excuse a script that was supposed to succeed.",
        JsonSchema: Schema);

    public PermissionLevel RequiredLevel => PermissionLevel.Execute;

    public async Task<ToolResult> InvokeAsync(string argumentsJson, ToolContext ctx, CancellationToken ct)
    {
        string? script;
        IReadOnlyCollection<int>? expected;
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            script = doc.RootElement.TryGetProperty("script", out var c) && c.ValueKind == JsonValueKind.String
                ? c.GetString() : null;

            // Refused before the script runs, not after it has had its effect.
            if (!ProcessExec.TryReadExpectedExitCodes(doc.RootElement, out expected, out var badCodes))
                return ToolResults.Fail(badCodes!);
        }
        catch (JsonException ex)
        {
            return ToolResults.Fail($"Invalid arguments JSON: {ex.Message}");
        }

        if (string.IsNullOrWhiteSpace(script))
            return ToolResults.Fail("'script' is required.");

        // -EncodedCommand takes base64 of the UTF-16LE script text — bypasses ALL cmd/shell quoting.
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        var startInfo = new ProcessStartInfo
        {
            FileName = OperatingSystem.IsWindows() ? "powershell.exe" : "pwsh",
            WorkingDirectory = ctx.WorkspaceRoot,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encoded);

        using var process = new Process { StartInfo = startInfo };
        // Bounded on purpose - see ProcessExec.CapturedStream. A command's output is not a budget
        // this application should let the command set.
        var stdout = new ProcessExec.CapturedStream();
        var stderr = new ProcessExec.CapturedStream();
        process.OutputDataReceived += (_, e) => stdout.Add(e.Data);
        process.ErrorDataReceived += (_, e) => stderr.Add(e.Data);

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return ToolResults.Fail($"PowerShell timed out after {TimeoutSeconds}s or was cancelled.");
        }
        catch (Exception ex)
        {
            return ToolResults.Fail($"Could not run PowerShell: {ex.Message}");
        }

        return ProcessExec.BuildResult(
            "PowerShell", process.ExitCode, stdout.ToString(), stderr.ToString(), expected);
    }

    private static readonly string Schema = $$"""
    {
      "type": "object",
      "properties": {
        "script": { "type": "string", "description": "The PowerShell script to run. Write it plainly; no shell quote-escaping is needed." },
        {{ProcessExec.ExpectedExitCodesSchema}}
      },
      "required": ["script"]
    }
    """;
}
