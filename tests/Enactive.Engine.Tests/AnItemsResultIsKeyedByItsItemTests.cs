namespace Enactive.Engine.Tests;

using Enactive.Agents;
using Enactive.Core.Tasks;
using Xunit;

/// <summary>
/// Run c30307ab, 2026-09-28: a page step keyed its finding "Architecture.md", then six times by its own
/// fields - "page", "verdict", "checks_checked", "details" - and every one was accepted. Coverage counted each
/// key as a file never read and asked for it to be read; the step handed the same result on seven times.
/// </summary>
public sealed class AnItemsResultIsKeyedByItsItemTests
{
    private static readonly StepOutputSchema Schema = new("s", 1,
        [new StepOutputField("findings", StepOutputFieldType.Results, "what the page gets wrong", Required: true)]);

    private static readonly string[] Page = ["Docs/wiki/Architecture.md"];

    private static StepOutputContract.Verdict Check(string json, IReadOnlyList<string>? items = null)
        => StepOutputContract.Check(Schema, json, _ => true, _ => true, items);

    [Fact]
    public void Fields_used_as_keys_are_refused_with_the_one_right_shape()
    {
        var verdict = Check("""{"findings":{"page":"Docs/wiki/Architecture.md","verdict":"accurate","details":"31 claims"}}""", Page);

        Assert.False(verdict.Accepted);
        var error = Assert.Single(verdict.Errors);
        Assert.Contains("'page', 'verdict', 'details' are not items of this step", error, StringComparison.Ordinal);
        Assert.Contains("""{"findings":{"Docs/wiki/Architecture.md":"..."}}""", error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_that_names_the_item_by_its_file_name_is_the_item()
    {
        var verdict = Check("""{"findings":{"Architecture.md":"31 claims, all accurate"}}""", Page);

        Assert.True(verdict.Accepted);
        Assert.Equal("31 claims, all accurate", verdict.Values!["findings"]!["Docs/wiki/Architecture.md"]!.GetValue<string>());
        Assert.Contains("findings: 'Architecture.md' was taken as the item Docs/wiki/Architecture.md.", verdict.Notes);
    }

    [Fact]
    public void A_file_name_two_items_share_is_not_guessed()
    {
        var verdict = Check("""{"findings":{"README.md":"fine"}}""", ["Docs/wiki/README.md", "Docs/README.md"]);
        Assert.False(verdict.Accepted);
    }

    [Fact]
    public void The_item_itself_is_accepted_as_it_is()
    {
        Assert.True(Check("""{"findings":{"Docs/wiki/Architecture.md":"fine"}}""", Page).Accepted);
        Assert.True(Check("""{"findings":{"docs\\wiki\\architecture.md":"fine"}}""", Page).Accepted);
    }

    [Fact]
    public void A_step_that_is_for_no_particular_items_keys_as_it_likes()
        => Assert.True(Check("""{"findings":{"page":"x","verdict":"y"}}""").Accepted);
}
