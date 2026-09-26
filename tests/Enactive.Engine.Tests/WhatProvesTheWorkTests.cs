namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Xunit;

/// <summary>
/// The planner's checks prompt, after an outside review called it over-prompted — 2026-09-23.
///
/// <para><b>The review was right about the principle and wrong about the audience.</b> Current
/// frontier models do need less instruction, and two rules here were each stated twice. But the
/// planner runs on whatever model the person bound, and on the machine this was measured on that
/// includes a 4B. A prompt tuned for Opus that degrades on a small local planner makes the product
/// worse at the one thing it exists for: letting somebody choose.</para>
///
/// <para><b>And the criticism uncovered a real defect it had not noticed.</b> The Windows sentence
/// listed Unix-isms — <c>grep</c>, <c>awk</c>, <c>test</c>, <c>[ ]</c> — while the failure §9au
/// records is <c>Select-String</c> sent to <c>cmd.exe</c>: a PowerShell cmdlet, not a Unix command.
/// It was guarding against mistakes models do not make and silent about the one they made.</para>
/// </summary>
public sealed class WhatProvesTheWorkTests
{
    private static string Prompt() => Planner.SystemPromptFor(maxSteps: null, proposeChecks: true);

    /// <summary>
    /// THE FIX THE REVIEW EARNED. The measured mistake was a cmdlet in cmd.exe, so the sentence
    /// names cmdlets.
    /// </summary>
    [Fact]
    public void The_windows_sentence_names_the_mistake_that_was_actually_made()
    {
        var prompt = Prompt();

        Assert.Contains("Select-String", prompt, StringComparison.Ordinal);
        Assert.Contains("powershell -NoProfile -Command", prompt, StringComparison.Ordinal);

        // The Unix names stay too - they are cheap and a planner told the host is Windows may
        // still reach for them - but they are no longer the whole of the warning.
        Assert.Contains("grep", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// What no model can infer and what therefore may never be trimmed: the harness's own
    /// contract. Each of these is a fact about Enactive, not about checking in general.
    /// </summary>
    [Theory]
    [InlineData("run_command")]          // which tool runs them
    [InlineData("cmd.exe")]              // and under which shell
    [InlineData("/bin/sh")]
    [InlineData("EXIT CODE")]            // that nothing reads the output
    [InlineData("BEFORE the work")]      // that a check is baselined
    [InlineData("4")]                    // the cap
    public void The_contract_no_model_could_guess_is_still_there(string clause)
        => Assert.Contains(clause, Prompt(), StringComparison.Ordinal);

    /// <summary>
    /// The two rules the review correctly found stated twice are now stated once — but their
    /// EXAMPLES stay. A named list is what a small model obeys, and nothing in the code enforces
    /// it: <c>echo</c> is refused by this sentence or by nothing.
    /// </summary>
    [Fact]
    public void One_rule_once_but_the_examples_stay()
    {
        var prompt = Prompt();

        Assert.Contains("echo, cd, dir, ls, type, cat or exit", prompt, StringComparison.Ordinal);

        // "would fail now and pass after" and the baseline are one idea; it is made once.
        Assert.Equal(1, Occurrences(prompt, "already passes"));
        Assert.Equal(1, Occurrences(prompt, "cannot fail"));
    }

    /// <summary>
    /// What a rule COSTS is not padding. It is the difference between a rule a model reads as
    /// style and one it reads as consequence, and §9au records a run failed for a check written
    /// against a filename the planner invented.
    /// </summary>
    [Fact]
    public void The_rules_still_say_what_they_cost()
    {
        var prompt = Prompt();

        Assert.Contains("failed for your guess", prompt, StringComparison.Ordinal);
        Assert.Contains("proving nothing", prompt, StringComparison.Ordinal);
    }

    /// <summary>
    /// And it got shorter. Not by half — the review's compact version dropped the costs above —
    /// but the duplication is gone.
    /// </summary>
    [Fact]
    public void It_is_shorter_than_it_was()
    {
        var words = Prompt().Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

        // The whole planner prompt, checks included. Before this it was over 700 words.
        Assert.True(words < 700, $"the planner prompt is {words} words");
    }

    /// <summary>
    /// A schema is deliberately NOT sent. An outside review asked for it; a test written months
    /// earlier refused it within the minute, holding a decision already argued — constrained
    /// decoding on a small planner can eat the reasoning the planner is there for, and the
    /// instruction attached to it was "measure before changing this".
    ///
    /// <para>So the shape stays in the words, where every planner can read it, instead of an unused schema constant.</para>
    /// </summary>
    [Fact]
    public void The_shape_is_in_the_words_because_the_schema_is_not_sent()
    {
        // The words carry it, and must: nothing else does.
        Assert.Contains("\"checks\":[{\"name\"", Prompt(), StringComparison.Ordinal);
        Assert.Contains("up to 4 shell commands", Prompt(), StringComparison.Ordinal);
    }

    /// <summary>A plan with no checks asked for gets no schema and no checks paragraph.</summary>
    [Fact]
    public void A_plan_without_checks_is_not_given_either()
    {
        var plain = Planner.SystemPromptFor(maxSteps: null, proposeChecks: false);

        Assert.DoesNotContain("checks", plain, StringComparison.OrdinalIgnoreCase);
    }

    private static int Occurrences(string text, string needle)
    {
        var n = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }
}
