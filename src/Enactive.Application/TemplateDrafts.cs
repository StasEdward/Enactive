namespace Enactive.Application;

using Enactive.Core.Templates;
using Enactive.Workspace;

/// <summary>
/// What a template is opened in the editor as, and the question before one is deleted - the window's Settings, Templates
/// section and the service's Templates page, in the same words. Each was the window's private rule; the service needs
/// the same ones, and a second copy of "a built-in is copied, not edited" is one that can drift into editing it.
/// </summary>
internal static class TemplateDrafts
{
    /// <summary>A template nobody has written yet.</summary>
    public static TaskTemplate New() => new(Id: "", Name: "", Goal: "", Description: null, Category: "Custom");

    /// <summary>
    /// The draft to open, and whether its id may still change. A built-in becomes a normal template of the same id -
    /// saving it writes a file that shadows the built-in, which is the documented customisation path and is undone by
    /// deleting that file - and, being a new file, may still take another id.
    /// </summary>
    public static (TaskTemplate Draft, bool IdEditable) Edit(StoredTemplate entry)
        => (entry.IsBuiltin ? entry.Template with { Builtin = false } : entry.Template, entry.IsBuiltin);

    /// <summary>A copy under an id nothing in <paramref name="taken"/> uses, so a duplicate does not silently replace its original.</summary>
    public static TaskTemplate Duplicate(StoredTemplate entry, IEnumerable<string> taken)
        => entry.Template.Duplicate(Available(entry.Template.Id + "-copy", taken));

    /// <summary>An id nothing is using yet: the one wanted, else the first "-2", "-3"… free.</summary>
    public static string Available(string wanted, IEnumerable<string> taken)
    {
        var used = taken.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!used.Contains(wanted))
            return wanted;

        for (var n = 2; ; n++)
            if (!used.Contains($"{wanted}-{n}"))
                return $"{wanted}-{n}";
    }

    /// <summary>
    /// The question before a template's file is deleted: a reset when the file replaced a built-in (the original comes
    /// back), a delete otherwise. <paramref name="notUndoneBy"/> names what a person might expect to undo it - the window's
    /// Settings has a Cancel that does not - or is null where nothing would.
    /// </summary>
    public static Question Remove(StoredTemplate entry, string? notUndoneBy = null)
    {
        var now = "This happens now" + (notUndoneBy is null ? "." : $" — {notUndoneBy} does not undo it.");
        return entry.Shadows == TemplateOrigin.Builtin
            ? new($"Reset “{entry.Template.Name}” to the built-in?",
                "Your version is deleted and the one that ships with Enactive comes back. " + now, "Remove", "Keep")
            : new($"Delete “{entry.Template.Name}”?",
                $"The file is deleted from {(entry.Scope == TemplateScope.Workspace ? "this workspace" : "your global templates")}. " + now,
                "Remove", "Keep");
    }
}
