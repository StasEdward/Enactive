namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Xunit;

/// <summary>
/// Reported with a log, 2026-09-07 20:16: "новый темплейт - новые проблемы."
///
/// <para>A three-step run: analyze coverage, write the missing tests, verify they fail when the
/// behaviour is broken. Step 1, "Analyze test coverage and identify gaps", made eleven calls — eight
/// listings and three reads (the test project's Program.cs, ConditionsParser.cs,
/// SubscriptionManager.cs) — and produced a specific, source-grounded analysis naming the parser's
/// untested date-range fallback, its year-wrap logic, the countdown formats, and three
/// SubscriptionManager gaps. Every claim pointed at a file the evidence shows it read.</para>
///
/// <para>The reviewer failed it:</para>
///
/// <para><c>"The step reported identifying several gaps in test coverage, but the tool execution
/// evidence does not show any commands being run to analyze test coverage or identify gaps. The
/// evidence only lists directory and file listing commands, but no actual analysis or test coverage
/// commands were executed."</c></para>
///
/// <para>Nothing required a command. The reviewer inferred the requirement from the step's TITLE,
/// which is what it had been told to do: <i>"Fail if the REQUIRED command was never actually run"</i>
/// left it to decide what a step required, and "Analyze" reads like a tool to a model that has not
/// been told otherwise.</para>
///
/// <para>The damage is the retry. Told it should have run something, the agent ran
/// <c>dotnet test</c> and <c>dotnet run --project tests/ParserSmokeTest</c> — not to learn anything,
/// but to satisfy the reviewer — and the second exited 1 because the smoke test's own gap tests
/// fail. That non-zero exit left the step Incomplete, both remaining steps were skipped, and the
/// run died without a single test being written.</para>
///
/// <para>So: <b>judge the answer, not the tools it was reached with.</b> A command matters when the
/// report leans on it — a build that succeeded, a test that passed, a printed value. An analysis
/// drawn from files the evidence shows were read is supported however few commands it ran.</para>
///
/// <para><b>What these tests are.</b> They pin the INSTRUCTION, not the behaviour: whether a given
/// model then judges correctly cannot be asserted without that model. The wording is the whole fix,
/// so the wording is what is nailed down — and it is nailed down in both places, because the system
/// prompt and the per-review prompt each carried the old rule and a prompt that contradicts itself
/// is worse than either half.</para>
/// </summary>
public sealed class ReviewScopeTests
{
    private static string System => Reviewer.ExecutionSystemPrompt;

    private static string User => Reviewer.BuildExecutionUserPrompt(
        "Analyze test coverage and identify gaps",
        "I have analyzed the current test coverage and identified several gaps.",
        "11 tool call(s) in this step, oldest first.\nlist_dir {\"path\":\".\"}\n"
        + "read_file {\"path\":\"tests/ParserSmokeTest/Program.cs\"}",
        Array.Empty<string>());

    // ── the rule that was missing ───────────────────────────────────────────

    /// <summary>The tools are the agent's choice, and the reviewer is told so in both prompts.</summary>
    [Fact]
    public void The_reviewer_is_told_the_tools_are_not_its_business()
    {
        Assert.Contains("agent's to choose", System, StringComparison.Ordinal);
        Assert.Contains("reading IS the work", User, StringComparison.Ordinal);
    }

    /// <summary>
    /// And that a step which ran no command is not deficient for it. This is the exact inference
    /// that was made — "no actual analysis or test coverage commands were executed" — stated as a
    /// non-finding.
    /// </summary>
    [Fact]
    public void Running_no_command_is_stated_as_not_a_finding()
    {
        Assert.Contains("never deficient merely for not having run one", System, StringComparison.Ordinal);
        Assert.Contains("has not failed for that", User, StringComparison.Ordinal);
    }

    /// <summary>
    /// What replaces it: does the answer stand up? That is checkable from the evidence, where "was
    /// a command required" is not.
    /// </summary>
    [Fact]
    public void The_question_is_whether_the_answer_is_supported()
    {
        Assert.Contains("THE ANSWER IS SUPPORTED", System, StringComparison.Ordinal);
        Assert.Contains("judge the ANSWER", User, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wording that caused it is gone from BOTH prompts. Leaving it in one while correcting the
    /// other gives the model two rules and lets it pick.
    /// </summary>
    [Fact]
    public void The_wording_that_invited_the_inference_is_gone()
    {
        Assert.DoesNotContain("the required command was never actually run", System, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("the step required running a command", User, StringComparison.OrdinalIgnoreCase);
    }

    // ── what must NOT change ────────────────────────────────────────────────

    /// <summary>
    /// The reviewer still fails a claim nothing backs. Narrowing what counts as a finding must not
    /// remove the finding this whole component exists for: a model reporting a command's result
    /// without having run the command.
    /// </summary>
    [Fact]
    public void A_claim_that_needed_a_command_is_still_a_failure()
    {
        Assert.Contains("leans on a command that was never run", System, StringComparison.Ordinal);
        Assert.Contains("leans on a command that is not in the evidence", User, StringComparison.Ordinal);
        Assert.Contains("build", User, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A command that ran and failed is still a finding.</summary>
    [Fact]
    public void A_command_that_ran_and_failed_is_still_a_failure()
    {
        Assert.Contains("a command it did run failed", System, StringComparison.Ordinal);
        Assert.Contains("non-zero exit", User, StringComparison.Ordinal);
    }

    /// <summary>And a fabricated value is still a finding — the oldest rule here.</summary>
    [Fact]
    public void A_fabricated_value_is_still_a_failure()
    {
        Assert.Contains("fabricated", System, StringComparison.Ordinal);
        Assert.Contains("fabricated", User, StringComparison.Ordinal);
    }

    /// <summary>
    /// The §8i clause survives untouched: a shortened result is not a missing call. It was added
    /// four hours before this one and is the other half of "judge by what is actually there".
    /// </summary>
    [Fact]
    public void The_shortened_result_clause_is_still_there()
    {
        Assert.Contains("EVERY call", System, StringComparison.Ordinal);
        Assert.Contains("shortened result is still a call that HAPPENED", System, StringComparison.Ordinal);
        Assert.Contains("Judge by the calls listed", System, StringComparison.Ordinal);
    }

    /// <summary>
    /// The evidence still opens where <c>RetryEvidenceTests</c> looks for it. Two files now depend
    /// on that sentence, and rewording the prompt around it must not move it.
    /// </summary>
    [Fact]
    public void The_evidence_section_is_still_where_the_other_tests_find_it()
        => Assert.Contains("the agent's own words above may be wrong or invented):", User,
                           StringComparison.Ordinal);
}
