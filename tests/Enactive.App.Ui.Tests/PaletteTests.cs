namespace Enactive.App.Ui.Tests;

using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Enactive.App.Ui.ViewModels;
using Enactive.Core.History;

/// <summary>
/// The palette's tables: what colour each fact a view model gives is drawn in (Palette). Asked here, in the window's own
/// tests, because they need Avalonia - the engine's tests build the window's Avalonia-free files only.
/// </summary>
public sealed class PaletteTests
{
    private static object? Pick(IValueConverter table, object? value)
        => table.Convert(value, typeof(object), null, CultureInfo.InvariantCulture);

    public static TheoryData<string, Type> Tables => new()
    {
        { nameof(Palette.CardTone), typeof(CardTone) },
        { nameof(Palette.EntryIcon), typeof(FeedEntryKind) },
        { nameof(Palette.Agent), typeof(AgentKind) },
        { nameof(Palette.Change), typeof(ChangeState) },
        { nameof(Palette.DiffLine), typeof(DiffLineKind) },
        { nameof(Palette.WorkspaceEdge), typeof(WorkspacePlace) },
        { nameof(Palette.Reach), typeof(ModelReach) },
        { nameof(Palette.Health), typeof(Enactive.Providers.ProviderHealth) },
        { nameof(Palette.Template), typeof(TemplateEdge) },
        { nameof(Palette.Schedule), typeof(ScheduleState) },
        { nameof(Palette.LogLevel), typeof(Enactive.Core.Diagnostics.LogLevel) },
    };

    /// <summary>
    /// Every value of every state a colour is chosen for has one. The tables name each value and throw on one they do
    /// not know, so a state added later is a failure here rather than a card quietly drawn in a fallback colour.
    /// </summary>
    [Theory]
    [MemberData(nameof(Tables))]
    public void Every_state_has_a_colour(string table, Type states)
    {
        var converter = (IValueConverter)typeof(Palette).GetField(table)!.GetValue(null)!;

        Assert.All(Enum.GetValues(states).Cast<object>(), state => Assert.IsAssignableFrom<IBrush>(Pick(converter, state)));
    }

    /// <summary>A card waiting for a person, or blocked, is amber: nothing went wrong in it.</summary>
    [Fact]
    public void A_card_that_needs_you_is_amber()
        => Assert.Same(Brand.Warning, Pick(Palette.CardTone, CardTone.NeedsYou));

    /// <summary>A run's status is coloured by the kind of ending Core says it is - the same table wherever it is shown.</summary>
    [Theory]
    [InlineData("Completed", "Success")]
    [InlineData("Failed", "Danger")]
    [InlineData("NeedsUser", "Amber")]
    public void A_run_s_edge_is_the_colour_of_its_ending(string status, string brand)
        => Assert.Same(typeof(Brand).GetField(brand)!.GetValue(null), Pick(Palette.RunEdge, status));
}
