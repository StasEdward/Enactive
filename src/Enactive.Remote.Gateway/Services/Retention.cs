namespace Enactive.Remote.Gateway.Services;

using Enactive.Remote.Gateway.Storage;
using MySqlConnector;

/// <summary>
/// Throws away history that is past its window, and records that it did - for each person, in that
/// person's own marker.
///
/// <para>The recording is the part that matters. A gateway that quietly deletes a month of events
/// shows a panel that looks like a quiet month, and there is nothing on the screen to say which of
/// the two it is - which is the same failure the log retention in the desktop had, and it got the
/// same answer: say so.</para>
///
/// <para><b>Per owner.</b> Every DELETE names one person and reads through a key that starts with
/// them, and only that person's marker is written. One marker for the gateway told Alice her history
/// had been trimmed when only Bob's was; and a DELETE over everybody's rows at once is one statement
/// whose locks reach into every account that is writing.</para>
///
/// <para><b>The line is never rewound.</b> Trimming deletes rows and nothing else: a person's
/// <c>user_streams</c> value stays where it is, so no ordinal is handed out twice and a cursor from
/// before the trim is still a cursor of the same line.</para>
///
/// <para><b>The bytes are given back.</b> An account's sealed history counts against its storage limit
/// (<see cref="Limits.SealedBytesPerUser"/>), so each batch takes the size of exactly the rows it deleted off
/// the account's total, in the same transaction. Without it an account filled up once and stayed full, however
/// much of its history had gone.</para>
///
/// <para><b>The audit trail has a window of its own.</b> It is not the panel's history, so trimming it does
/// not write the person's marker, and it is kept longer: see <see cref="AuditDays"/>.</para>
///
/// <para><b>Trimming events cannot resurrect a duplicate.</b> A Host that replayed an event whose
/// row had been deleted would get past the id check - but not past the run's
/// <c>applied_sequence</c>, which is on the run row, is never trimmed, and refuses anything at or
/// below the high-water mark with a Drop code. So the replay is dropped rather than applied twice,
/// and the outbox lets go of it. That backstop is why events can be deleted at all.</para>
/// </summary>
public sealed class Retention(Database database, int days)
{
    /// <summary>
    /// Deleted per statement. A first run against a long-neglected account would otherwise be one
    /// transaction holding row locks on a hundred thousand rows while the gateway is serving.
    /// </summary>
    private const int Batch = 1000;

    /// <summary>
    /// Accounts read per page. Reading every account at once would hold a list the size of the
    /// service in memory for the length of the pass.
    /// </summary>
    private const int Owners = 500;

    /// <summary>
    /// How long the audit trail is kept. Every registration, removal and invitation writes a row, so without
    /// a window a script that adds and removes devices grew the table for ever. Longer than the history's
    /// month, because what happened to an account - a device removed, a computer revoked - is still worth
    /// reading when the person notices weeks later; a season is long enough for that.
    /// </summary>
    public const int AuditDays = 90;

    public int Days { get; } = days > 0
        ? days
        : throw new ArgumentOutOfRangeException(nameof(days), days, "Retention must be at least one day.");

    /// <summary>
    /// One pass over every account. Returns how many rows went, so the caller and a test can both
    /// tell the difference between "nothing was old enough" and "it ran".
    /// </summary>
    public async Task<int> TrimAsync(CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Days);
        var auditCutoff = DateTimeOffset.UtcNow.AddDays(-AuditDays);
        await using var connection = await database.OpenAsync(ct);

        var removed = 0;
        var after = "";

        while (!ct.IsCancellationRequested)
        {
            // Keyset paging on the primary key: each page is a range of the index, and an account
            // created during the pass is visited at most once, or left to the next pass.
            var owners = await connection.ReadAllAsync(null,
                $"SELECT owner_id FROM user_retention WHERE owner_id > @after ORDER BY owner_id LIMIT {Owners}",
                reader => reader.GetString("owner_id"), ("@after", after));

            foreach (var owner in owners)
            {
                removed += await TrimTableAsync(connection, owner, Events, cutoff, ct);
                removed += await TrimTableAsync(connection, owner, Notices, cutoff, ct);
                removed += await TrimTableAsync(connection, owner, Audit, auditCutoff, ct);
            }

            if (owners.Count < Owners)
            {
                break;
            }

            after = owners[^1];
        }

