namespace Enactive.Engine.Tests;

using System.Text.Json;
using System.Text.Json.Nodes;
using Enactive.Agents;
using Enactive.Core.Events;
using Enactive.Core.Tasks;
using Enactive.Core.Templates;
using Xunit;

/// <summary>
/// Bench wiki-drift, all 5 runs (plans/aidlc-adoption-bench.md, Phase 1): step 2 consumed Docs/wiki/CLI.md and
/// Docs/wiki/Configuration.md, and the local worker keyed its results by "CLI.md", by "'Docs/wiki/CLI.md'", or by one
/// summary key ("discrepancies"). Each key was looked up as a path, so a page read whole was answered "CLI.md (never
/// read) - read them": the worker read it again, sent the same keys, and the repeat guard stopped the step.
/// </summary>
public sealed class AConsumersResultIsKeyedByItsItemTests
{
    private static readonly string[] Pages = ["Docs/wiki/CLI.md", "Docs/wiki/Configuration.md"];

    private const string Plan = """
        {"disposition":"task","title":"check the wiki",
         "steps":[{"title":"find pages","dependsOn":[],"output":{"pages":{"type":"path[]","description":"the pages"}}},
                  {"title":"check pages","dependsOn":[0],"consumes":[{"step":0,"field":"pages","evidence":"file_read"}],
                   "output":{"notes":{"type":"results","description":"a note per page"}}}]}
        """;

    private static EngineFixture Wiki(IEnumerable<string> pages)
    {
        var fx = new EngineFixture { StepOutputs = true, TypedCriteria = true };
        foreach (var page in pages)
            fx.Write(page, $"# {page}\nabout it\n");
        return fx;
    }

    private static Turn[] Found(string[] pages)
        => [Turn.Calls1(StepOutputContract.ToolName, JsonSerializer.Serialize(new { pages }), "s1"), Turn.Says("Found them.")];

    private static Turn Read(string page) => Turn.Calls1("read_file", JsonSerializer.Serialize(new { path = page }), "r-" + page);

    private static Turn Notes(IReadOnlyDictionary<string, string> notes)
        => Turn.Calls1(StepOutputContract.ToolName, JsonSerializer.Serialize(new { notes }), "s2");

    private static WorkEvent StepEnd(List<WorkEvent> events, int step)
        => events.Last(e => e.Kind == EventKind.StepCompleted && e.StepNo() == step);

    /// <summary>What step 2 was answered when it handed its result on.</summary>
    private static string Answer(List<WorkEvent> events)
        => events.First(e => e.Kind == EventKind.ToolResult && e.StepNo() == 2
                             && e.Summary.StartsWith(StepOutputContract.ToolName + " -> ok:", StringComparison.Ordinal)).Summary;

    private static async Task<List<WorkEvent>> RunAsync(string[] pages, IReadOnlyDictionary<string, string> notes, IEnumerable<string>? read = null)
    {
        using var fx = Wiki(pages);
        var worker = new FakeChatProvider([Turn.Says(Plan), .. Found(pages), .. (read ?? pages).Select(Read), Notes(notes), Turn.Says("Checked them.")]);
        return await fx.RunAsync(fx.Build(worker), "check every wiki page");
    }

