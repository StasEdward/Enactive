namespace Enactive.App.Ui.Tests;

using Enactive.App.Ui.ViewModels;
using Enactive.Core.Providers;
using Enactive.Settings;

/// <summary>
/// The provider window: twenty fields in one column, a third of which only one kind of provider reads, and two that said
/// one thing in two units. Each field is now shown for the kinds that read it, and the handover point is one field.
/// </summary>
public sealed class ProviderEditTests
{
    private static ProviderEditViewModel Edit(ProviderConfig config) => new(config, () => { });

    /// <summary>What only one kind of provider reads is shown for that kind alone.</summary>
    [Theory]
    [InlineData(ProviderKind.Anthropic, true, false, false)]
    [InlineData(ProviderKind.OllamaNative, false, true, false)]
    [InlineData(ProviderKind.OpenAiCompatible, false, false, true)]
    public void A_field_is_shown_for_the_kind_that_reads_it(ProviderKind kind, bool effort, bool keepAlive, bool openAi)
    {
        var editor = Edit(new ProviderConfig { Id = "p" });
        editor.Kind = kind;

        Assert.Equal((effort, keepAlive, openAi), (editor.ShowsEffort, editor.ShowsKeepAlive, editor.ShowsOpenAiOptions));
    }

    /// <summary>The window is told when the kind changes what it shows, or it would keep showing the old kind's fields.</summary>
    [Fact]
    public void Changing_the_kind_says_what_is_shown_has_changed()
    {
        var editor = Edit(new ProviderConfig { Id = "p", Kind = ProviderKind.OpenAiCompatible });
        var changed = new List<string?>();
        editor.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        editor.Kind = ProviderKind.Anthropic;

        Assert.Contains(nameof(ProviderEditViewModel.ShowsEffort), changed);
        Assert.Contains(nameof(ProviderEditViewModel.ShowsKeepAlive), changed);
        Assert.Contains(nameof(ProviderEditViewModel.ShowsOpenAiOptions), changed);
    }

    /// <summary>A value hidden by a change of kind is kept, not cleared - back to the kind, it is there; applied, it is written.</summary>
    [Fact]
    public void A_hidden_value_is_kept()
    {
        var config = new ProviderConfig { Id = "p", Kind = ProviderKind.Anthropic, Effort = "high", OllamaKeepAliveSeconds = -1 };
        var editor = Edit(config);

        editor.Kind = ProviderKind.OpenAiCompatible;
        Assert.Equal("high", editor.EffortText);
        editor.Kind = ProviderKind.Anthropic;
        editor.SaveCommand.Execute(null);

        Assert.Equal("high", config.Effort);
        Assert.Equal(-1, config.OllamaKeepAliveSeconds);
    }

    // ── where a step is handed over ─────────────────────────────────────────

    [Theory]
    [InlineData("", null, null)]
    [InlineData("75%", 75, null)]
    [InlineData("80000", null, 80000)]
    [InlineData("75%, 80000", 75, 80000)]
    [InlineData(" 80000 ,75 % ", 75, 80000)]
    public void The_handover_point_is_a_share_a_size_or_both(string text, int? percent, int? tokens)
        => Assert.Equal((percent, tokens, (string?)null), ProviderEditViewModel.ReadHandover(text));

    [Theory]
    [InlineData("abc")]
    [InlineData("150%")]
    [InlineData("0%")]
    [InlineData("75%, 80%")]
    [InlineData("80000, 90000")]
    [InlineData("-5")]
    public void What_is_not_a_handover_point_is_said(string text)
        => Assert.NotNull(ProviderEditViewModel.ReadHandover(text).Problem);

    /// <summary>
    /// A handover point that cannot be read is said, and nothing is applied: read as blank it would be a step never
    /// handed over, which the window's other number fields do to what they cannot read.
    /// </summary>
    [Fact]
    public void An_unreadable_handover_point_applies_nothing_and_says_why()
    {
        var config = new ProviderConfig { Id = "p", HandoverAtPercent = 75 };
        var editor = Edit(config);
        var closed = false;
        editor.CloseRequested += () => closed = true;

        editor.HandoverText = "most of it";
        editor.Id = "renamed";
        editor.SaveCommand.Execute(null);

        Assert.False(closed);
        Assert.Equal(("p", 75), (config.Id, config.HandoverAtPercent));
        Assert.Contains("Hand a step over at", editor.Status, StringComparison.Ordinal);
    }

    /// <summary>Both settings are shown in the one field and written back as they were.</summary>
    [Fact]
    public void Both_handover_settings_come_through_the_one_field()
    {
        var config = new ProviderConfig { Id = "p", HandoverAtPercent = 75, WorkingContextTokens = 80_000 };
        var editor = Edit(config);
        Assert.Equal("75%, 80000", editor.HandoverText);

        editor.SaveCommand.Execute(null);

        Assert.Equal((75, 80_000), (config.HandoverAtPercent, config.WorkingContextTokens));
    }

    /// <summary>The window no longer asks for a display name, and does not wipe one a provider already has.</summary>
    [Fact]
    public void A_display_name_already_given_is_kept()
    {
        var config = new ProviderConfig { Id = "anthropic", DisplayName = "Anthropic (work)" };

        Edit(config).SaveCommand.Execute(null);

        Assert.Equal("Anthropic (work)", config.DisplayName);
    }
}
