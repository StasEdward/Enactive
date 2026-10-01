namespace Enactive.Remote.Gateway.Storage;

using MySqlConnector;

/// <summary>
/// Hands out the numbers the panel's delta poll is ordered by.
///
/// <para>Events and notices share one number line. They are one stream of things that happened,
/// stored in two tables because they are read differently, and a panel asking "what is new since
/// 41" has to be answerable without asking it twice and merging.</para>
///
/// <para><b>Why a counter row and not an AUTO_INCREMENT.</b> Auto-increment ids are allocated when
/// the row is inserted and become visible when the transaction commits, and those are not the same
/// order. A transaction that took id 41 can commit AFTER one that took 42, so a poll made in
/// between sees 42, stores it as its cursor, and never asks for anything below it again - 41 is
/// lost, once, silently. For an ApprovalRequested notice that is a permission nobody is asked for.
/// </para>
///
/// <para>Incrementing a single row instead takes an exclusive lock that is held until commit, so a
/// second writer cannot allocate until the first has committed. Allocation order therefore IS
/// commit order, and a reader that can see ordinal N can see everything below it. That is the whole
/// invariant the delta rests on, and it is a property of the lock rather than of timing.</para>
///
/// <para>The counter is a row PER OWNER. One row for the whole gateway made every user's writes wait
/// on every other user's transactions; with a row each, a person waits only on their own, and the
/// ordering guarantee is the one the delta needs, because a delta is one person's.</para>
/// </summary>
internal static class StreamCursor
{
    /// <summary>The next number on this owner's line.</summary>
    public static async Task<long> NextAsync(
        MySqlConnection connection, MySqlTransaction transaction, string ownerId)
    {
        // The UPDATE takes the lock; the SELECT reads this transaction's own uncommitted value.
        // Both must be inside the caller's transaction, which is what makes the number and the row
        // it will be written to commit together or not at all.
        var updated = await connection.ExecuteAsync(transaction,
            "UPDATE user_streams SET value = value + 1 WHERE owner_id = @owner", ("@owner", ownerId));

        if (updated != 1)
        {
            // Every account is provisioned with its stream row. Its absence is a broken account, and
            // writing with a guessed ordinal would hide an event from that owner for good.
            throw new InvalidOperationException(
                "This account has no stream row, so no ordinal can be allocated.");
        }

        return await connection.ReadOneAsync(transaction,
            "SELECT value FROM user_streams WHERE owner_id = @owner",
            reader => reader.GetInt64("value"), ("@owner", ownerId));
    }
}
