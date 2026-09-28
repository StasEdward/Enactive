namespace Enactive.Agents;

using System.Text;
using System.Text.Json;
using Enactive.Core.Chat;
using Enactive.Core.Execution;
using Enactive.Core.Providers;

/// <summary>
/// What a proof pass answered, and what asking cost. The tokens are carried for the same reason the
/// verdict's are: this is a second call on the Review model, and a cost that is not counted is a
/// cost that gets attributed to nothing.
/// </summary>
public sealed record ProofOutcome(ProofClaim Claim, int PromptTokens = 0, int CompletionTokens = 0)
{
    /// <summary>The cached share of <see cref="PromptTokens"/>, or null where nobody counted.</summary>
    public int? CachedPromptTokens { get; init; }
    public int? CacheCreationPromptTokens { get; init; }
}

/// <summary>
/// Reviewer verdict for a step, plus what asking cost.
///
/// <para>The review call sits outside the tool loop, so its tokens were never counted; on a run that
/// reviews content, the reviewer reads whole documents on the most expensive model bound, which made
/// the uncounted share the LARGEST part of some runs. The counts cover the re-ask too, when there
/// was one - two calls were made, and two calls were paid for.</para>
/// </summary>
public sealed record ReviewResult(
    bool Pass, string Notes, int PromptTokens = 0, int CompletionTokens = 0)
{
    public ProofVerdict? Soundness { get; init; }
    public IReadOnlyList<ObligationClaim>? Obligations { get; init; }
    public string? BudgetExhausted { get; init; }
    public string? IncompleteReason { get; init; }

    /// <summary>
    /// The reviewer could not return a usable verdict: an error, a response refused after
    /// clarification, no verdict at all, or its own statement that it could not tell. Set only where
    /// that is what happened, and false by default, so any path not marked keeps its old outcome.
    ///
    /// <para>Distinct from <see cref="IncompleteReason"/>, which is also set when the reviewer DID
    /// reach a verdict that ends the step - a prohibition the work violated. That work must not be
    /// built on, and this flag is what keeps the two apart: a step whose verdict is missing becomes
    /// <see cref="Enactive.Core.Events.StepOutcomeKind.DoneUnverified"/> and releases its dependents;
    /// a step with a damning verdict does not.</para>
    /// </summary>
    public bool VerdictUnavailable { get; init; }
    /// <summary>Concrete semantic defects, distinct from malformed review or unavailable evidence.</summary>
    public string? RepairAdvice { get; init; }

    /// <summary>
    /// The reviewer judged what the step PRODUCED to be right - implementation: pass - and named no
    /// file of it to correct; the failure it found is somewhere else: the report, the process, a
    /// command run the wrong way. A step rejected on such a review keeps its files.
    ///
    /// <para><b>Measured 2026-09-28, run 3fe4f8.</b> The final review of step 1 said of the seven
    /// tests it wrote "implementation: pass ... 7/7 pass", and failed the step for how it had run its
    /// commands and what its report left out. The step was rejected, and the engine put the test file
    /// back: the only thing the review had found right was the thing that was thrown away. False by
    /// default, so every review that does not say this keeps the revert it always had.</para>
    /// </summary>
    public bool WorkStands { get; init; }
    /// <summary>
    /// The cached share of <see cref="PromptTokens"/>, or null where nobody counted. This is the
    /// phase most likely to have one on a real machine: review is bound to a cloud model, and a
    /// re-ask re-sends the same prefix it just sent.
    /// </summary>
    public int? CachedPromptTokens { get; init; }
    public int? CacheCreationPromptTokens { get; init; }
}

