namespace Enactive.Server.Components.Shell;

using Enactive.Server.Accounts;

/// <summary>
/// One icon of the rail. <paramref name="Href"/> is the address the item is MEANT to lead to - the desktop's sections,
/// each with a page of its own; <paramref name="Label"/> is its tooltip and its accessible name (an icon has no text
/// of its own); <paramref name="Icon"/> is the icon's path data, one stroke per '|' (24 by 24, the window's grid);
/// <paramref name="Needs"/> is the role that sees it.
/// </summary>
internal sealed record RailItem(string Href, string Label, string Icon, Role Needs)
{
    // Drawn in one outline style for the web's redesign - rounded corners and caps at 1.75 px, the look of the open icon
    // sets (Lucide) - where the window's were square-cornered strokes of its own; the sections and their order are the
    // window's.
    private static readonly RailItem[] All =
    [
        new("/", "Runs", "M 7,5.5 C 7,4.7 7.9,4.2 8.6,4.6 L 18.7,11.1 C 19.3,11.5 19.3,12.5 18.7,12.9 L 8.6,19.4 C 7.9,19.8 7,19.3 7,18.5 Z", Role.Viewer),
        new("/timeline", "Timeline · project memory",
            "M 3,12 A 9,9 0 1 0 6,5.3 L 3,8|M 3,3 V 8 H 8|M 12,7 V 12 L 16,14", Role.Viewer),
        new("/templates", "Templates · a saved task",
            "M 5,3 H 19 A 2,2 0 0 1 21,5 V 8 A 2,2 0 0 1 19,10 H 5 A 2,2 0 0 1 3,8 V 5 A 2,2 0 0 1 5,3 Z"
            + "|M 5,14 H 10 A 2,2 0 0 1 12,16 V 19 A 2,2 0 0 1 10,21 H 5 A 2,2 0 0 1 3,19 V 16 A 2,2 0 0 1 5,14 Z|M 16,14 H 21 M 16,17.5 H 21 M 16,21 H 21",
            Role.Viewer),
        new("/schedules", "Schedules · run it on a day and a time",
            "M 21,7.5 V 6 A 2,2 0 0 0 19,4 H 5 A 2,2 0 0 0 3,6 V 20 A 2,2 0 0 0 5,22 H 8.5|M 16,2 V 6 M 8,2 V 6 M 3,10 H 8"
            + "|M 11,16 A 5,5 0 1 0 21,16 A 5,5 0 1 0 11,16 Z|M 16,14 V 16.2 L 17.5,17.2", Role.Viewer),
        new("/inbox", "Inbox",
            "M 22,12 H 16 L 14,15 H 10 L 8,12 H 2|M 5.45,5.11 L 2,12 V 18 A 2,2 0 0 0 4,20 H 20 A 2,2 0 0 0 22,18 V 12 L 18.55,5.11 A 2,2 0 0 0 16.76,4 H 7.24 A 2,2 0 0 0 5.45,5.11 Z",
            Role.Viewer),
        new("/proposals", "Proposals",
            "M 15,2 H 6 A 2,2 0 0 0 4,4 V 20 A 2,2 0 0 0 6,22 H 18 A 2,2 0 0 0 20,20 V 7 Z|M 14,2 V 6 A 2,2 0 0 0 16,8 H 20|M 9,11 H 15 M 12,8 V 14|M 9,17 H 15",
            Role.Viewer),
        new("/log", "Global log", "M 13,6 H 21 M 13,12 H 21 M 13,18 H 21|M 3,6 H 4 M 3,12 H 4 M 3,18 H 4|M 8,6 H 9 M 8,12 H 9 M 8,18 H 9", Role.Viewer),
        // The rail's bottom: the desktop docks Environment and Settings low; Accounts and Audit sit with them.
        new("/environment", "Environment",
            "M 4,3 H 20 A 2,2 0 0 1 22,5 V 15 A 2,2 0 0 1 20,17 H 4 A 2,2 0 0 1 2,15 V 5 A 2,2 0 0 1 4,3 Z|M 8,21 H 16 M 12,17 V 21", Role.Viewer),
        new("/settings", "Settings",
            "M 9,12 A 3,3 0 1 0 15,12 A 3,3 0 1 0 9,12 Z|"
            + "M 19.4,15 A 1.65,1.65 0 0 0 19.73,16.82 L 19.79,16.88 A 2,2 0 1 1 16.96,19.71 L 16.9,19.65 A 1.65,1.65 0 0 0 15.08,19.32 "
            + "A 1.65,1.65 0 0 0 14.08,20.83 V 21 A 2,2 0 1 1 10.08,21 V 20.91 A 1.65,1.65 0 0 0 9,19.4 A 1.65,1.65 0 0 0 7.18,19.73 "
            + "L 7.12,19.79 A 2,2 0 1 1 4.29,16.96 L 4.35,16.9 A 1.65,1.65 0 0 0 4.68,15.08 A 1.65,1.65 0 0 0 3.17,14.08 H 3 A 2,2 0 1 1 3,10.08 "
            + "H 3.09 A 1.65,1.65 0 0 0 4.6,9 A 1.65,1.65 0 0 0 4.27,7.18 L 4.21,7.12 A 2,2 0 1 1 7.04,4.29 L 7.1,4.35 A 1.65,1.65 0 0 0 8.92,4.68 "
            + "H 9 A 1.65,1.65 0 0 0 10,3.17 V 3 A 2,2 0 1 1 14,3 V 3.09 A 1.65,1.65 0 0 0 15,4.6 A 1.65,1.65 0 0 0 16.82,4.27 L 16.88,4.21 "
            + "A 2,2 0 1 1 19.71,7.04 L 19.65,7.1 A 1.65,1.65 0 0 0 19.32,8.92 V 9 A 1.65,1.65 0 0 0 20.83,10 H 21 A 2,2 0 1 1 21,14 H 20.91 "
            + "A 1.65,1.65 0 0 0 19.4,15 Z",
            Role.Admin),
        new("/accounts", "Accounts",
            "M 16,21 V 19 A 4,4 0 0 0 12,15 H 6 A 4,4 0 0 0 2,19 V 21|M 5,7 A 4,4 0 1 0 13,7 A 4,4 0 1 0 5,7 Z|M 22,21 V 19 A 4,4 0 0 0 19,15.13|M 16,3.13 A 4,4 0 0 1 16,10.88",
            Role.Admin),
        new("/audit", "Audit",
            "M 20,13 C 20,18 16.5,20.5 12.34,21.95 A 1,1 0 0 1 11.67,21.94 C 7.5,20.5 4,18 4,13 V 6 A 1,1 0 0 1 5,5 C 7,5 9.5,3.8 11.24,2.28 "
            + "A 1.17,1.17 0 0 1 12.76,2.28 C 14.51,3.81 17,5 19,5 A 1,1 0 0 1 20,6 Z|M 9,12 L 11,14 L 15,10",
            Role.Admin),
    ];

    /// <summary>The items a person of this role sees, in the rail's order. The role decides what is offered; the pages decide what is allowed.</summary>
    public static IReadOnlyList<RailItem> For(Role role) => All.Where(i => i.Needs <= role).ToList();

    /// <summary>The icon's strokes.</summary>
    public IEnumerable<string> Strokes => Icon.Split('|');

    /// <summary>Docked to the bottom of the rail rather than the top.</summary>
    public bool AtBottom => Href is "/environment" or "/settings" or "/accounts" or "/audit";

    /// <summary>The link, relative to the page's base, as every link the pages build is (Links).</summary>
    public string Link => Href == "/" ? "/" : Href[1..];

    /// <summary>
    /// Is this base-relative path (no query, no slashes at its ends) inside the item's section - "you are here"? Each
    /// section is its address and what is under it; the Runs screen also has a run's address and a past run's, and on a
    /// past run no section was marked. Never an address another item has: both would light up on it.
    /// </summary>
    public bool IsIn(string path)
    {
        string[] prefixes = Href == "/" ? ["runs", "history"] : [Href[1..]];
        return (Href == "/" && path.Length == 0)
               || prefixes.Any(prefix => path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal));
    }
}
