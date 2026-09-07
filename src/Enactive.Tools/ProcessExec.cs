namespace Enactive.Tools;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Enactive.Core.Tools;

/// <summary>
/// Shared helper for tools that run a fixed executable (git, docker, ...) with a list of arguments.
/// Passes each argument via ProcessStartInfo.ArgumentList (no shell, no quoting problems), captures
/// stdout/stderr + exit code, times out, and never throws — a missing binary or failure becomes a
/// ToolResult, not an exception.
/// </summary>
internal static class ProcessExec
{
    private const int MaxOutputChars = 6000;

    /// <summary>
    /// How much of one stream is held in memory while a process runs. Far more than
    /// <see cref="MaxOutputChars"/>, which is what actually reaches the model, because this is not a
    /// display budget - it is the ceiling that stops a command from deciding how much memory this
    /// application uses. A build that prints a gigabyte used to be accumulated in full and then
    /// truncated to six thousand characters: every byte paid for, none of it read.
    /// </summary>
    private const int MaxCapturedChars = 64_000;

    /// <summary>Reads an "args" element that is either an array of strings or a single whitespace-split string.</summary>
    public static List<string> ParseArgs(JsonElement args)
    {
        var list = new List<string>();
        if (args.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in args.EnumerateArray())
                if (e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s)
                    list.Add(s);
        }
        else if (args.ValueKind == JsonValueKind.String && args.GetString() is { } str)
        {
            foreach (var part in str.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                list.Add(part);
        }
        return list;
    }

    /// <summary>
    /// Turns a finished process into a <see cref="ToolResult"/>, shared by every process-running tool.
    /// A non-zero exit code is a FAILED result: "the process started" and "the command succeeded" are
    /// two different things, and reporting the second when only the first is true pushes the detection
    /// of a failure onto the model and an optional reviewer. Output is preserved either way.
    /// <paramref name="allowedExitCodes"/> covers the commands whose codes carry meaning
    /// (e.g. <c>git diff --exit-code</c>); the default is {0}.
    /// </summary>
    public static ToolResult BuildResult(
        string what, int exitCode, string stdout, string stderr, IReadOnlyCollection<int>? allowedExitCodes = null)
    {
        var combined = stdout;
        if (stderr.Length > 0)
            combined += "\n[stderr]\n" + stderr;
        combined = combined.Trim();
        if (combined.Length > MaxOutputChars)
            combined = combined[..MaxOutputChars] + "\n… (truncated)";

        // Label the result clearly so the model uses the OUTPUT (not the command text) when asked to save it.
        var output = $"exit code {exitCode}\n----- command output (this is the result) -----\n{combined}";
        var metadata = new Dictionary<string, object?> { ["exitCode"] = exitCode };

        var allowed = allowedExitCodes is { Count: > 0 } ? allowedExitCodes : DefaultAllowedExitCodes;
        return allowed.Contains(exitCode)
            ? ToolResults.Ok(output: output, metadata: metadata)
            : ToolResults.Fail($"{what} exited with code {exitCode}.", output, metadata);
    }

    private static readonly IReadOnlyCollection<int> DefaultAllowedExitCodes = new[] { 0 };

    public static async Task<ToolResult> RunAsync(
        string fileName, IReadOnlyList<string> args, string workingDir, int timeoutSeconds,
        CancellationToken ct, IReadOnlyCollection<int>? allowedExitCodes = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in args)
            startInfo.ArgumentList.Add(a);

        using var process = new Process { StartInfo = startInfo };
        var stdout = new CapturedStream();
        var stderr = new CapturedStream();
        process.OutputDataReceived += (_, e) => stdout.Add(e.Data);
        process.ErrorDataReceived += (_, e) => stderr.Add(e.Data);

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return ToolResults.Fail($"{fileName} timed out after {timeoutSeconds}s or was cancelled.");
        }
        catch (Exception ex)
        {
            // Most commonly: the binary is not installed / not on PATH.
            return ToolResults.Fail($"Could not run {fileName}: {ex.Message}");
        }

        return BuildResult(fileName, process.ExitCode, stdout.ToString(), stderr.ToString(), allowedExitCodes);
    }

    /// <summary>
    /// One process stream, captured up to a ceiling. Past it the lines are counted and dropped
    /// rather than kept: the caller only ever shows the first few thousand characters anyway, and a
    /// command's output is not a budget this application should let the command set.
    ///
    /// <para>Says so when it happens. Silently keeping the first N characters of a build log and
    /// calling that "the output" is how a model comes to believe a build succeeded because the
    /// errors were off the end.</para>
    /// </summary>
    public sealed class CapturedStream
    {
        private readonly StringBuilder _text = new();
        private int _dropped;

        public void Add(string? line)
        {
            if (line is null)
                return;

            if (_text.Length + line.Length + 1 > MaxCapturedChars)
            {
                _dropped++;
                return;
            }

            _text.AppendLine(line);
        }

        public override string ToString()
            => _dropped == 0
                ? _text.ToString()
                : _text + $"… ({_dropped} more line(s) produced and dropped — output passed "
                        + $"{MaxCapturedChars:N0} characters)\n";
    }
}
