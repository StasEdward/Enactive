namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Execution;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// A model answer whose JSON is broken is refused as broken - never validated as some fragment of
/// itself - and a file the step read in full is not called "an excerpt" once its text has been cut.
///
/// <para>Both from 2026-09-28. At 11:01 a reviewer's answer had one ']' where a '}' belonged; the
/// extraction handed on the assessment object inside it, and the reviewer was told eight fields it
/// had sent were missing. At 11:36 a step's context was trimmed, and a file it had read whole was
/// refused a rewrite as "seen only as an excerpt", three times, until the step ended.</para>
/// </summary>
public sealed class ABrokenAnswerIsNotAFragmentTests
{
    private const string Case = "20260928-080139-808c815a8a1a.json";

    /// <summary>THE ONE THAT MATTERS, on the real answer, kept in the corpus beside what the validators were handed.</summary>
    [Fact]
    public void The_real_broken_answer_is_refused_as_broken_not_as_a_fragment()
    {
        var recorded = ReviewCorpus.Read(Path.Combine(CorpusRatchet.RepositoryFolder("review-corpus"), Case));
        var raw = recorded.RawAnswer!;

        // What the validators were given then: a whole object from inside the answer.
        Assert.StartsWith("""{"verdict":"fail","reason":""", recorded.Answer, StringComparison.Ordinal);
        Assert.Contains(recorded.Answer[..40], raw, StringComparison.Ordinal);

        // What they are given now: nothing - and told why.
        Assert.Null(ModelText.ExtractJsonObject(raw));
        var errors = CombinedReviewValidation.Errors(raw, recorded.Obligations, new ExecutionJournal().Describe());
        var error = Assert.Single(errors);
        Assert.Contains("never closed", error, StringComparison.Ordinal);
        Assert.Contains("does not fulfil S2.\"]}", error, StringComparison.Ordinal);   // where it broke
        Assert.DoesNotContain("required field is missing", error, StringComparison.Ordinal);
    }

    [Theory]
    // An unclosed answer holding whole objects: none of them is the answer.
    [InlineData("""{"verdict":"fail","assessments":{"report":{"verdict":"fail","reason":"r"}""")]
    // Balanced, but broken - a bracket where a brace belongs: its inside is not the answer either.
    [InlineData("""{"verdict":"fail","assessments":{"report":{"verdict":"fail","reason":"r"]},"notes":"n"}""")]
    public void Nothing_inside_a_broken_object_is_returned_as_the_answer(string answer)
        => Assert.Null(ModelText.ExtractJsonObject(answer));

    /// <summary>What was right before stays right: prose braces are stepped over, and the real object after them found.</summary>
    [Theory]
    [InlineData("""I used {like this} here: {"verdict":"pass"}""", """{"verdict":"pass"}""")]
    [InlineData("""The shape is {"verdict": ...}. Answer: {"verdict":"pass"}""", """{"verdict":"pass"}""")]
    [InlineData("""{see below} {"verdict":"pass"}""", """{"verdict":"pass"}""")]
    public void A_real_object_after_prose_is_still_found(string text, string expected)
        => Assert.Equal(expected, ModelText.ExtractJsonObject(text));

    [Fact]
    public void A_complete_answer_has_no_problem_to_report()
        => Assert.Null(ModelText.JsonProblem("""{"verdict":"pass","notes":"fine"}"""));

    // ── a file read in full, and then cut ────────────────────────────────────────────────

    [Fact]
    public void A_file_read_whole_and_then_cut_from_the_conversation_is_not_called_an_excerpt()
    {
        var ledger = new ReadLedger();
        ledger.Saw(new("r", "read_file", "{}"), ToolResults.Ok(metadata: new Dictionary<string, object?>
            { ["path"] = "notes.md", ["firstLine"] = 1, ["lastLine"] = 48, ["totalLines"] = 48 }), new ReadFileTool().Definition);
        ledger.ForgetDiscardedReads();

        var refusal = ledger.Refuse(new("w", "write_file", "{}"), "notes.md", new WriteFileTool().Definition);

        Assert.NotNull(refusal);
        Assert.Contains("cut from this conversation", refusal, StringComparison.Ordinal);
        Assert.DoesNotContain("excerpt", refusal, StringComparison.Ordinal);

        // Read again, and the write goes ahead.
        ledger.Saw(new("r2", "read_file", "{}"), ToolResults.Ok(metadata: new Dictionary<string, object?>
            { ["path"] = "notes.md", ["firstLine"] = 1, ["lastLine"] = 48, ["totalLines"] = 48 }), new ReadFileTool().Definition);
        Assert.Null(ledger.Refuse(new("w", "write_file", "{}"), "notes.md", new WriteFileTool().Definition));
    }
}
