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
/// <para>The price is that every event and notice serialises on one row for the length of the
/// transaction that writes it. On a gateway serving one person that is not a cost worth trading an
/// invariant for.</para>
/// </summary>
internal static class StreamCursor
{
    private const string Name = "stream";

    public static async Task<long> NextAsync(MySqlConnection connection, MySqlTransaction transaction)
    {
        // The UPDATE takes the lock; the SELECT reads this transaction's own uncommitted value.
        // Both must be inside the caller's transaction, which is what makes the number and the row
        // it will be written to commit together or not at all.
        var updated = await connection.ExecuteAsync(transaction,
            "UPDATE counters SET value = value + 1 WHERE name = @name", ("@name", Name));

        if (updated != 1)
        {
            // The row is created by migration 002. Its absence means the schema is not what this
            // build expects, and continuing would write an ordinal of 0 that no delta ever returns.
            throw new InvalidOperationException(
                $"The '{Name}' counter row is missing, so no ordinal can be allocated.");
        }

        return await connection.ReadOneAsync(transaction,
            "SELECT value FROM counters WHERE name = @name",
            reader => reader.GetInt64("value"), ("@name", Name));
    }
}
