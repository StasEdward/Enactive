namespace Enactive.Server.Components.Runs;

using System.Security.Claims;
using Enactive.Server.Accounts;
using Enactive.Server.Runs;

/// <summary>
/// One run's kept proposals as a person reads and decides them - the Artifacts tab's (RunArtifacts) and the proposals page's
/// (ProposalsScreen) alike, so there is one Apply and one Reject, not a copy per screen. A plain class, so what it decides
/// is tested without a page; it offers by role, and every action is still ServerStaging's to decide, which checks the
/// account again at the action.
/// </summary>
internal sealed class StagedSetState(ServerStaging staging, ClaimsPrincipal user, string workspace, string set)
{
    /// <summary>The set's header, null when nothing is kept (or it cannot be read - see <see cref="Problem"/>).</summary>
    public StagedSet? Set { get; private set; }

    public IReadOnlyList<StagedFile> Files { get; private set; } = [];

    /// <summary>Why there is nothing to show, when the service said.</summary>
    public string? Problem { get; private set; }

    /// <summary>What the service said to the last Apply or Reject.</summary>
    public string? Said { get; private set; }

    /// <summary>A decision is on its way: a second press is not a second apply.</summary>
    public bool Busy { get; private set; }

    /// <summary>Counted from the files, not the header: after the last decision the service has dropped the set.</summary>
    public int Pending => Files.Count(f => !f.Applied && !f.Rejected);

    /// <summary>
    /// Apply and Reject are offered: to an Operator, once the run has ended - while it is writing, the store is not to be
    /// decided from under it, and the service refuses then anyway.
    /// </summary>
    public bool CanDecide => ServerAuth.RoleOf(user) >= Role.Operator && Set is { Running: false };

    public void Load()
    {
        (Set, Files, Problem) = staging.Open(user, workspace, set);
    }

    /// <summary>Applies or rejects one change, and reads the set again.</summary>
    public async Task ActAsync(StagedFile file, bool apply, CancellationToken ct)
    {
        if (Busy)
            return;
        Busy = true;
        try
        {
            Said = await staging.ActAsync(user, workspace, set, file.Id, apply, ct);
            var (before, header) = (Files, Set);
            Load();
            // Every proposal decided, the service drops the set - and read again, the list emptied under the person's eyes,
            // the change they had just applied gone with it. Only this action can have decided the last one, and only if it
            // went through (ServerStaging says "Applied: ..." / "Rejected: ..." then): the list stays, with this change as
            // it now is.
            if (Files.Count == 0 && before.Count > 0 && Said.StartsWith(apply ? "Applied:" : "Rejected:", StringComparison.Ordinal))
            {
                Files = before.Select(p => p.Id == file.Id ? p with { Applied = apply, Rejected = !apply } : p).ToList();
                Set = header;
                Problem = null;
            }
        }
        finally
        {
            Busy = false;
        }
    }
}
