namespace Enactive.Server.Components.Shell;

using Enactive.Server.Accounts;

/// <summary>
/// One icon of the rail. <paramref name="Href"/> is the address the item is MEANT to lead to - the desktop's sections,
/// each with a page of its own; <paramref name="Label"/> is its tooltip and its accessible name (an icon has no text
/// of its own); <paramref name="Icon"/> is the icon's path data, one stroke per '|' (24 by 24, as MainWindow.axaml
/// draws them); <paramref name="Needs"/> is the role that sees it.
/// </summary>
internal sealed record RailItem(string Href, string Label, string Icon, Role Needs)
{
    // Runs, Accounts and Audit have no icon in the window; theirs are drawn to match its 1.6 px outline style.
    private static readonly RailItem[] All =
    [
        new("/", "Runs", "M 8,5 L 19,12 L 8,19 Z", Role.Viewer),
        new("/timeline", "Timeline · project memory",
            "M 7,4 V 20|M 7,7 A 2,2 0 1 0 7.01,7 Z M 7,12 A 2,2 0 1 0 7.01,12 Z M 7,17 A 2,2 0 1 0 7.01,17 Z|M 12,7 H 19 M 12,12 H 17 M 12,17 H 20",
            Role.Viewer),
        new("/templates", "Templates · a saved task", "M 4,4 H 20 V 8 H 4 Z|M 4,11 H 20 V 15 H 4 Z|M 4,18 H 13 V 20 H 4 Z", Role.Viewer),
        new("/schedules", "Schedules · run it on a day and a time",
            "M 4,6 H 20 V 20 H 4 Z|M 4,10 H 20|M 8,4 V 7 M 16,4 V 7|M 12,13 V 16 H 15", Role.Viewer),
        new("/inbox", "Inbox", "M 3,7 H 21 V 19 H 3 Z|M 3,7 L 12,14 L 21,7", Role.Viewer),
        new("/proposals", "Proposals", "M 6,4 H 14 L 18,8 V 20 H 6 Z|M 14,4 V 8 H 18|M 9,13 H 15 M 9,16 H 13", Role.Viewer),
        new("/log", "Global log", "M 5,5 H 19 V 19 H 5 Z|M 8,9 H 16 M 8,13 H 16 M 8,17 H 13", Role.Viewer),
        // The rail's bottom: the desktop docks Environment and Settings low; Accounts and Audit sit with them.
        new("/environment", "Environment", "M 4,5 H 20 V 15 H 4 Z|M 9,19 H 15 M 12,15 V 19", Role.Viewer),
        new("/settings", "Settings",
            "M 12,8.8 A 3.2,3.2 0 1 0 12.01,8.8 Z|"
            + "M 19.43,12.98 C 19.47,12.66 19.5,12.33 19.5,12 C 19.5,11.67 19.47,11.34 19.43,11.02 L 21.54,9.37 L 19.54,5.91 L 17.05,6.91 "
            + "C 16.53,6.51 15.97,6.18 15.36,5.93 L 14.98,3.28 L 9.02,3.28 L 8.64,5.93 C 8.03,6.18 7.47,6.51 6.95,6.91 L 4.46,5.91 "
            + "L 2.46,9.37 L 4.57,11.02 C 4.53,11.34 4.5,11.67 4.5,12 C 4.5,12.33 4.53,12.66 4.57,12.98 L 2.46,14.63 L 4.46,18.09 "
            + "L 6.95,17.09 C 7.47,17.49 8.03,17.82 8.64,18.07 L 9.02,20.72 L 14.98,20.72 L 15.36,18.07 C 15.97,17.82 16.53,17.49 17.05,17.09 "
            + "L 19.54,18.09 L 21.54,14.63 Z M 12,15.5 A 3.5,3.5 0 1,1 12,8.5 A 3.5,3.5 0 1,1 12,15.5",
            Role.Admin),
        new("/accounts", "Accounts", "M 8.5,9 A 3.5,3.5 0 1 0 15.5,9 A 3.5,3.5 0 1 0 8.5,9 Z|M 5,20 C 5,16 8,14.5 12,14.5 C 16,14.5 19,16 19,20", Role.Admin),
        new("/audit", "Audit", "M 12,3 L 19,6 V 12 C 19,16 16,19 12,21 C 8,19 5,16 5,12 V 6 Z|M 9,12 L 11,14 L 15,10", Role.Admin),
    ];

    /// <summary>The items a person of this role sees, in the rail's order. The role decides what is offered; the pages decide what is allowed.</summary>
    public static IReadOnlyList<RailItem> For(Role role) => All.Where(i => i.Needs <= role).ToList();

    /// <summary>The icon's strokes.</summary>
    public IEnumerable<string> Strokes => Icon.Split('|');

    /// <summary>Docked to the bottom of the rail rather than the top.</summary>
    public bool AtBottom => Href is "/environment" or "/settings" or "/accounts" or "/audit";
}

/// <summary>
/// Where each rail item leads TODAY, while the desktop's pages are being rebuilt for the web. The rail is built for the
/// final addresses (<see cref="RailItem.Href"/>); an item whose page is not there yet would be a link to nothing, so it
/// leads to the page that does the job now, or - with no page at all - is shown disabled. Each task that builds a page
/// changes its row to the new address, and when every row is the item's own this table goes.
///
/// <para>An item is marked "you are here" by <c>Active</c>, never by an address another item shares (Templates and
/// Schedules both lead to the Runs screen for now): all three would light up on it.</para>
/// </summary>
internal static class RailAddresses
{
    /// <param name="Now">Relative to the page's base; null when there is no page yet.</param>
    /// <param name="Active">Path prefixes (relative, no leading slash) that put the item in "you are here"; "" is the root page only.</param>
    internal sealed record Where(string? Now, string[] Active);

    // Rows marked TEMPORARY lead somewhere other than their Href today.
    private static readonly Dictionary<string, Where> Table = new()
    {
        ["/"] = new("/", ["", "runs"]),
        ["/timeline"] = new(null, ["timeline"]),                                          // TEMPORARY: no page yet
        // TEMPORARY: the library needs a workspace, and the workspace list it was reached from is the Runs screen's
        // switcher now - so the Runs screen, until Templates and Schedules have pages of their own (Tasks 16, 17).
        ["/templates"] = new("/", ["templates", "library/template"]),
        ["/schedules"] = new("/", ["schedules", "library/schedule"]),
        ["/inbox"] = new("inbox", ["inbox"]),
        ["/proposals"] = new("proposals", ["proposals"]),
        ["/log"] = new("logs", ["log", "logs"]),                                         // TEMPORARY: /logs today
        ["/environment"] = new(null, ["environment"]),                                    // TEMPORARY: no page yet
        ["/settings"] = new("admin/settings", ["settings", "admin/settings"]),           // TEMPORARY: /admin/settings today
        ["/accounts"] = new("admin/users", ["accounts", "admin/users"]),                 // TEMPORARY: /admin/users today
        ["/audit"] = new("admin/audit", ["audit", "admin/audit"]),                       // TEMPORARY: /admin/audit today
    };

    public static Where Of(RailItem item) => Table[item.Href];

    /// <summary>Is this base-relative path (no query) inside the item's section?</summary>
    public static bool IsIn(RailItem item, string path)
        => Of(item).Active.Any(prefix => prefix.Length == 0 ? path.Length == 0 : path == prefix || path.StartsWith(prefix + "/", StringComparison.Ordinal));
}
