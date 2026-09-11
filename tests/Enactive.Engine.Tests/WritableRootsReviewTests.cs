namespace Enactive.Engine.Tests;

using Enactive.Core.Permissions;
using Xunit;

/// <summary>
/// FIX_PLAN §9t N3: a grant a person cannot see is a grant they cannot take back.
///
/// <para><c>Revoke</c> has existed since the store was built and nothing called it, so withdrawing a
/// standing permission meant editing JSON in <c>%APPDATA%</c> by hand. That is most of the argument
/// for having the store at all, unbuilt.</para>
///
/// <para>The awkward part is deliberate and recorded in the store's own summary: the file is keyed
/// by the id the PATH gives, never the path, so a clone of a repository cannot inherit somebody's
/// grant. The store therefore cannot name the workspaces it holds grants for — it has hashes. The
/// app knows the paths, so <see cref="WritableRoots.Review"/> takes them and does the join.</para>
/// </summary>
public sealed class WritableRootsReviewTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "enactive-review", Guid.NewGuid().ToString("N"));

    private readonly string _alpha;
    private readonly string _beta;
    private readonly string _outsideA;
    private readonly string _outsideB;
    private readonly WritableRoots _roots;

    public WritableRootsReviewTests()
    {
        Directory.CreateDirectory(_dir);
        _alpha = Path.Combine(_dir, "alpha");
        _beta = Path.Combine(_dir, "beta");
        _outsideA = Path.Combine(_dir, "shared-output");
        _outsideB = Path.Combine(_dir, "reports");
        foreach (var d in new[] { _alpha, _beta, _outsideA, _outsideB })
            Directory.CreateDirectory(d);

        _roots = new WritableRoots(Path.Combine(_dir, "writable-roots.json"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp folder */ }
    }

    /// <summary>Nothing granted, nothing to review — and no empty row pretending otherwise.</summary>
    [Fact]
    public void An_empty_store_reviews_as_empty()
    {
        Assert.Empty(_roots.Review(new[] { _alpha, _beta }));
    }

    /// <summary>
    /// The ordinary case: what was granted comes back, attached to the workspace that has it, with
    /// the path a person recognises rather than the hash the file is keyed by.
    /// </summary>
    [Fact]
    public void What_a_workspace_was_granted_comes_back_under_its_own_path()
    {
        Assert.Null(_roots.Add(_alpha, _outsideA));
        Assert.Null(_roots.Add(_beta, _outsideB));

        var review = _roots.Review(new[] { _alpha, _beta });

        Assert.Equal(2, review.Count);

        var alpha = review.Single(e => string.Equals(e.WorkspaceRoot, _alpha, StringComparison.OrdinalIgnoreCase));
        Assert.False(alpha.IsOrphan);
        Assert.Equal(new[] { _outsideA }, alpha.Roots);
    }

    /// <summary>
    /// The case that matters most, and the one hiding it would get wrong. A workspace removed from
    /// the list — or moved, or renamed, all of which change its id by design — leaves a grant that
    /// is still in force. It comes back as an orphan rather than vanishing, because a standing
    /// permission nothing can see is one nobody can withdraw.
    /// </summary>
    [Fact]
    public void A_grant_whose_workspace_is_gone_is_shown_rather_than_hidden()
    {
        Assert.Null(_roots.Add(_alpha, _outsideA));
        Assert.Null(_roots.Add(_beta, _outsideB));

        // Beta is no longer a workspace the app knows about.
        var review = _roots.Review(new[] { _alpha });

        Assert.Equal(2, review.Count);

        var orphan = Assert.Single(review, e => e.IsOrphan);
        Assert.Null(orphan.WorkspaceRoot);
        Assert.Equal(new[] { _outsideB }, orphan.Roots);
        Assert.False(string.IsNullOrWhiteSpace(orphan.Key));

        // Known first, leftovers last — so the list reads the same way twice running.
        Assert.False(review[0].IsOrphan);
        Assert.True(review[^1].IsOrphan);
    }

    /// <summary>
    /// And an orphan can actually be withdrawn. Showing it and being unable to remove it would be
    /// the same failure with better manners: <c>Revoke</c> needs a workspace path, and an orphan is
    /// precisely the entry that no longer has one.
    /// </summary>
    [Fact]
    public void An_orphan_can_be_forgotten_by_the_only_handle_it_has_left()
    {
        Assert.Null(_roots.Add(_alpha, _outsideA));
        Assert.Null(_roots.Add(_beta, _outsideB));

        var orphan = Assert.Single(_roots.Review(new[] { _alpha }), e => e.IsOrphan);
        _roots.Forget(orphan.Key);

        var after = _roots.Review(new[] { _alpha });
        Assert.Single(after);
        Assert.False(after[0].IsOrphan);

        // And the grant is really gone, not merely hidden from the review.
        Assert.Empty(_roots.For(_beta));
    }

    /// <summary>
    /// Revoking one folder leaves the others standing. The screen offers a button per folder, so
    /// this is the operation behind it, and "revoke" that quietly cleared a workspace would be a
    /// worse surprise than one that did nothing.
    /// </summary>
    [Fact]
    public void Revoking_one_folder_leaves_the_rest()
    {
        Assert.Null(_roots.Add(_alpha, _outsideA));
        Assert.Null(_roots.Add(_alpha, _outsideB));

        _roots.Revoke(_alpha, _outsideA);

        var entry = Assert.Single(_roots.Review(new[] { _alpha }));
        Assert.Equal(new[] { _outsideB }, entry.Roots);
    }

    /// <summary>
    /// A workspace whose last folder was revoked disappears from the review entirely. An entry
    /// reading "this workspace may write to nothing outside itself" is the default state of every
    /// workspace there has ever been, and listing it would bury the ones that mean something.
    /// </summary>
    [Fact]
    public void A_workspace_with_nothing_left_is_not_listed()
    {
        Assert.Null(_roots.Add(_alpha, _outsideA));
        _roots.Revoke(_alpha, _outsideA);

        Assert.Empty(_roots.Review(new[] { _alpha }));
    }

    /// <summary>
    /// The review must open. A path the OS refuses to turn into an id is skipped rather than
    /// thrown on — a screen that crashes because one remembered workspace was on a drive that is
    /// gone is a screen nobody can use to clean up after that drive.
    /// </summary>
    [Fact]
    public void A_path_that_cannot_be_keyed_does_not_take_the_screen_down()
    {
        Assert.Null(_roots.Add(_alpha, _outsideA));

        var review = _roots.Review(new[] { _alpha, "", "   ", "\0not a path\0" });

        Assert.Single(review);
        Assert.Equal(_alpha, review[0].WorkspaceRoot, ignoreCase: true);
    }
}
