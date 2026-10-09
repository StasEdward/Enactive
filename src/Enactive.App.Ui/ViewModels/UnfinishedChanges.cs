namespace Enactive.App.Ui.ViewModels;

using Enactive.Workspace;

/// <summary>
/// What deleting an unfinished run would put back, said before the person chooses. The files are named - a person
/// deciding whether to undo a run's work is deciding about these files, and "the files it changed" said nothing about
/// which: on 2026-10-09 an interrupted run had left a source file broken on purpose, and nothing told anyone.
/// </summary>
internal static class UnfinishedChanges
{
    /// <summary>How many files each sentence names before it says how many more there are.</summary>
    internal const int Named = 6;

    public static string Describe(IReadOnlyList<EarlierChange> changes)
    {
        var lines = new List<string>();
        var found = changes.Where(c => c.ExistedBefore && c.AsLeft).Select(c => c.Path).ToArray();
        var made = changes.Where(c => !c.ExistedBefore && c.AsLeft).Select(c => c.Path).ToArray();
        var since = changes.Where(c => !c.AsLeft).Select(c => c.Path).ToArray();
        if (found.Length > 0)
            lines.Add($"Put back as they were before it: {List(found)}.");
        if (made.Length > 0)
            lines.Add($"Taken away, as it made them: {List(made)}.");
        if (since.Length > 0)
            lines.Add($"Changed since it stopped, so left as they are either way: {List(since)}.");
        lines.Add("What it changed with commands is not recorded, and stays.");
        return string.Join("\n", lines);
    }

    private static string List(IReadOnlyList<string> paths)
        => paths.Count <= Named ? string.Join(", ", paths) : string.Join(", ", paths.Take(Named)) + $" and {paths.Count - Named} more";
}
