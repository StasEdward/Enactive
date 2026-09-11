namespace Enactive.Engine.Tests;

using Enactive.Core.Guidance;
using Xunit;

/// <summary>
/// The explanations behind the "?" on AI · General.
///
/// <para>Fourteen paragraphs used to be stacked down that page, which made a settings screen that
/// had to be read end to end to be used: the switch somebody came for was four scrolls down,
/// behind reasoning about switches they were not changing. The text was never the problem — it is
/// why these settings are understandable at all — it just did not need to be on screen at once.
/// </para>
///
/// <para>It is testable only because it moved to Core on the way. In <c>Enactive.App.Ui</c> it was
/// fourteen strings inside a WinExe no test project references.</para>
/// </summary>
public sealed class SettingsAdviceTests
{
    /// <summary>
    /// Nothing is blank. A "?" that opens an empty panel invites a click, answers nothing, and
    /// teaches the reader to stop trusting the other thirteen.
    /// </summary>
    [Fact]
    public void Every_setting_says_something()
    {
        foreach (var (setting, text) in SettingsAdvice.All)
            Assert.False(string.IsNullOrWhiteSpace(text), setting);
    }

    /// <summary>
    /// And no two settings say the SAME thing. Fourteen constants copied from fourteen places is
    /// exactly the shape where two end up pointing at one paragraph, and the wrong explanation
    /// under a switch is worse than none: it is confidently about something else.
    /// </summary>
    [Fact]
    public void No_two_settings_share_an_explanation()
    {
        var seen = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (setting, text) in SettingsAdvice.All)
        {
            Assert.False(seen.TryGetValue(text, out var already),
                $"'{setting}' and '{already}' have the same explanation. One of them is wired to "
                + "the wrong constant.");

            seen[text] = setting;
        }
    }

    /// <summary>
    /// Short enough to read standing up. This is how the feature fails: a paragraph grows into a
    /// page, and a page beside a checkbox is read by nobody. The cap is generous — it catches an
    /// essay, not a careful explanation.
    /// </summary>
    [Fact]
    public void None_of_it_is_an_essay()
    {
        foreach (var (setting, text) in SettingsAdvice.All)
            Assert.True(text.Length < 1200,
                $"'{setting}' is {text.Length} characters. Split it, or say less.");
    }

    /// <summary>
    /// The advice must not be the only place a cost is stated, but where it states one it has to be
    /// specific. These four settings each cost an extra model call, and a person turning one on
    /// without knowing that is the person who later asks why the bill moved.
    /// </summary>
    [Theory]
    [InlineData(nameof(SettingsAdvice.VerifyWrites))]
    [InlineData(nameof(SettingsAdvice.ReviewContent))]
    [InlineData(nameof(SettingsAdvice.CheckSoundness))]
    [InlineData(nameof(SettingsAdvice.ReviewRetries))]
    public void A_setting_that_costs_another_call_says_so(string setting)
    {
        var text = SettingsAdvice.All.Single(a => a.Setting == setting).Text;

        Assert.Contains("call", text, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The one that is genuinely dangerous says what it exposes, rather than only that it is off by
    /// default. A switch whose own label calls it unsafe and whose explanation does not say WHY is
    /// a warning nobody can act on.
    /// </summary>
    [Fact]
    public void The_unsafe_one_explains_what_it_lets_through()
    {
        Assert.Contains("become an action", SettingsAdvice.AllowImplicitToolCalls, StringComparison.Ordinal);
    }

    /// <summary>
    /// Every explanation moved out of the window verbatim. Spot-checked on the two that carry a
    /// fact somebody could act on — the folder logs are written to, and the measured size of a
    /// real log — because a relocation that quietly reworded a path or a number would be a change
    /// nobody reviewed.
    /// </summary>
    [Fact]
    public void The_facts_that_travelled_with_the_text_are_still_in_it()
    {
        Assert.Contains(@"%APPDATA%\Enactive\logs", SettingsAdvice.LogRetentionDays, StringComparison.Ordinal);
        Assert.Contains("three megabytes", SettingsAdvice.LogPromptBodies, StringComparison.Ordinal);
    }
}
