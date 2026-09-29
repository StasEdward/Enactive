namespace Enactive.Engine.Tests;

using System.Text.Json.Nodes;
using Enactive.Agents;
using Enactive.Core.Execution;
using Xunit;

/// <summary>
/// Run feed29, 2026-09-29: two steps passed on everything but one claim each - per-class figures in a 50,000-character
/// report whose middle the evidence view had cut - and ended DONE, NOT VERIFIED. The reviewer named the calls, and did
/// not ask for them. Now the engine shows those calls whole, once, and asks again; what is still unknown stays unknown.
/// Deliberately not code: a long inventory listing, and a claim about what is in its middle.
/// </summary>
public sealed class WhatWasCutIsShownForAnUnknownTests
{
    private const string Middle = "SHELF-17: 42 boxes, all counted";

    private static EvidenceView LongListing(bool cut = true)
    {
        var journal = new ExecutionJournal();
        var listing = new string('a', 20_000) + "\n" + Middle + "\n" + new string('z', 20_000);
        journal.Record(1, "run_command", """{"command":"list-inventory"}""", ActionOutcome.Succeeded, cut ? listing : Middle, exitCode: 0);
        return journal.Describe(0, 6000);
    }

    private static Turn Answer(string verdict, bool namesTheCall = true, int proofCall = 1)
    {
        var answer = JsonNode.Parse(Verdicts.Combined(Verdicts.Shown("inventory listed", proofCall), "run").Text!)!;
        answer["report_checks"] = new JsonArray(new JsonObject {
            ["source_id"] = "worker-report", ["fragment_id"] = "F1", ["kind"] = "observed",
            ["verdict"] = verdict, ["reason"] = verdict == "unknown"
                ? "The count for shelf 17 is in the part of inventory/shelves.txt that was cut" : "The listing shows shelf 17 with 42 boxes",
            ["calls"] = namesTheCall ? new JsonArray(1) : new JsonArray(), ["obligation_ids"] = new JsonArray()
        });
        return Turn.Says(answer.ToJsonString());
    }

    private static EvidenceView Shelves(string middle = Middle)
    {
        var journal = new ExecutionJournal();
        journal.Record(1, "read_file", """{"path":"inventory/shelves.txt"}""", ActionOutcome.Succeeded,
            new string('a', 20_000) + "\n" + middle + "\n" + new string('z', 20_000));
        return journal.Describe(0, 6000);
    }

    /// <summary>
    /// Run 341c2f: the review was being corrected by parts when the calls were shown, and the parts kept were the
    /// report checks - the unknowns themselves. A whole answer is asked for after the calls are shown.
    /// </summary>
    [Fact]
    public async Task A_correction_by_parts_pending_does_not_keep_the_old_unknowns()
    {
        var provider = new FakeChatProvider(Answer("unknown", proofCall: 99), Answer("unknown"), Answer("pass"));

        var result = await Review(provider, LongListing());

        Assert.True(result.Pass, result.Notes);
        Assert.Equal(3, provider.Requests.Count);
        Assert.Contains("Additional requested evidence", provider.Requests[2].Messages[1].Content!, StringComparison.Ordinal);
    }

    /// <summary>Shown as text, as the model saw it: not as a JSON document where every Cyrillic letter is six characters.</summary>
    [Fact]
    public async Task What_is_shown_is_the_text_as_it_was()
    {
        const string shelf = "Полка 17: 42 коробки";
        var provider = new FakeChatProvider(Answer("unknown"), Answer("pass"));

        await Review(provider, Shelves(shelf));

        Assert.Contains(shelf, provider.Requests[1].Messages[1].Content!, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\u041F", provider.Requests[1].Messages[1].Content!, StringComparison.Ordinal);
    }

    /// <summary>Run 341c2f, step 2: the unknown named the file and no call; the shown read of that file stands for it.</summary>
    [Fact]
    public async Task An_unknown_that_names_a_file_and_no_call_is_shown_the_read_of_that_file()
    {
        var provider = new FakeChatProvider(Answer("unknown", namesTheCall: false), Answer("pass"));

        var result = await Review(provider, Shelves());

        Assert.True(result.Pass, result.Notes);
        Assert.Contains(Middle, provider.Requests[1].Messages[1].Content!, StringComparison.Ordinal);
    }

    private static Task<ReviewResult> Review(FakeChatProvider provider, EvidenceView evidence)
        => new Reviewer().ReviewWithProofAsync("count the inventory", "Shelf 17 holds 42 boxes.", evidence, [], [],
            RequestObligations.Create("Count the inventory"), provider, "strong", default);

    /// <summary>THE ONE THAT MATTERS: the call is shown whole, and the claim it bears out is settled.</summary>
    [Fact]
    public async Task An_unknown_over_a_cut_output_is_asked_again_with_the_call_shown_whole()
    {
        var provider = new FakeChatProvider(Answer("unknown"), Answer("pass"));

        var result = await Review(provider, LongListing());

        Assert.True(result.Pass, result.Notes);
        Assert.False(result.Undecided);
        Assert.Equal(2, provider.Requests.Count);
        Assert.DoesNotContain(Middle, provider.Requests[0].Messages[1].Content!, StringComparison.Ordinal);
        Assert.Contains("Additional requested evidence", provider.Requests[1].Messages[1].Content!, StringComparison.Ordinal);
        Assert.Contains(Middle, provider.Requests[1].Messages[1].Content!, StringComparison.Ordinal);
        Assert.Contains("rests on call(s) 1", provider.Requests[1].Messages.Last().Content!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Still_unknown_after_it_stays_unknown_and_is_not_asked_a_third_time()
    {
        var provider = new FakeChatProvider(Answer("unknown"), Answer("unknown"), Answer("pass"));

        var result = await Review(provider, LongListing());

        Assert.True(result.Undecided);
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task Nothing_cut_nothing_to_show_the_unknown_stands_at_once()
    {
        var provider = new FakeChatProvider(Answer("unknown"), Answer("pass"));

        var result = await Review(provider, LongListing(cut: false));

        Assert.True(result.Undecided);
        Assert.Single(provider.Requests);
    }
}