/// <summary>One file the step wrote, as the reviewer needs to see it.</summary>
/// <param name="Content">
/// What the reviewer is shown. May be an excerpt.
/// </param>
/// <param name="TotalChars">
/// How long the file ACTUALLY is, however much of it <paramref name="Content"/> carries.
///
/// <para>Without this the prompt could not tell whether it was looking at a whole file. It measured
/// truncation by comparing its own slice against the string it had been handed - and the caller had
/// already cut that string to the same 8000 characters, so the comparison was 8000 &lt; 8000 and the
/// "showing the first N of M" line never appeared. On 2026-09-07 a 414-line page was reviewed as its
/// first 161 lines, ending mid-&lt;article&gt;, with nothing saying so. The reviewer is instructed to
/// fail on "a truncated line", so it did - and then, asked to name the offending lines, invented a
/// specific one: a &lt;div&gt; supposedly closed with "&lt;/div" at line 43. Nothing was wrong with
/// the file. The worker spent the rest of the run chasing that phantom until the stall detector
/// stopped it.</para>
///
/// <para>Two caps in two places, equal by coincidence, and the honesty check of the second was
/// defeated by the silence of the first. The size travels with the content now, so the notice fires
/// whoever did the cutting.</para>
/// </param>
/// <param name="SharedWithAnotherStep">
/// Another step, running at the same time, also wrote this file. What is shown is therefore the
/// two of them together and cannot be attributed — see
/// <see cref="Enactive.Core.Artifacts.IArtifactScope.SharedWithAnotherStep"/>.
/// </param>
public sealed record WrittenFile(
    string RelativePath, string Content, int TotalChars, bool SharedWithAnotherStep = false,
    // What Content IS, when it is not simply the file: this step's CHANGES as a diff, a new file
    // whole, a deletion, or the file as it is now with no record of before. Null is the file itself,
    // which is what every caller meant until what a step changed could be measured (2026-09-24).
    string? Heading = null)
{
    public WrittenFile(string relativePath, string content)
        : this(relativePath, content, content.Length) { }

    /// <summary>Whether the reviewer is being shown less than the whole file.</summary>
    public bool IsExcerpt => Content.Length < TotalChars;
}

/// <summary>
/// What a step should be judged ON.
///
/// A step that RAN something can be checked against reality: the command either executed or it did
/// not, and its exit code is not a matter of opinion. A step that only WROTE something cannot —
/// there is no failing exit code in a document, so execution review has nothing to look at and
/// passes anything at all. That is not a hypothetical: on 2026-09-06 a local model produced a
/// cluster guide full of invented package names, invented pcs syntax, wrong Corosync ports and
/// stray CJK characters inside a resource identifier, and the run finished green because the write
/// itself had succeeded.
/// </summary>
public enum ReviewMode
{
    /// <summary>Did the commands actually run, and did they succeed? The original review.</summary>
    Execution,

    /// <summary>Is what was written actually true? For steps whose only output is content.</summary>
    Content
}

/// <summary>
/// The reasoning agent reviewing a coding step (PLAN_v2 multi-agent: Reasoner reviews the Coder's work).
/// One LLM call → PASS/FAIL + short notes, in one of two modes (see <see cref="ReviewMode"/>).
///
/// It fails CLOSED. An answer that carries no verdict earns exactly one re-ask with a stricter
/// instruction; a second unparseable answer is a FAIL, not a pass. "The reviewer could not tell us"
/// and "the reviewer approved" are different facts, and treating the first as the second is how a
/// gate ends up enforcing nothing while looking configured.
/// </summary>
public sealed partial class Reviewer : IReviewer
{
    /// <summary>Per file, and in total — a reviewer prompt is not the place to send a whole repository.</summary>
    private const int MaxContentCharsPerFile = 8000;
    private const int MaxContentCharsTotal = 20000;

