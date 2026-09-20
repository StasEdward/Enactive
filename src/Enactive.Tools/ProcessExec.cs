namespace Enactive.Tools;

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Enactive.Core.Execution;
using Enactive.Core.Tools;

/// <summary>
/// Shared helper for tools that run a fixed executable (git, docker, ...) with a list of arguments.
/// Passes each argument via ProcessStartInfo.ArgumentList (no shell, no quoting problems), captures
/// stdout/stderr + exit code, times out, and never throws — a missing binary or failure becomes a
/// ToolResult, not an exception.
/// </summary>
internal static class ProcessExec
{
    /// <summary>
    /// How much of a command's output reaches the model.
    ///
    /// <para>Doubled from 6,000 on 2026-09-20, with the evidence in hand: a run of
    /// <c>dotnet test --verbosity normal</c> on a two-project solution spent more than six
    /// thousand characters on restore chatter and certificate notices before reaching anything
    /// about tests, and the output was shortened 52 times in one session. The captured ceiling is
    /// <see cref="MaxCapturedChars"/> — ten times this — so the data was there and withheld.</para>
    ///
    /// <para>Not raised further, and this is the reason: the result goes into the transcript and
    /// stays there. A local model with <c>num_ctx</c> of 16,384 holds about 49,000 characters in
    /// total, so a handful of results at this size is already most of its window, and what
    /// rescues it then is <c>Transcript.Elide</c> throwing the oldest ones away. A generous cap
    /// buys one good answer and then starts destroying the conversation that needed it.</para>
    /// </summary>
    private const int MaxOutputChars = 12_000;

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
    /// (<c>["commit", "-m", "message with spaces"]</c>) and must be left alone. Two shapes are
    /// refused there: whitespace, which is a command line packed into one string, and a
    /// quote-comma-quote, which is the JSON array written INSIDE one - see the note in the body for
    /// where the second one came from.</para>
    ///
    /// <para>It refuses instead of quietly splitting. The model is now shown why a call failed, so a
    /// refusal that names the fix costs one turn - and silently rewriting the arguments somebody
    /// passed is how a wrong action becomes one nobody can see.</para>
    /// </summary>
    public static string? WrongShapeOfArgs(string program, IReadOnlyList<string> args)
    {
        // A quote-comma-quote INSIDE one element is the array syntax written as data — the mistake
        // this message itself provokes. Reported 2026-08: told to «pass ["diff", "HEAD"] instead»,
        // the model sent {"args":["diff\",\"HEAD"]}, one argument reading `diff","HEAD`, which git
        // then rejected as an unknown subcommand. No real argument contains that sequence.
        var pasted = args.Count > 0 && args[0].Contains("\",\"", StringComparison.Ordinal);

        if (args.Count == 0 || (!pasted && args[0].AsSpan().IndexOfAny(' ', '\t', '\n') < 0))
            return null;

        var pieces = args[0].Split(
            new[] { ' ', '\t', '\n', '"', ',' }, StringSplitOptions.RemoveEmptyEntries);

        // An element made of nothing but punctuation leaves no pieces to name, and a message that
        // throws while explaining a mistake is worse than the mistake.
        if (pieces.Length == 0)
            return $"'{args[0]}' is not a {program} subcommand and has no argument in it. Send each "
                 + "argument as its own element of 'args', with no brackets, quotes or commas inside "
                 + "an element.";

        // Says the SHAPE rather than showing the syntax. Showing it is what got pasted into a string
        // the first time; naming the pieces and how many there should be cannot be copied wrongly.
        return $"'{args[0]}' is not a {program} subcommand - it is a whole command line in one "
             + "argument. Send each argument as its OWN element of 'args': "
             + $"{pieces.Length} elements here, the first being {pieces[0]}"
             + (pieces.Length > 1 ? $" and the second {pieces[1]}" : "")
             + (args.Count > 1 ? ", then the arguments you already sent after it" : "")
             + ". Do not put brackets, quotes or commas inside an element - they are the JSON around "
             + "the strings, not part of them. An argument may contain spaces (a commit message, a "
             + "path); the subcommand never does.";
    }

