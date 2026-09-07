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
    /// The one argument mistake a model makes with these tools, named rather than run.
    ///
    /// <para>A whole command line arrives as a SINGLE array element - <c>["diff HEAD"]</c> instead of
    /// <c>["diff", "HEAD"]</c> - and each element is passed to the process verbatim, so git is asked
    /// to run a subcommand called "diff HEAD" and answers "is not a git command", with exit code 1
    /// and nothing else the model can use.</para>
    ///
    /// <para>Checking only the FIRST element is what makes this exact rather than clever: no git or
    /// docker subcommand contains whitespace, while later arguments legitimately do
    /// (<c>["commit", "-m", "message with spaces"]</c>) and must be left alone.</para>
    ///
    /// <para>It refuses instead of quietly splitting. The model is now shown why a call failed, so a
    /// refusal that names the fix costs one turn - and silently rewriting the arguments somebody
    /// passed is how a wrong action becomes one nobody can see.</para>
    /// </summary>
    public static string? WrongShapeOfArgs(string program, IReadOnlyList<string> args)
    {
        if (args.Count == 0 || args[0].AsSpan().IndexOfAny(' ', '\t', '\n') < 0)
            return null;

        var pieces = args[0].Split(
            new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries);

        return $"'{args[0]}' is not a {program} subcommand - it is a whole command line in one "
             + "argument. Each element of 'args' is passed through as ONE argument, so pass "
             + $"[{string.Join(", ", pieces.Select(p => $"\"{p}\""))}"
             + (args.Count > 1 ? ", ..." : "")
             + "] instead. An argument may contain spaces (a commit message, a path); the "
             + "subcommand never does.";
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

    /// <summary>
    /// The exit codes a caller DECLARED it expects, read from the tool's own arguments.
    ///
    /// <para>Reported 2026-09-07: a step whose whole job was "verify the tests fail when the
    /// behaviour is broken" ran the test project, got exit 1 because tests failed — which is the
    /// answer, not a malfunction — and the run died on it. <see cref="BuildResult"/> is right that
    /// "the process started" and "the command succeeded" are different things, and it cannot tell a
    /// broken build from a test runner reporting. The caller can: it knows what it asked for.</para>
    ///
    /// <para>So the caller says so IN THE ARGUMENTS, which it writes before it has seen any exit
    /// code or output. That makes it a prediction rather than an excuse — asked afterwards, anything
    /// would be declared expected. It is recorded in the evidence with the rest of the arguments,
    /// where the reviewer reads it alongside the output and can see whether it was honest.</para>
    ///
    /// <para>0 is always in the set. A command that succeeded has succeeded, whatever was declared,
    /// and no declaration can turn a clean exit into a failure.</para>
    /// </summary>
    /// <param name="error">Set when the property is present but not a list of integers.</param>
    public static bool TryReadExpectedExitCodes(
        JsonElement arguments, out IReadOnlyCollection<int>? codes, out string? error)
    {
        codes = null;
        error = null;

        if (!arguments.TryGetProperty(ExpectedExitCodes, out var declared)
            || declared.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return true;

        if (declared.ValueKind != JsonValueKind.Array)
        {
            error = $"'{ExpectedExitCodes}' must be an array of integers, e.g. [0, 1].";
            return false;
        }

        var set = new HashSet<int> { 0 };
        foreach (var element in declared.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var code))
            {
                error = $"'{ExpectedExitCodes}' must contain integers only, e.g. [0, 1]; "
                      + $"got {element.ValueKind}.";
                return false;
            }

            set.Add(code);
        }

        // An empty list declares nothing; the default already says 0.
        codes = set;
        return true;
    }

    /// <summary>The argument name, in one place — it appears in two schemas and two descriptions.</summary>
    public const string ExpectedExitCodes = "expectedExitCodes";

    /// <summary>
    /// The schema property and the guidance that goes with it, shared by every command-running tool.
    /// The guidance is the part that matters: a model told only that the field exists will reach for
    /// it whenever something is red.
    /// </summary>
    public const string ExpectedExitCodesSchema = """
        "expectedExitCodes": {
          "type": "array",
          "items": { "type": "integer" },
          "description": "Exit codes that mean this command DID its job, declared before you run it. Use ONLY when the exit code is part of the answer you are asking for - a test runner that returns 1 when tests fail, 'git diff --exit-code', a linter that returns non-zero when it finds something. Do NOT use it to excuse a command that was supposed to succeed: a build that fails is a failure whatever you declare, and the declaration is recorded and reviewed. 0 always counts as success and does not need listing. Default: [0]."
        }
        """;

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