    public async Task<ReviewResult> ReviewAsync(
        string stepTitle, string coderOutput, string executionEvidence, IReadOnlyList<string> artifacts,
        IChatProvider provider, string model, CancellationToken ct,
        ReviewMode mode = ReviewMode.Execution,
        IReadOnlyList<WrittenFile>? writtenFiles = null,
        // The user's own request, verbatim, when the caller has it. A step's TITLE is the planner's
        // paraphrase of a piece of this - "Write new tests in existing style" - and a constraint the
        // request stated explicitly ("run the tests with THAT command and no other", "each test must
        // FAIL if its behaviour is broken") does not survive being paraphrased into a title. Measured
        // 2026-09-24, run 4f779e: the request named one exact test command; the step ran 22 different
        // `dotnet test --filter …` invocations instead, and execution review passed it - it had
        // nothing to check that instruction against, because nothing here had ever been given it.
        string? request = null, RequestObligations? obligations = null)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(mode == ReviewMode.Content ? ContentSystemPrompt : ExecutionSystemPrompt),
            ChatMessage.User(mode == ReviewMode.Content
                ? BuildContentUserPrompt(stepTitle, coderOutput, writtenFiles ?? Array.Empty<WrittenFile>(), request)
                : BuildExecutionUserPrompt(stepTitle, coderOutput, executionEvidence, artifacts, writtenFiles, request))
        };

        if (obligations is not null)
            messages[1] = ChatMessage.User((mode == ReviewMode.Content
                ? BuildContentUserPrompt(stepTitle, coderOutput, writtenFiles ?? Array.Empty<WrittenFile>())
                : BuildExecutionUserPrompt(stepTitle, coderOutput, executionEvidence, artifacts, writtenFiles)) + obligations.Describe());

        var completion = await provider.CompleteAsync(
            new ChatRequest(model, messages, Temperature: 0.0, ResponseSchema: VerdictSchema), ct);
        var answer = completion.Message.Content ?? "";

        var prompt = completion.PromptTokens ?? 0;
        var output = completion.CompletionTokens ?? 0;
        var cached = completion.CachedPromptTokens;
        var created = completion.CacheCreationPromptTokens;

        if (Parse(answer) is { } verdict)
            return verdict with
            {
                PromptTokens = prompt, CompletionTokens = output, CachedPromptTokens = cached, CacheCreationPromptTokens = created
            };

        // No verdict in there. Ask once more, showing what came back and what was wanted, because a
        // model that wandered off format usually recovers when told exactly what shape to produce.
        messages.Add(new ChatMessage(ChatRole.Assistant, answer, null));
        messages.Add(ChatMessage.User(
            "That reply did not contain a verdict. Reply with NOTHING but a single JSON object, no prose, "
            + "no code fences, no explanation before or after it, in exactly this shape:\n"
            + "{\"verdict\":\"pass\",\"notes\":\"...\"}\n"
            + "or\n"
            + "{\"verdict\":\"fail\",\"notes\":\"...\"}"));

        var retry = await provider.CompleteAsync(
            new ChatRequest(model, messages, Temperature: 0.0, ResponseSchema: VerdictSchema), ct);

        prompt += retry.PromptTokens ?? 0;
        output += retry.CompletionTokens ?? 0;
        // Two calls with the same prefix is the shape caching exists for, so the re-ask is exactly
        // where a cache read shows up. Added through TokenCounts, never with ?? 0: a retry that
        // reports 400 on top of a first call that reported nothing is 400, and two silences are
        // still silence.
        cached = TokenCounts.Add(cached, retry.CachedPromptTokens);
        created = TokenCounts.Add(created, retry.CacheCreationPromptTokens);

        if (Parse(retry.Message.Content ?? "") is { } retried)
            return retried with
            {
                PromptTokens = prompt, CompletionTokens = output, CachedPromptTokens = cached, CacheCreationPromptTokens = created
            };

        // Twice with no verdict. A reviewer that cannot answer has not approved anything.
        return new ReviewResult(false,
            "the reviewer did not return a verdict, twice — treating the step as not reviewed",
            prompt, output) { CachedPromptTokens = cached, CacheCreationPromptTokens = created, IncompleteReason = "the reviewer did not return a verdict after clarification", VerdictUnavailable = true };
    }

    /// <summary>
    /// The shape a verdict takes, offered to any provider that can hold a model to it
    /// (<c>FIX_PLAN.md</c> §9c). Two fields, both required, one of them an enumeration of two words.
    ///
    /// <para>The reviewer is where this was worth doing FIRST and the planner is not: there is
    /// nothing here to reason about, so constrained decoding cannot eat the thinking - which on a
    /// 12-14B model is exactly what it does to a plan. Measure before the planner, if ever.</para>
    ///
    /// <para>It changes NOTHING about how the answer is judged. <see cref="Parse"/> runs on the
    /// reply exactly as before, the re-ask below still happens when it comes back unreadable, and
    /// two unreadable answers still fail closed. A schema makes the bad path rarer; it must never
    /// become the thing correctness rests on, because a provider may ignore it, a gateway may not
    /// support it, and a well-formed claim is still only a claim.</para>
    /// </summary>
    internal const string VerdictSchema = """
        {
          "type": "object",
          "properties": {
            "verdict": { "type": "string", "enum": ["pass", "fail"] },
            "notes": { "type": "string" }
          },
          "required": ["verdict", "notes"],
          "additionalProperties": false
        }
        """;

    /// <summary>
    /// The user's own request, quoted for the reviewer with the instruction to check it directly -
    /// not through the step's title, which is the planner's paraphrase of a piece of it. Shared
    /// between execution and content review because the failure is the same in both: a plan can
    /// satisfy its own restated goal while missing something the request said outright.
    /// </summary>
    private static string RequestBlock(string? request)
    {
        if (string.IsNullOrWhiteSpace(request))
            return "";

        return "\nThe user's ORIGINAL REQUEST for this run, verbatim - the step's title above is the "
             + "planner's paraphrase of a PIECE of this, and a specific instruction in it (an exact "
             + "command to use and no other, a naming or format rule, a check required of EVERY item "
             + "produced) does not survive being paraphrased into a title:\n"
             + RequestObligations.Create(request).Describe()
             + "If the request states such an instruction, check the evidence against THAT instruction "
             + "specifically, in addition to judging the report on its own terms. Only the step's own "
             + "share of the request is this step's to satisfy - a constraint about the run's LAST step "
             + "is not a finding against an earlier one.\n";
    }

    internal static string BuildExecutionUserPrompt(
        string stepTitle, string coderOutput, string executionEvidence, IReadOnlyList<string> artifacts,
        IReadOnlyList<WrittenFile>? changes = null, string? request = null, ReviewSources? sources = null)
    {
        var files = artifacts.Count == 0 ? "(none)" : string.Join(", ", artifacts);

        // What the step CHANGED, whichever tool changed it. A step that runs commands AND writes -
        // a disk check that saves a report, an analysis that writes its findings - used to get only
        // this review, and what it wrote was checked against nothing but the tool arguments, and not
        // at all when a command wrote it. See WorkspaceChanges.
        var changed = "";
        if (changes is { Count: > 0 })
        {
            var sb = new StringBuilder();
            // "While this step ran", not "by this step": the comparison measures the workspace, and
            // something outside the run can change it too (run 3fe4f8, 2026-09-28). Each file says
            // what the journal shows about who changed it.
            sb.AppendLine().AppendLine("What CHANGED in the workspace while this step ran - by any tool, commands "
                                       + "included, or by something outside the run; each file says whether this "
                                       + "step's calls account for it (this is ground truth for what is on disk):");
            AppendFiles(sb, changes, sources);
            changed = sb.ToString();
        }

        return $"Step: {stepTitle}\n\n"
             + RequestBlock(request)
             + $"What the coding agent reported (worker message, not a file):\n{sources?.RenderReport() ?? coderOutput}\n\n"
             + $"Tool execution evidence — the ACTUAL commands run and their real stdout/stderr/exit codes "
             + $"(this is the ground truth; the agent's own words above may be wrong or invented):\n{executionEvidence}\n\n"
             + $"Files changed: {files}\n"
             + changed + "\n"
             + "Judge ONLY from the evidence, and judge the ANSWER — not which tools it was reached with. "
             + "FAIL if: the report above leans on a command that is not in the evidence (it claims a build "
             + "succeeded, a test passed, a value was printed); a command that DID run failed (non-zero exit "
             + "or an error in its output) and the report does not account for it; or the reported/saved result "
             + "is a placeholder, or a value that CONTRADICTS what a result you can see actually says. A value "
             + "you simply cannot find is not one of these: the outputs above are excerpts and each says where "
             + "it was cut, so a quote from a part that was cut is unverified, not fabricated, and is not a "
             + "reason to fail. A step that only read and listed has not failed for that — for an analysis "
             + "step, reading IS the work. Otherwise PASS.";
    }

    internal static string BuildContentUserPrompt(
        string stepTitle, string coderOutput, IReadOnlyList<WrittenFile> writtenFiles, string? request = null)
    {
        var sb = new StringBuilder();
        sb.Append("Step: ").AppendLine(stepTitle).AppendLine();
        sb.Append(RequestBlock(request));
        sb.AppendLine("What the coding agent reported:").AppendLine(coderOutput).AppendLine();

        if (writtenFiles.Count == 0)
        {
            sb.AppendLine("The step wrote no content at all.");
            return sb.ToString();
        }

        // "Exactly what it wrote" stopped being true the moment two steps could run at once, and
        // said so anyway. Measured 2026-09-21: two steps with dependsOn [] made 21 and 41 edits to
        // one document, and the second to finish was reviewed on a file the first had been editing.
        // Both passed. A reviewer told what it is looking at can say it cannot attribute this; one
        // told "exactly what it wrote" answers the question it was asked.
        var shared = writtenFiles.Any(f => f.SharedWithAnotherStep);

        var asChanges = writtenFiles.Any(f => f.Heading is not null);

        sb.AppendLine(shared
            ? "This step ran no commands — it produced text. Here is what it wrote, EXCEPT where "
              + "marked: a file marked below was also being written by another step at the same "
              + "time, so what you see is both of them and cannot be told apart."
            : asChanges
            ? "This step ran no commands — it produced text. Here is what it CHANGED, file by file:"
            : "This step ran no commands — it produced text. Here is exactly what it wrote:");

        AppendFiles(sb, writtenFiles);
        return sb.ToString();
    }

    /// <summary>
    /// The files, each under its own header, within the review budget - one renderer for both
    /// reviews, so a file reads the same whichever question is being asked of it.
    /// </summary>
    private static void AppendFiles(StringBuilder sb, IReadOnlyList<WrittenFile> writtenFiles, ReviewSources? sources = null)
    {
        var budget = MaxContentCharsTotal;
        foreach (var file in writtenFiles)
        {
            sb.AppendLine().Append("----- ").Append(file.RelativePath)
              .Append(file.SharedWithAnotherStep ? "  (ALSO WRITTEN BY ANOTHER STEP)" : "")
              .AppendLine(" -----");
            if (file.Heading is { Length: > 0 } heading)
                sb.AppendLine(heading);

            // The START and the END, not the first N characters - the same rule as a command's
            // output, and here for a sharper reason. Measured 2026-09-23 on the run that finished:
            // a step appends its findings to one growing report, so the work of steps 2, 3 and 4 is
            // always at the END, and a head-only excerpt showed the reviewer the same opening every
            // time. Its own notes say so, under a PASS:
            //
            //   [2] PASS: "Only the first 8000 characters were shown. They cover the pages 1-3 …"
            //   [4] PASS: "The excerpt covers pages 1-2 … and ends mid-line"
            //
            // Step 4 was reviewing pages 10-12. It passed on an excerpt that could not contain
            // them, and passed honestly: ContentSystemPrompt tells it to judge only what the
            // excerpt holds. The reviewer was not wrong; it was shown the wrong 8000 characters.
            //
            // This is 9q again ("the verdict was at the end, and shortening kept the beginning"),
            // which was fixed for command output and never carried across to the file a reviewer
            // reads.
            var slice = Shortening.ToFit(file.Content, Math.Min(MaxContentCharsPerFile, Math.Max(0, budget)));
            var rendered = sources?.AddFile(file.RelativePath, slice) ?? slice;

            budget -= slice.Length;
            sb.AppendLine(rendered);

            // Against the file's REAL size, not against the string handed to us - which the caller
            // may already have cut to exactly this budget, making the comparison always false.
            // The label has to describe the slice that was actually taken. It said "the first N
            // characters" for a day after the slice became head-AND-tail, and the reviewer believed
            // the label over the text in front of it - its own notes on 2026-09-24 read "Only the
            // first 8000 of 28893 characters are shown, and they cover pages 1-4. The page 10-12
            // sections this step reports appending are in the unseen part", while the page 10-12
            // sections were in the prompt, at the bottom, where the tail had put them.
            //
            // A wrong label is worse than the head-only cut it replaced: that one was at least
            // honest about what it had left out.
            if (slice.Length < file.TotalChars)
                sb.AppendLine()
                  .AppendLine($"----- END OF EXCERPT: what is shown above for {file.RelativePath} is "
                            + $"the START and the END of {(file.Heading is null ? "the file" : "it")} - "
                            + $"{slice.Length} characters of {file.TotalChars}, "
                            + "with the middle left out and marked where it was cut. What is missing "
                            + "is the MIDDLE; the end of the file IS above. Nothing here is missing "
                            + "from the file itself. -----");

            if (budget <= 0)
            {
                sb.AppendLine().AppendLine("… (further files omitted — the review budget was reached)");
                break;
            }
        }
    }

    /// <summary>
    /// The second question, asked of a step whose report the reviewer has already found TRUE:
    /// what in the evidence SHOWS the step's objective was met?
    ///
    /// <para>A different question from the first, and the one nobody was asking. Every fact in a
    /// report can be in the evidence while the conclusion follows from none of it — a test
    /// "targeted" that was already failing and stayed failing, reported honestly, passed. Truth is
    /// not soundness.</para>
    ///
    /// <para>It asks for a POINTER rather than an opinion: the numbers of the calls that show it.
    /// A number can be looked up, and <see cref="ProofAudit"/> looks it up against the journal
    /// rather than believing it. That is what makes this different from adding a fifth clause to a
    /// prompt whose fourth was already one too many.</para>
    ///
    /// <para>Fails CLOSED, like the verdict above and for the same reason: two unreadable answers
    /// are "the pass could not tell us", which is not "the step is proven".</para>
    /// </summary>
    public async Task<ProofOutcome> ProveAsync(
        string stepTitle, string coderOutput, string executionEvidence,
        IChatProvider provider, string model, CancellationToken ct, string? request = null)
    {
        var messages = new List<ChatMessage>
        {
            ChatMessage.System(ProofSystemPrompt),
            ChatMessage.User(BuildProofUserPrompt(stepTitle, coderOutput, executionEvidence) + RequestBlock(request))
        };

        var completion = await provider.CompleteAsync(
            new ChatRequest(model, messages, Temperature: 0.0, ResponseSchema: ProofSchema), ct);
        var answer = completion.Message.Content ?? "";

        var prompt = completion.PromptTokens ?? 0;
        var output = completion.CompletionTokens ?? 0;
        var cached = completion.CachedPromptTokens;
        var created = completion.CacheCreationPromptTokens;

        if (ParseProof(answer) is { } claim)
            return new ProofOutcome(claim, prompt, output) { CachedPromptTokens = cached, CacheCreationPromptTokens = created };

        messages.Add(new ChatMessage(ChatRole.Assistant, answer, null));
        messages.Add(ChatMessage.User(
            "That reply did not contain an answer. Reply with NOTHING but a single JSON object, no prose, "
            + "no code fences, in exactly this shape:\n"
            + "{\"shown\":\"yes\",\"calls\":[3],\"what\":\"...\"}\n"
            + "or {\"shown\":\"no\",\"calls\":[],\"what\":\"...\"}\n"
            + "or {\"shown\":\"not-by-any-call\",\"calls\":[],\"what\":\"...\"}\n"
            + "or {\"shown\":\"nothing-to-do\",\"calls\":[2],\"what\":\"...\"}"));

        var retry = await provider.CompleteAsync(
            new ChatRequest(model, messages, Temperature: 0.0, ResponseSchema: ProofSchema), ct);

        prompt += retry.PromptTokens ?? 0;
        output += retry.CompletionTokens ?? 0;
        cached = TokenCounts.Add(cached, retry.CachedPromptTokens);
        created = TokenCounts.Add(created, retry.CacheCreationPromptTokens);

        if (ParseProof(retry.Message.Content ?? "") is { } retried)
            return new ProofOutcome(retried, prompt, output) { CachedPromptTokens = cached, CacheCreationPromptTokens = created };

        // Twice with nothing usable. Not proven — the same rule as the verdict, because "we could
        // not find out" and "it is fine" are different facts.
        return new ProofOutcome(
            new ProofClaim(ProofClaimKind.NotShown, Array.Empty<int>(),
                           "the proof pass did not answer, twice"),
            prompt, output) { CachedPromptTokens = cached, CacheCreationPromptTokens = created };
    }

    /// <summary>The shape of a proof answer, for a provider that can hold a model to one.</summary>
    internal const string ProofSchema = """
        {
          "type": "object",
          "properties": {
            "shown": { "type": "string", "enum": ["yes", "no", "not-by-any-call", "nothing-to-do", "expected-failure"] },
            "calls": { "type": "array", "items": { "type": "integer" } },
            "what": { "type": "string" }
          },
          "required": ["shown", "calls", "what"],
          "additionalProperties": false
        }
        """;

    internal static string BuildProofUserPrompt(
        string stepTitle, string coderOutput, string executionEvidence)
        => $"The step's objective:\n{stepTitle}\n\n"
         + $"What the agent reported:\n{coderOutput}\n\n"
         + $"Every call the step made, numbered:\n{executionEvidence}\n\n"
         + "Which of these calls SHOWS that the objective above was met — or, if the objective was "
         + "conditional, that it did not need doing? Answer with their numbers.";

    /// <summary>
    /// Deliberately short. This pass has one question and a long prompt would invite it to answer a
    /// different one — which is how the execution reviewer came to fail steps for not running
    /// commands nobody asked for.
    /// </summary>
    internal const string ProofSystemPrompt =
        "You are checking whether a step's reported success FOLLOWS from what the step actually did. "
        + "Another reviewer has already confirmed that the report is truthful about the evidence; "
        + "that is not your question. Yours is narrower: does the evidence SHOW the objective was met?\n\n"
        + "Respond with ONLY a JSON object, no prose and no code fences:\n"
        + "{\"shown\":\"yes\"|\"no\"|\"not-by-any-call\"|\"nothing-to-do\"|\"expected-failure\",\"calls\":[numbers],"
        + "\"what\":\"one sentence\"}\n\n" + ProofGuidance;

    internal const string ProofGuidance =
        "\"yes\" — the evidence shows it. Put in \"calls\" the number of EVERY call that shows it, "
        + "by its call number - the [n] that begins the call - and nothing else. Only cite a call you can actually see. A "
        + "citation is checked against what really happened, so a number you are unsure of is worse "
        + "than one fewer number.\n\n"
        + "\"no\" — a call could have shown it, and none of these does. Use this when the report draws "
        + "a conclusion the calls do not support: a fix reported over a test that still fails, a "
        + "problem called solved by calls that only looked at it, a claim that the calls are merely "
        + "consistent with rather than evidence for. Say which conclusion is unsupported in \"what\".\n\n"
        + "\"not-by-any-call\" — the objective is not the kind of thing a tool call settles: reading, "
        + "analysing, deciding, explaining, or writing a document. WHICH TOOLS a step uses are the "
        + "agent's to choose, and many correct steps run no command at all. Use this answer freely; "
        + "it is never held against the step. Never answer \"no\" merely because you would have "
        + "expected some command to be run — that is this answer, not that one.\n\n"
        + "\"nothing-to-do\" — the objective was CONDITIONAL (correct what has drifted, fix it if it "
        + "is broken, update the file if it is out of date) and the calls show the condition does "
        + "not hold, so there was nothing to do. The step is finished and correct. Put in \"calls\" "
        + "the number of every call that SHOWS there was nothing to do, exactly as for \"yes\" — an "
        + "answer that names none is not believed, because \"nothing needed doing\" is a finding and "
        + "a finding rests on having looked. This is not the answer for a step that simply did not "
        + "do its work: use it only when the calls themselves say the work was not needed.\n\n"
        + "\"expected-failure\" — ONLY for an explicitly requested negative test (including a mutation check). "
        + "Cite the commands with a recorded nonzero exit and explain the requested negative behavior and "
        + "matching failure in what. Missing expectedExitCodes does not require rerunning an observed negative test. "
        + "A timeout, refused command, compilation error or unrelated test failure cannot demonstrate the intended "
        + "mutation. Assess restoration and passing regression tests separately. The command's original outcome "
        + "remains unchanged.\n\n"
        + "A call marked ERROR cannot support yes; a REFUSED call never proves execution. "
        + "A call marked NOTHING THERE ran and answered — a file that is absent, an offset "
        + "past the end — and can be exactly what shows one.\n\n"
        // The evidence header already says a long RESULT keeps its start and end. What it does not
        // say, and what only this pass needs, is what to do about it when your answer is a list of
        // call numbers: the engine can also leave whole calls out, and the older ones are the first
        // to go. ExecutionJournal.HeadAndTail records the incident that taught this on the other
        // pass - a reviewer read "1,645 characters cut from the middle" as proof the file did not
        // contain what was quoted, and failed the step twice for a value inside those characters -
        // and says the rule "belongs in the reviewer's instructions, where it is said ONCE". It was
        // said once, in the execution reviewer's, and this pass never got it.
        + "The evidence may be SHORTENED: a long result keeps its start and its end with the cut "
        + "marked between them, and a note may say the oldest calls are not listed at all. Neither "
        + "is an absence. Cite a call whose result was cut exactly as you would any other, and "
        + "never answer \"no\" because what would have shown it is in a part you were not shown.";

    /// <summary>The claim, or null when the answer carried none.</summary>
    internal static ProofClaim? ParseProof(string text)
    {
        var json = ModelText.ExtractJsonObject(ModelText.StripThink(text));
        if (json is null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("shown", out var s)
                || s.ValueKind != JsonValueKind.String)
                return null;

            // The word has to be one of the three. Anything else is not an answer, and picking a
            // default here is how a gate comes to enforce nothing.
            var kind = s.GetString()?.Trim().ToLowerInvariant() switch
            {
                "yes" => ProofClaimKind.Shown,
                "expected-failure" => ProofClaimKind.ExpectedFailure,
                "no" => ProofClaimKind.NotShown,
                "not-by-any-call" or "not_by_any_call" or "notbyanycall" => ProofClaimKind.NotByAnyCall,
                "nothing-to-do" or "nothing_to_do" or "nothingtodo" => ProofClaimKind.NothingToDo,
                _ => (ProofClaimKind?)null
            };
            if (kind is not { } claimKind)
                return null;

            var calls = new List<int>();
            if (root.TryGetProperty("calls", out var c) && c.ValueKind == JsonValueKind.Array)
                foreach (var item in c.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var n))
                        calls.Add(n);
                    // A model that answers with the number as it appears in the text - "3", "[3]",
                    // "#3" - has still pointed at a call, and refusing to read it would fail a proof
                    // over its punctuation.
                    else if (item.ValueKind == JsonValueKind.String
                             && int.TryParse(item.GetString()?.Trim().Trim('[', ']', '#'), out var parsed))
                        calls.Add(parsed);

            var what = root.TryGetProperty("what", out var w) && w.ValueKind == JsonValueKind.String
                ? w.GetString() ?? ""
                : "";

            return new ProofClaim(claimKind, calls.Distinct().ToArray(), what);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Returns the verdict, or null when the answer carried none.</summary>
    private static ReviewResult? Parse(string text)
    {
        var json = ModelText.ExtractJsonObject(ModelText.StripThink(text));
        if (json is null)
            return null;

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("verdict", out var v)
                || v.ValueKind != JsonValueKind.String)
                return null;

            var verdict = v.GetString();

            // The verdict has to SAY one of the two things. Anything else — an empty string, "unsure",
            // a sentence — is not a verdict, and defaulting it to pass is the bug this replaces.
            var pass = string.Equals(verdict, "pass", StringComparison.OrdinalIgnoreCase);
            var fail = string.Equals(verdict, "fail", StringComparison.OrdinalIgnoreCase);
            if (!pass && !fail)
                return null;

            var notes = root.TryGetProperty("notes", out var n) && n.ValueKind == JsonValueKind.String
                ? n.GetString() ?? ""
                : "";

            return new ReviewResult(pass, notes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal const string ExecutionSystemPrompt = ExecutionGuidance + VerdictResponseInstruction;
    internal const string VerdictResponseInstruction =
        "Respond with ONLY a JSON object, no prose and no code fences: "
        + "{\"verdict\":\"pass\" or \"fail\",\"notes\":\"short, specific feedback\"}.\n\n";
    internal const string ExecutionGuidance =
        """
        Review the step against real tool evidence and workspace changes, trusting those over the agent's summary.
        Ask whether THE ANSWER IS SUPPORTED. Fail if the report leans on a command that was never run,
        a command it did run failed unexpectedly, or visible evidence contradicts a reported/saved value
        (fabricated or placeholder output). Otherwise pass.
        WHICH TOOLS to use are the agent's to choose within the request: reading/listing can support analysis.
        A step is never deficient merely for not having run one. Claims of builds, tests or measurements
        require the corresponding calls, not assumptions from the step title.

        Tool approval never waives request prohibitions. A ban on deleting files includes restoration
        and scratch cleanup. Do not invent exceptions: correct final files do not excuse a violation.

        Judge by the calls listed; cite a call by its [n], never by an [evidence N] line label.
        The evidence marks omitted calls:
        their outcomes are unknown. A shortened result is still a call that HAPPENED.
        What is omitted is CUT, not absent. Unverified is not fabricated: fail a quote only for a visible
        contradiction, never merely because it is missing from an excerpt. State evidence gaps.

        expectedExitCodes declares expected outcomes: an intentionally failing test is reporting, not malfunctioning.
        Its expected exit code alone is no finding. Flag a declaration that does not fit the command
        (accepting a broken build as success), or a report that contradicts the output.
        A requested mutation's matching failure is evidence even without expectedExitCodes; do not rerun
        just to declare it. Compilation errors or unrelated failures do not prove mutation detection.
        NOTHING THERE is not an error and is not something the report has to account for;
        it means a lookup found nothing. ERROR denotes a failed call.

        Workspace changes include writes through commands. Check saved results/code against these and tool output.
        In diffs, '+' lines are additions, '-' removals, and space-prefixed lines unchanged context.

        """;

    // Deliberately narrow. A content reviewer that fails on anything it is merely unsure about blocks
    // every run and gets switched off, so it is told to fail only on a specific, nameable falsehood —
    // and told explicitly that style, length and completeness are none of its business.
    internal const string ContentSystemPrompt = ContentGuidance + VerdictResponseInstruction;
    internal const string ContentGuidance =
        "You are a senior technical reviewer checking a document another agent just wrote, for FACTUAL "
        + "correctness. The agent may be a small model that invents plausible-looking detail.\n\n"
        + "FAIL when you can point to a SPECIFIC assertion that is wrong or invented, such as:\n"
        + "- a package, tool, command or file that does not exist under that name;\n"
        + "- command syntax, subcommands, flags or configuration keys that are not real;\n"
        + "- wrong port numbers, protocols, paths, API names or version requirements;\n"
        + "- steps that contradict each other, or that cannot work in the stated order;\n"
        + "- text corruption: stray characters from another script or language inside an identifier, "
        + "mojibake, or a truncated line.\n\n"
        + "In notes, name the offending lines so the agent can fix exactly those. Only name something "
        + "you can actually see in the text above; if you cannot point at it, do not report it.\n\n"
        + "Where an excerpt is marked as such, judge ONLY what it contains. An excerpt is the START "
        + "and the END of the file, with a line between them saying how much is not shown, so a "
        + "line that breaks off at "
        + "either cut is the cut and never a defect - and neither is anything you expected to find "
        + "in the part between them.\n\n"
        + "A file may be shown as the CHANGES this step made - a unified diff, where '+' lines were "
        + "added by this step, '-' lines removed, and lines starting with a space are unchanged "
        + "context shown for orientation. Judge what the step added or changed; the context is not "
        + "its work, and parts of the file not shown did not change.\n\n"
        + "Do NOT fail for style, tone, formatting, length, or for being incomplete — a short document is "
        + "not a wrong one. Do NOT fail because you would have written it differently. If you are unsure "
        + "whether something exists, do not fail on it: say so in notes and pass. Judge the content, not "
        + "the effort.";
}
