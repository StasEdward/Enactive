namespace Enactive.Application;

using Enactive.Core.Schedules;

/// <summary>
/// Where the schedules form's changes to what is saved go: Save, Enable / Disable and Delete (SchedulesViewModel).
///
/// <para>A seam because the two hosts keep schedules under different rules. The window writes its own store, as the
/// person at the machine (<see cref="StoredScheduleChanges"/>). The service has to ask who is asking first - an
/// Operator's, a Viewer's not - and put it in the audit trail; written straight to the store from the form, a page a
/// Viewer had open would have saved, switched and deleted schedules the service's own check never saw.</para>
/// </summary>
internal interface IScheduleChanges
{
    /// <summary>Keeps a schedule ScheduleDrafts passed. Null when it is kept; otherwise why not, to show the person.</summary>
    string? Save(Schedule schedule);

    /// <summary>Turns a saved schedule on or off. Null when done; otherwise why not.</summary>
    string? Switch(Schedule schedule, bool on);

    /// <summary>Forgets a saved schedule. Null when done; otherwise why not.</summary>
    string? Remove(Schedule schedule);
}

/// <summary>The window's: straight into the store, as the person at the machine - nobody else is asking.</summary>
internal sealed class StoredScheduleChanges(ScheduleStore store) : IScheduleChanges
{
    public string? Save(Schedule schedule)
    {
        store.Save(schedule);
        return null;
    }

    public string? Switch(Schedule schedule, bool on)
    {
        store.Save(schedule with { Enabled = on });
        return null;
    }

    public string? Remove(Schedule schedule)
    {
        store.Remove(schedule.WorkspaceRoot, schedule.Id);
        return null;
    }
}
