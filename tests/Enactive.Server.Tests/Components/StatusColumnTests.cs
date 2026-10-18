namespace Enactive.Server.Tests.Components;

using Bunit;
using Enactive.Server.Components.Runs;

/// <summary>The status column's MODEL PERFORMANCE, folded to a line per model.</summary>
public sealed class StatusColumnTests : BunitContext
{
    /// <summary>
    /// The model's name and its speed are drawn apart, so the stylesheet cuts the name and never the speed: as one line
    /// cut at its end, a long local model's name (gemma-4-26B-A4B-it-UD-Q3_K_M) left no room for the tokens a second the
    /// line is there to say. The whole name is in its tooltip.
    /// </summary>
    [Fact]
    public void A_folded_model_line_draws_its_speed_apart_from_the_name()
    {
        const string model = "gemma-4-26B-A4B-it-UD-Q3_K_M";
        var view = LiveRunView.Empty with
        {
            Speeds = [new SpeedLine("llama.cpp", model, "38.4 tok/s", "Generation", "", "", "", "", "", "")]
        };

        var column = Render<StatusColumn>(p => p.Add(c => c.View, view));

        var line = column.Find(".performance .speed");
        Assert.Equal(model, line.QuerySelector(".speed-name")!.TextContent);
        Assert.Equal(model, line.QuerySelector(".speed-name")!.GetAttribute("title"));
        Assert.Equal("38.4 tok/s", line.QuerySelector(".speed-rate")!.TextContent);
        Assert.Equal("llama.cpp", line.GetAttribute("title"));
    }
}
