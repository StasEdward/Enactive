namespace Enactive.Remote.Gateway.Tests;

using Enactive.Remote.Gateway.Accounts;
using Enactive.Remote.Gateway.Storage;
using Xunit;

/// <summary>
/// The line of numbers a panel's poll is ordered by, one line per person.
///
/// <para>The questions here are about what the line promises. A number must never be visible before
/// everything below it is, because a poll that has seen 42 never asks for 41 again; and one person's
/// writes must neither move nor wait on another's, because the gateway serves many.</para>
/// </summary>
public sealed class CursorTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private static readonly TimeSpan Generously = TimeSpan.FromSeconds(2);

    private Database Db => new(database.ConnectionString);

    private static string NewName(string stem) => stem + Guid.NewGuid().ToString("N")[..8];

    /// <summary>One whole allocation as a writer does it: a number, taken and committed.</summary>
    private async Task<long> AllocateAsync(string ownerId)
    {
        await using var connection = await Db.OpenAsync();
        await using var transaction = await connection.BeginAsync(default);
        var ordinal = await StreamCursor.NextAsync(connection, transaction, ownerId);
        await transaction.CommitAsync();
        return ordinal;
    }

    private Task<long> CommittedValueAsync(string ownerId)
        => database.ScalarLongAsync($"SELECT value FROM user_streams WHERE owner_id = '{ownerId}'");

    /// <summary>
    /// The one this design exists for. A writer has taken a number and not committed. What a poll
    /// can read is the last COMMITTED value, strictly below it; and the next writer for the same
    /// person cannot take a number until the first has committed, so allocation order is commit
    /// order and a poll that sees N sees everything below N.
    ///
    /// <para>Shown red by an allocator that does not hold the row until commit: the second writer
    /// then gets a number while the first is in flight, and can commit first.</para>
    /// </summary>
    [Fact]
    public async Task A_cursor_never_covers_a_number_that_has_not_committed()
    {
        var alice = await TestAccounts.CreateAsync(database, NewName("alice"));

        await using var connection = await database.OpenAsync();
        await using var inFlight = await connection.BeginAsync(default);
        var ordinal = await StreamCursor.NextAsync(connection, inFlight, alice.UserId);

        // What a poll sees now: the committed line, which has not reached the in-flight number.
        Assert.Equal(ordinal - 1, await CommittedValueAsync(alice.UserId));

        // A second writer for the same person has to wait for the first to finish.
        var second = AllocateAsync(alice.UserId);
        await Assert.ThrowsAsync<TimeoutException>(() => second.WaitAsync(TimeSpan.FromMilliseconds(500)));

        await inFlight.CommitAsync();

        Assert.Equal(ordinal + 1, await second.WaitAsync(Generously));
        Assert.Equal(ordinal + 1, await CommittedValueAsync(alice.UserId));
    }

    /// <summary>
    /// The reason the counter is per owner. With one row for the gateway, Bob's write waited for
    /// every open transaction of Alice's, and one slow writer stalled everybody.
    ///
    /// <para>Shown red by pointing every owner at one counter row: Bob's allocation then blocks on
    /// Alice's transaction and the 2-second wait times out.</para>
    /// </summary>
    [Fact]
    public async Task Bobs_writes_do_not_wait_on_alices_stream()
    {
        var alice = await TestAccounts.CreateAsync(database, NewName("alice"));
        var bob = await TestAccounts.CreateAsync(database, NewName("bob"));

        await using var connection = await database.OpenAsync();
        await using var alicesTransaction = await connection.BeginAsync(default);
        await StreamCursor.NextAsync(connection, alicesTransaction, alice.UserId);

        var bobs = await AllocateAsync(bob.UserId).WaitAsync(Generously);

        Assert.Equal(1, bobs);
        await alicesTransaction.CommitAsync();
    }

    /// <summary>
    /// A person's numbers are their own. If Bob's events moved Alice's line, her panel's cursor
    /// would jump past rows that exist only for her, or her line would show gaps that tell her
    /// something about how much somebody else is doing.
    /// </summary>
    [Fact]
    public async Task Bobs_events_do_not_move_alices_cursor()
    {
        var alice = await TestAccounts.CreateAsync(database, NewName("alice"));
        var bob = await TestAccounts.CreateAsync(database, NewName("bob"));

        Assert.Equal(1, await AllocateAsync(alice.UserId));
        Assert.Equal(1, await AllocateAsync(bob.UserId));
        Assert.Equal(2, await AllocateAsync(bob.UserId));
        Assert.Equal(3, await AllocateAsync(bob.UserId));

        Assert.Equal(1, await CommittedValueAsync(alice.UserId));
        Assert.Equal(2, await AllocateAsync(alice.UserId));
        Assert.Equal(3, await CommittedValueAsync(bob.UserId));
    }

    /// <summary>
    /// A refused write gives its number back: the increment rolls back with the rest of the
    /// transaction, so a rejection leaves no hole in the person's line.
    /// </summary>
    [Fact]
    public async Task A_rolled_back_allocation_leaves_no_hole()
    {
        var alice = await TestAccounts.CreateAsync(database, NewName("alice"));

        await using (var connection = await database.OpenAsync())
        await using (var refused = await connection.BeginAsync(default))
        {
            Assert.Equal(1, await StreamCursor.NextAsync(connection, refused, alice.UserId));
            await refused.RollbackAsync();
        }

        Assert.Equal(1, await AllocateAsync(alice.UserId));
    }

    /// <summary>
    /// An owner without a stream row is a broken account. Handing out a guessed number would write
    /// an event the owner's delta could never return.
    /// </summary>
    [Fact]
    public async Task An_owner_with_no_stream_row_gets_no_number()
    {
        await using var connection = await database.OpenAsync();
        await using var transaction = await connection.BeginAsync(default);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => StreamCursor.NextAsync(connection, transaction, Ids.New()));
    }

    /// <summary>
    /// Two tabs signing in as one new person at once must make one account. The identity's key is
    /// what decides; a "look first, then create" would give both callers a user, and the person's
    /// data would end up split between two accounts that only one sign-in can reach.
    ///
    /// <para>Shown red by not catching the duplicate-key error (ten calls, nine exceptions) or by
    /// dropping the identity's primary key (ten users).</para>
    /// </summary>
    [Fact]
    public async Task Concurrent_provisioning_of_one_identity_makes_one_account()
    {
        var subject = NewName("carol");
        var accounts = new AccountService(Db, TimeProvider.System);
        var before = await database.ScalarLongAsync("SELECT COUNT(*) FROM users");

        var ids = await Task.WhenAll(Enumerable.Range(0, 10)
            .Select(_ => Task.Run(() => accounts.ProvisionAsync("test", subject, "Carol", default))));

        var userId = Assert.Single(ids.Distinct());
        Assert.Equal(before + 1, await database.ScalarLongAsync("SELECT COUNT(*) FROM users"));
        Assert.Equal(1, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM external_identities WHERE provider = 'test' AND subject = '{subject}'"));
        Assert.Equal(1, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM user_streams WHERE owner_id = '{userId}'"));
        Assert.Equal(1, await database.ScalarLongAsync(
            $"SELECT COUNT(*) FROM user_retention WHERE owner_id = '{userId}'"));
        Assert.Equal("Active", (await database.StringsAsync(
            $"SELECT status FROM users WHERE id = '{userId}'")).Single());
    }

    /// <summary>
    /// A provider's display name longer than the column is cut, not refused: refusing would lock a
    /// person with a long name out of the service for good.
    /// </summary>
    [Fact]
    public async Task A_display_name_longer_than_the_column_is_cut_and_the_account_is_made()
    {
        var accounts = new AccountService(Db, TimeProvider.System);
        // A surrogate pair straddles the cut, and must not be split.
        var longName = new string('a', 99) + "\U0001F600" + new string('b', 300);

        var userId = await accounts.ProvisionAsync("test", NewName("dave"), longName, default);

        var stored = (await database.StringsAsync($"SELECT display_name FROM users WHERE id = '{userId}'")).Single();
        Assert.Equal(new string('a', 99), stored);
    }
}