    /// <summary>
    /// Turns a finished process into a <see cref="ToolResult"/>, shared by every process-running tool.
    /// A non-zero exit code is a FAILED result: "the process started" and "the command succeeded" are
    /// two different things, and reporting the second when only the first is true pushes the detection
    /// of a failure onto the model and an optional reviewer. Output is preserved either way.
    /// <paramref name="allowedExitCodes"/> covers the commands whose codes carry meaning
    /// (e.g. <c>git diff --exit-code</c>); the default is {0}.
    /// </summary>
    /// <param name="declarable">
    /// Whether this tool accepts <see cref="ExpectedExitCodes"/> — if it does, a failure says so.
    ///
    /// <para>Found 2026-09-07 20:49, the first run after the declaration shipped: the model ran the
    /// test project three times, never declared anything, and ended its report with "the fallback
    /// parsing failure specifically confirms that the behavior I identified as a gap is indeed
    /// broken… satisfying the requirement to implement tests that fail if the behavior is broken."
    /// It KNEW the failure was the answer. It said so in prose. It did not say so in the arguments,
    /// because nothing asked it to at the moment it wrote them.</para>
    ///
    /// <para>So the failure itself says how. That is the pattern every other correction in this
    /// codebase follows — the git argument shape, the CRLF edit, the denied tool: put the fix in the
    /// message the model is actually reading, at the moment it is stuck.</para>
    /// </param>
    /// <param name="outputCutShort">
    /// The command ended while something it started still held the output pipe. Said in the result,
    /// in one shared place, because the alternative is a model reading a short output as a complete
    /// one - and "the build printed nothing" is a very different conclusion from "the build printed
    /// something I did not wait for".
    /// </param>
    public static ToolResult BuildResult(
        string what, int exitCode, string stdout, string stderr,
        IReadOnlyCollection<int>? allowedExitCodes = null, bool declarable = false,
        bool outputCutShort = false)
    {
        var combined = stdout;
        if (stderr.Length > 0)
            combined += "\n[stderr]\n" + stderr;
        combined = combined.Trim();
        // The START and the END, not the first N characters. A program reports its outcome last,
        // so head-only shortening hands the model the part with no answer in it: on 2026-09-20 a
        // step wrote its tests, ran them, and could not tell whether they passed - the summary was
        // past the cut - then spent eight turns trying to pipe the output into a file and died on
        // the stall guard. ExecutionJournal fixed exactly this on 2026-09-07 for the REVIEWER and
        // the rule stayed private to it; the model that ran the command still got head-only.
        combined = Shortening.ToFit(combined, MaxOutputChars);

        if (outputCutShort)
            combined += "\n… (the command finished, but something it started is still running and "
                      + "holding the output. Anything printed after this point is not here.)";

        // Label the result clearly so the model uses the OUTPUT (not the command text) when asked to save it.
        var output = $"exit code {exitCode}\n----- command output (this is the result) -----\n{combined}";
        var metadata = new Dictionary<string, object?> { ["exitCode"] = exitCode };

        var allowed = allowedExitCodes is { Count: > 0 } ? allowedExitCodes : DefaultAllowedExitCodes;
        if (allowed.Contains(exitCode))
            return ToolResults.Ok(output: output, metadata: metadata);

        // Said only when nothing was declared: repeating the option to somebody who used it and
        // still failed is noise, and worse, reads as an invitation to widen the declaration.
        var howTo = declarable && allowedExitCodes is null
            ? $" If this exit code IS the answer you wanted — a test runner reporting failing tests, "
              + $"a linter reporting findings — run the same command again with "
              + $"\"{ExpectedExitCodes}\": [0, {exitCode}] and the result will count as a finding "
              + $"instead of a failure. If the command was meant to succeed, do NOT do that: the "
              + $"output above says what went wrong, so fix the cause."
            : "";

        return ToolResults.Fail($"{what} exited with code {exitCode}.{howTo}", output, metadata);
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

    /// <summary>The argument name. Defined in Core, which the engine can see too — it has to key
    /// calls without it, so the spelling cannot live only on this side.</summary>
    public const string ExpectedExitCodes = ToolArguments.ExpectedExitCodes;

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

    /// <summary>
    /// How long output is still collected after the process itself has gone.
    ///
    /// <para>Bounded, and that is the whole point of it: the pipe belongs to whoever is holding it,
    /// and something the command started on purpose may hold it for hours. Two seconds is long
    /// enough for lines already written to arrive and short enough that nobody calls it a hang.</para>
    /// </summary>
    private static readonly TimeSpan OutputGrace = TimeSpan.FromSeconds(2);

    /// <summary>What a contained run ended up doing.</summary>
    /// <param name="Completed">
    /// True when the process ran to its own end. False when it was cancelled or timed out - and
    /// then it, and everything it started, is already dead.
    /// </param>
    /// <param name="OutputCutShort">
    /// The process ended but something it started still holds the output pipe, so what was captured
    /// may not be all of it. Said out loud rather than left for the reader to wonder about.
    /// </param>
    public readonly record struct RunOutcome(bool Completed, bool OutputCutShort);

    /// <summary>
    /// Starts a process CONTAINED, waits for THE PROCESS, and cleans up what this call is
    /// responsible for.
    ///
    /// <para>One helper rather than the three near-copies this replaces - <c>run_command</c>,
    /// <c>run_powershell</c> and <see cref="RunAsync"/> each had the same start / wait / kill dance,
    /// and the same wrong kill in it. This codebase has already paid for that shape once: three
    /// provider adapters had the same header code and two of them had quietly lost it.</para>
    ///
    /// <para>The process runs inside a <see cref="ProcessJob"/>, so cancelling really does cancel:
    /// see that type for what a job changes, and for the deliberate limit on it - a call that ended
    /// normally leaves running whatever it was asked to start.</para>
    /// </summary>
    /// <param name="process">Not started yet; this starts it. Output reading must not be started by the caller.</param>
    public static async Task<RunOutcome> RunContainedAsync(
        Process process, int timeoutSeconds, CancellationToken ct)
    {
        // Made BEFORE the process starts, so there is no window in which a child could be started
        // outside it. Null on a platform without job objects; the process then runs as it always
        // did rather than not at all.
        using var job = OperatingSystem.IsWindows() ? ProcessJob.Create() : null;

        // Waiting on the PROCESS, not on its streams - and this is the difference between a tool
        // that returns and one that hangs.
        //
        // Process.WaitForExitAsync waits for the redirected output to reach end-of-file as well as
        // for the process to exit. A process the command STARTED inherits those pipe handles, so
        // the pipe stays open for as long as that process lives. `start "" "TOTALCMD64.EXE"` exits
        // in milliseconds; Total Commander then held the pipe, the tool sat there for its full
        // 60-second timeout, reported a timeout - and, with the job terminating on timeout, killed
        // the application it had just been asked to launch. Reported from a real run, 2026-09-10.
        //
        // Exited fires when the process is gone, whoever is holding what.
        process.EnableRaisingEvents = true;
        var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.Exited += (_, _) => exited.TrySetResult();

        // Null arrives on each stream at end-of-file. Used to tell "the output is complete" from
        // "somebody is still holding the pipe", which decides whether to say so.
        var stdoutDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        process.OutputDataReceived += (_, e) => { if (e.Data is null) stdoutDone.TrySetResult(); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is null) stderrDone.TrySetResult(); };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // After Start, because a job needs a process to hold. The gap is this thread's next few
        // instructions: a child started inside it escapes containment but is still reached by the
        // kill below, which is the behaviour that existed before this and is not made worse.
        job?.Assign(process);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await exited.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            // Both, and in this order. The job is the exact set and kills it at once; the tree kill
            // is what happens when there is no job, and costs nothing when there is.
            job?.Terminate();
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { /* ignore */ }
            return new RunOutcome(Completed: false, OutputCutShort: false);
        }

