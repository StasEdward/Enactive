namespace Enactive.Engine.Tests;

using System.Text;
using Enactive.Core.Artifacts;
using Enactive.Core.Permissions;
using Enactive.Core.Tools;
using Enactive.Tools;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// The write journal, judged on SEQUENCES rather than on single operations.
///
/// <para>Five of these came from an outside review on 2026-09-08 (N1-N5 in
/// <c>Docs/Enactive_Code_Review_2026-09-08.md</c>), which found them by running orders of operations
/// nobody had written down. All five were real, and the suite of 742 was green throughout: each one
/// needs two things to happen in a particular order, and every test until now exercised one thing at
/// a time. They are ported here under the reviewer's own names so the report and the suite speak of
/// the same defects.</para>
///
/// <para>They are five faces of one thing. The journal had no model of versions: entries were
/// addressed by their POSITION in a list that shifts when anything is removed, keyed by whatever
/// STRING the caller happened to pass, guarded by the LAST owner on a path rather than by whether
/// anyone else wrote it at all, and hashed as TEXT rather than as the bytes on disk. The plan for
/// the fixes is <c>Docs/REVERT_INTEGRITY_PLAN.md</c>.</para>
/// </summary>
public sealed class RevertIntegrityTests
{
    private static Task<ArtifactRef> Write(IArtifactStore store, string path, string text)
        => Bytes(store, path, Encoding.UTF8.GetBytes(text));

    private static Task<ArtifactRef> Bytes(IArtifactStore store, string path, byte[] bytes)
        => store.CreateAsync(
            path, ArtifactKind.FileSet, path,
            async output => await output.WriteAsync(bytes),
            default);

    // ── N2 — a checkpoint that moves ────────────────────────────────────────

    /// <summary>
    /// Two steps, two different files, no path conflict at all — and the second revert does nothing.
    /// A scope's checkpoint was the journal's LENGTH when the scope opened, and reverting the first
    /// scope removed its entries, sliding the second scope's checkpoint past its own write.
    ///
    /// <para>This is the one that needs no parallelism and no unusual spelling: two steps rejected in
    /// one run reaches it, and <c>RevertRejectedSteps</c> is on by default.</para>
    /// </summary>
    [Fact]
    public async Task RemovingEarlierJournalEntryMustNotBreakLaterRevert()
    {
        using var fixture = new EngineFixture();
        fixture.Write("a.txt", "original A");
        fixture.Write("b.txt", "original B");

        var a = fixture.Artifacts.BeginStep();
        await Write(a, "a.txt", "rejected A");

        var b = fixture.Artifacts.BeginStep();
        await Write(b, "b.txt", "rejected B");

        await a.RevertAsync(a.TouchedPaths, default);
        await b.RevertAsync(b.TouchedPaths, default);

        Assert.Equal("original B", fixture.Read("b.txt"));
    }

    /// <summary>
    /// The same defect from the other end: a path a revert was ASKED about, and did nothing to, must
    /// come back in the report. It used to fall out of the loop with a bare <c>continue</c> — in
    /// neither <c>Reverted</c> nor <c>Kept</c> — so the caller was told the revert had done its job.
    /// An absence is not an answer.
    /// </summary>
    [Fact]
    public async Task A_path_a_revert_did_nothing_about_is_still_reported()
    {
        using var fixture = new EngineFixture();
        fixture.Write("a.txt", "original A");
        fixture.Write("b.txt", "original B");

        var a = fixture.Artifacts.BeginStep();
        await Write(a, "a.txt", "rejected A");

        var b = fixture.Artifacts.BeginStep();
        await Write(b, "b.txt", "rejected B");

        await a.RevertAsync(a.TouchedPaths, default);
        var second = await b.RevertAsync(b.TouchedPaths, default);

        Assert.Contains(
            "b.txt",
            second.Reverted.Concat(second.Kept));
    }
}
