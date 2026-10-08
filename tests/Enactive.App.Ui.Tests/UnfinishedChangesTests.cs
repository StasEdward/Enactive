namespace Enactive.App.Ui.Tests;

using Enactive.App.Ui.ViewModels;
using Enactive.Workspace;

/// <summary>What deleting an unfinished run would put back, said by file before the person chooses.</summary>
public sealed class UnfinishedChangesTests
{
    [Fact]
    public void Each_file_is_named_by_what_putting_it_back_does()
    {
        var said = UnfinishedChanges.Describe(
        [
            new EarlierChange("TicTacToe/ComputerAI.cs", ExistedBefore: true, AsLeft: true),
            new EarlierChange("draft.txt", ExistedBefore: false, AsLeft: true),
            new EarlierChange("notes.txt", ExistedBefore: true, AsLeft: false)
        ]);

        Assert.Contains("Put back as they were before it: TicTacToe/ComputerAI.cs.", said);
        Assert.Contains("Taken away, as it made them: draft.txt.", said);
        Assert.Contains("Changed since it stopped, so left as they are either way: notes.txt.", said);
        Assert.Contains("with commands is not recorded", said);
    }

    [Fact]
    public void A_long_list_says_how_many_more()
    {
        var said = UnfinishedChanges.Describe(Enumerable.Range(1, UnfinishedChanges.Named + 3)
            .Select(i => new EarlierChange($"f{i}.txt", true, true)).ToArray());

        Assert.Contains("and 3 more", said);
        Assert.DoesNotContain($"f{UnfinishedChanges.Named + 1}.txt", said);
    }
}