        // The process is gone; its last lines may still be in flight. Bounded, because the pipe may
        // never close: waiting for it is exactly the bug above.
        await Task.WhenAny(
            Task.WhenAll(stdoutDone.Task, stderrDone.Task),
            Task.Delay(OutputGrace, CancellationToken.None));

        // Asked of the two streams themselves rather than of which task WhenAny handed back. The
        // grace timer winning the race does not mean output was lost - both streams may have
        // finished in the same instant - and the streams are the thing the answer is about.
        var complete = stdoutDone.Task.IsCompleted && stderrDone.Task.IsCompleted;
        return new RunOutcome(Completed: true, OutputCutShort: !complete);
    }

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

        RunOutcome outcome;
        try
        {
            outcome = await RunContainedAsync(process, timeoutSeconds, ct);
            if (!outcome.Completed)
                return ToolResults.Fail($"{fileName} timed out after {timeoutSeconds}s or was cancelled.");
        }
        catch (Exception ex)
        {
            // Most commonly: the binary is not installed / not on PATH.
            return ToolResults.Fail($"Could not run {fileName}: {ex.Message}");
        }

        return BuildResult(
            fileName, process.ExitCode, stdout.ToString(), stderr.ToString(), allowedExitCodes,
            outputCutShort: outcome.OutputCutShort);
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