        return removed;
    }

    /// <summary>What is trimmed: a table, the key its rows are read through, and what trimming it writes.</summary>
    /// <param name="Key">The table's key on (owner_id, at).</param>
    /// <param name="Order">
    /// The time and then the primary key: the batch that is measured and the batch that is deleted are
    /// the same rows only if the order leaves no ties to break differently. The primary key is the tail of
    /// every secondary key in InnoDB, so the order is still a read of the time key.
    /// </param>
    /// <param name="Sealed">The sealed column whose size is given back, or null for a table with none.</param>
    /// <param name="History">Whether this is the panel's history, whose trimming the person is told of.</param>
    internal sealed record Trimmed(string Table, string Key, string Order, string? Sealed, bool History);

    internal static readonly Trimmed Events =
        new("events", "ix_events_owner_at", "at, host_id, id", "sealed_detail", History: true);

    internal static readonly Trimmed Notices =
        new("notices", "ix_notices_owner_at", "at, id", "sealed_detail", History: true);

    internal static readonly Trimmed Audit = new("audit", "ix_audit_owner", "at, id", null, History: false);

    /// <summary>
    /// One person's rows of one table, in batches.
    ///
    /// <para>Each batch is deleted in the same transaction that writes the person's marker. Written
    /// afterwards, a gateway stopped between the two left the rows gone and the panel saying nothing
    /// - the quiet month this class exists to prevent. A batch that deleted nothing writes nothing:
    /// stamping the cutoff anyway would have an account three days old announce that history before
    /// last month had been trimmed - true of no data that ever existed.</para>
    ///
    /// <para>A table with sealed rows is trimmed under the account's lock, taken first, as every path that
    /// writes those rows takes it. The batch is measured and then deleted, and the lock keeps a new row out
    /// of it in between. And without the lock first, a computer's publish holding the account and inserting
    /// into the range this batch had locked would wait for the batch, while the batch waited for the account
    /// to take its bytes back - a deadlock.</para>
    /// </summary>
    private static async Task<int> TrimTableAsync(
        MySqlConnection connection, string owner, Trimmed table, DateTimeOffset cutoff, CancellationToken ct)
    {
        var total = 0;

        while (!ct.IsCancellationRequested)
        {
            await using var transaction = await connection.BeginAsync(ct);

            long bytes = 0;

            if (table.Sealed is not null)
            {
                // Gone since the page of owners was read: its rows went with it.
                if (!await connection.ExistsAsync(transaction,
                        "SELECT 1 FROM users WHERE id = @owner FOR UPDATE", ("@owner", owner)))
                {
                    await transaction.CommitAsync(ct);
                    return total;
                }

                var sizes = await connection.ReadAllAsync(transaction, MeasureBatch(table),
                    reader => reader.IsDBNull(0) ? 0L : reader.GetInt64(0),
                    ("@owner", owner), ("@cutoff", cutoff));
                bytes = sizes.Sum();
            }

            var deleted = await connection.ExecuteAsync(transaction, DeleteBatch(table),
                ("@owner", owner), ("@cutoff", cutoff));

            if (deleted > 0 && bytes > 0)
            {
                await connection.ExecuteAsync(transaction,
                    "UPDATE users SET sealed_bytes = sealed_bytes - @bytes WHERE id = @owner",
                    ("@bytes", bytes), ("@owner", owner));
            }

            if (deleted > 0 && table.History)
            {
                await connection.ExecuteAsync(transaction,
                    """
                    UPDATE user_retention
                    SET trimmed_before = GREATEST(COALESCE(trimmed_before, @cutoff), @cutoff),
                        trimmed_at = @now
                    WHERE owner_id = @owner
                    """,
                    ("@cutoff", cutoff), ("@now", DateTimeOffset.UtcNow), ("@owner", owner));
            }

            await transaction.CommitAsync(ct);
            total += deleted;

            if (deleted < Batch)
            {
                return total;
            }
        }

        return total;
    }

    /// <summary>
    /// One batch of one person's oldest rows, through the table's key on (owner_id, at): the batch is
    /// the first stretch of one range of that key, read in order, and only what it reads is locked.
    ///
    /// <para>The key is named in a hint because the optimizer did not always take it. On a neglected
    /// account, whose old rows are a large share of the table, it chose to read the whole table and
    /// sort it - and a DELETE at REPEATABLE READ locks every row it reads, so the batch locked every
    /// account's rows until it committed. A single-table DELETE takes no FORCE INDEX, so this is the
    /// optimizer hint, naming the table's own key.</para>
    /// </summary>
    internal static string DeleteBatch(Trimmed table)
        => $"DELETE /*+ INDEX({table.Table} {table.Key}) */ FROM {table.Table} "
           + $"WHERE owner_id = @owner AND at < @cutoff ORDER BY {table.Order} LIMIT {Batch}";

    /// <summary>
    /// The size of each sealed field in the batch <see cref="DeleteBatch"/> deletes next: the same rows, read
    /// through the same key in the same order, locked so they are still there for the delete.
    /// </summary>
    internal static string MeasureBatch(Trimmed table)
        => $"SELECT LENGTH({table.Sealed}) FROM {table.Table} FORCE INDEX ({table.Key}) "
           + $"WHERE owner_id = @owner AND at < @cutoff ORDER BY {table.Order} LIMIT {Batch} FOR UPDATE";
}

/// <summary>
/// Runs <see cref="Retention.TrimAsync"/> on a timer, and is the only thing that does. The trim
/// itself is a plain method so a test can run one pass and look at the result rather than wait for
/// an hour to pass.
/// </summary>
public sealed class RetentionLoop(Retention retention, ILogger<RetentionLoop> log)
    : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stopping)
    {
        using var timer = new PeriodicTimer(Every);

        do
        {
            try
            {
                var removed = await retention.TrimAsync(stopping);

                if (removed > 0)
                {
                    log.LogInformation(
                        "Retention removed {Removed} rows older than {Days} days.", removed, retention.Days);
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                // A failed trim is not a reason to stop serving. It is a reason to say so and try
                // again in an hour, because the alternative is a background task that died months
                // ago and a database nobody knows is still growing.
                log.LogError(error, "Retention pass failed; the next one is in {Every}.", Every);
            }
        }
        while (await timer.WaitForNextTickAsync(stopping));
    }
}