    // ── through a run ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// THE ONE THAT MATTERS: keyed by file name, both pages read whole - the keys are taken as the pages, the read of the
    /// full path backs each, and the gate counts both covered. Without it: "CLI.md (never read)", then "no result".
    /// </summary>
    [Fact]
    public async Task A_result_keyed_by_the_items_file_name_is_the_item_and_is_covered()
    {
        var events = await RunAsync(Pages, new Dictionary<string, string> { ["CLI.md"] = "two flags are gone", ["Configuration.md"] = "accurate" });

        var answer = Answer(events);
        Assert.DoesNotContain("never read", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("Not backed", answer, StringComparison.Ordinal);
        Assert.Contains("notes: 'CLI.md' was taken as the item Docs/wiki/CLI.md.", answer, StringComparison.Ordinal);
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, 2).StepOutcome());
        Assert.Contains(events, e => e.Summary.Contains("The engine's coverage check passed: pages of step 1, covered 2 of 2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_result_keyed_by_the_items_path_in_quotes_is_the_item_and_is_covered()
    {
        var events = await RunAsync(Pages, new Dictionary<string, string> { ["'Docs/wiki/CLI.md'"] = "two flags are gone", ["`Docs/wiki/Configuration.md`"] = "accurate" });

        Assert.DoesNotContain("never read", Answer(events), StringComparison.Ordinal);
        Assert.Equal(StepOutcomeKind.Succeeded, StepEnd(events, 2).StepOutcome());
    }

    /// <summary>One summary key: told it is not one of the items, and which are - not to read pages it has read.</summary>
    [Fact]
    public async Task A_key_that_is_no_item_is_told_the_keys_expected_and_not_to_read_again()
    {
        var events = await RunAsync(Pages, new Dictionary<string, string> { ["discrepancies"] = "CLI.md: two flags are gone" });

        var answer = Answer(events);
        Assert.DoesNotContain("never read", answer, StringComparison.Ordinal);
        Assert.Contains("'notes' is keyed by 'discrepancies' - not one of the items this step is responsible for, so not counted as covered. "
            + "Its keys are the items themselves; expected: Docs/wiki/CLI.md, Docs/wiki/Configuration.md.", answer, StringComparison.Ordinal);
        // Accepted as before; the gate still counts what is not covered.
        Assert.Equal(StepOutcomeKind.ReviewRejected, StepEnd(events, 2).StepOutcome());
    }

    /// <summary>Regression guard: the right key on a page that really was not read is still "never read".</summary>
    [Fact]
    public async Task The_right_key_on_a_page_never_read_is_still_never_read()
    {
        var events = await RunAsync(Pages, Pages.ToDictionary(p => p, _ => "fine"), read: [Pages[1]]);

        var answer = Answer(events);
        Assert.Contains("Not backed by anything this step did, so not counted as covered: Docs/wiki/CLI.md (never read).", answer, StringComparison.Ordinal);
        Assert.DoesNotContain("not one of the items", answer, StringComparison.Ordinal);
    }

    /// <summary>A file name two pages share is not guessed: the step is told the keys expected.</summary>
    [Fact]
    public async Task A_file_name_two_items_share_is_not_guessed()
    {
        string[] pages = ["a/x.md", "b/x.md"];
        var events = await RunAsync(pages, new Dictionary<string, string> { ["x.md"] = "fine" });

        var answer = Answer(events);
        Assert.DoesNotContain("was taken as the item", answer, StringComparison.Ordinal);
        Assert.Contains("'notes' is keyed by 'x.md' - not one of the items this step is responsible for", answer, StringComparison.Ordinal);
        Assert.Contains("expected: a/x.md, b/x.md.", answer, StringComparison.Ordinal);
    }

    // ── the decision ─────────────────────────────────────────────────────────────────────

    private static readonly StepOutputSchema Schema = new("s", 1,
        [new StepOutputField("notes", StepOutputFieldType.Results, "a note per page", Required: true),
         new StepOutputField("other", StepOutputFieldType.Results, "keyed as it likes", Required: false)]);

    private static readonly Dictionary<string, IReadOnlyList<string>> Owed = new() { ["notes"] = Pages };

    private static JsonObject Values(string json) => JsonNode.Parse(json)!.AsObject();

    /// <summary>Renamed in place, once - what is gathered, handed on and counted is keyed by the item, in the step's order.</summary>
    [Fact]
    public void Keys_are_renamed_to_the_item_in_the_values_handed_on()
    {
        var values = Values("""{"notes":{"Configuration.md":"ok","\"Docs/wiki/CLI.md\"":"gone"},"other":{"CLI.md":"kept"}}""");

        var keying = EvidenceCoverage.KeyByItems(Schema, values, Owed);

        Assert.Equal(["Docs/wiki/Configuration.md", "Docs/wiki/CLI.md"], values["notes"]!.AsObject().Select(p => p.Key));
        Assert.Equal("gone", values["notes"]!["Docs/wiki/CLI.md"]!.GetValue<string>());
        // A field no consume feeds keys as it likes.
        Assert.Equal(["CLI.md"], values["other"]!.AsObject().Select(p => p.Key));
        Assert.Empty(keying.Strangers);
        Assert.Equal(2, keying.Taken.Count);
    }

    /// <summary>A key that would land on an item that already has a result is not renamed: one of the two would be lost.</summary>
    [Fact]
    public void A_key_is_not_renamed_onto_an_item_the_field_already_has()
    {
        var values = Values("""{"notes":{"Docs/wiki/CLI.md":"a","CLI.md":"b"}}""");

        var keying = EvidenceCoverage.KeyByItems(Schema, values, Owed);

        Assert.Equal(["Docs/wiki/CLI.md", "CLI.md"], values["notes"]!.AsObject().Select(p => p.Key));
        Assert.Equal([("notes", "CLI.md", "Docs/wiki/CLI.md")], keying.Duplicates);
        Assert.Empty(keying.Strangers);
    }

    /// <summary>
    /// A key refused only because its item already has a result names one of the items: it is told so, not that it is
    /// "not one of the items" - which sent it looking for another name when the fault is the second result.
    /// </summary>
    [Fact]
    public void A_second_result_for_an_item_is_said_to_be_one()
    {
        var values = Values("""{"notes":{"Docs/wiki/CLI.md":"a","CLI.md":"b"}}""");

        var said = EvidenceCoverage.NotItems(EvidenceCoverage.KeyByItems(Schema, values, Owed), Owed)!;

        Assert.Contains("'CLI.md' in 'notes' names Docs/wiki/CLI.md, which already has a result there", said, StringComparison.Ordinal);
        Assert.DoesNotContain("not one of the items", said, StringComparison.Ordinal);
        // No read would back it either: only the item itself (here really unread) is told "never read", not the second key.
        var gathered = EvidenceCoverage.Gather(Schema, values, new ReadLedger(), [], [], step: 2);
        Assert.Equal("Not backed by anything this step did, so not counted as covered: Docs/wiki/CLI.md (never read). "
                     + "Read or check them, then hand the result on again.",
            EvidenceCoverage.Unbacked(gathered, EvidenceCoverage.KeyByItems(Schema, values, Owed)));
    }

    /// <summary>
    /// A step that filters gives each item one entry, in its results or in 'excluded' - told "one entry per item" alone it
    /// could put an item in both, which its gate fails as "both processed and excluded".
    /// </summary>
    [Fact]
    public void A_step_that_filters_is_told_each_item_once_in_its_results_or_excluded()
    {
        var filtering = new StepOutputSchema("s", 1,
            [new StepOutputField("notes", StepOutputFieldType.Results, "a note per page", Required: true),
             new StepOutputField(EvidenceCoverage.ExcludedField, StepOutputFieldType.Results, "why left out", Required: false)]);
        var owed = new Dictionary<string, IReadOnlyList<string>> { ["notes"] = Pages, [EvidenceCoverage.ExcludedField] = Pages };
        var values = Values("""{"notes":{"discrepancies":"x"}}""");

        var said = EvidenceCoverage.NotItems(EvidenceCoverage.KeyByItems(filtering, values, owed), owed)!;

        Assert.EndsWith("Hand the result on again keyed by the items: each item once, in 'notes' or in 'excluded', not both.", said, StringComparison.Ordinal);
        Assert.DoesNotContain("one entry per item", said, StringComparison.Ordinal);
    }

    [Fact]
    public void Without_items_owed_nothing_changes()
    {
        var values = Values("""{"notes":{"discrepancies":"x"}}""");

        var keying = EvidenceCoverage.KeyByItems(Schema, values, null);

        Assert.Same(EvidenceCoverage.Keying.None, keying);
        Assert.Null(EvidenceCoverage.NotItems(keying, null));
        Assert.Equal(["discrepancies"], values["notes"]!.AsObject().Select(p => p.Key));
    }

    /// <summary>The items owed are the consumed list's, per results field - 'excluded' too for a step that filters.</summary>
    [Fact]
    public void The_items_owed_are_the_consumed_lists()
    {
        var filters = new SuccessCriterionDefinition("covered", "covers_all", Required: true, Origin: CriterionOrigin.Declared)
        {
            Typed = new TypedCriterion(TypedCriterionKind.EvidenceCoversAll, SourceStep: 0, SourceField: "pages",
                ResultsStep: 1, ResultsField: "notes", Evidence: EvidenceKind.FileRead, Subset: true)
        };
        var source = new StepOutput(1, "find", Guid.NewGuid(), "s", 1, DateTimeOffset.UtcNow, JsonSerializer.Serialize(new { pages = Pages }), [], 1, []);

        var owed = EvidenceCoverage.Owed([filters], [source]);

        Assert.Equal(Pages, owed["notes"]);
        Assert.Equal(Pages, owed[EvidenceCoverage.ExcludedField]);
        Assert.Empty(EvidenceCoverage.Owed([filters], []));
    }

    /// <summary>The expected keys are bounded, like the items Unbacked names.</summary>
    [Fact]
    public void The_keys_expected_are_named_up_to_eight()
    {
        var many = Enumerable.Range(1, 11).Select(i => $"wiki/p{i:00}.md").ToArray();
        var values = Values("""{"notes":{"discrepancies":"x"}}""");
        var owed = new Dictionary<string, IReadOnlyList<string>> { ["notes"] = many };

        var said = EvidenceCoverage.NotItems(EvidenceCoverage.KeyByItems(Schema, values, owed), owed)!;

        Assert.Contains("expected: wiki/p01.md, wiki/p02.md, wiki/p03.md, wiki/p04.md, wiki/p05.md, wiki/p06.md, wiki/p07.md, wiki/p08.md, and 3 more.",
            said, StringComparison.Ordinal);
    }
}
