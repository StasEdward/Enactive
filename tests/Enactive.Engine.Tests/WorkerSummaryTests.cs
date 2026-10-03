namespace Enactive.Engine.Tests;

using Enactive.Settings;
using Xunit;

/// <summary>
/// The team list printed a worker's model as it is stored. Since the worker editor keeps "no model chosen" as a
/// state of its own, that state is an empty string, and the row read "  ·  Execute" - a separator with nothing
/// in front of it, which looks like a rendering fault and says nothing about the worker.
/// </summary>
public sealed class WorkerSummaryTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void A_worker_with_no_model_says_so_in_the_list(string model)
    {
        var line = new WorkerConfig { Model = model }.Summary();

        Assert.StartsWith(WorkerConfig.NoModel + "  ·  ", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_worker_with_a_model_shows_it_and_its_level()
        => Assert.Equal("local/qwen  ·  Execute", new WorkerConfig { Model = "local/qwen" }.Summary());
}
