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
                removed += await TrimTableAsync(connection, owner, "events", cutoff, ct);
                removed += await TrimTableAsync(connection, owner, "notices", cutoff, ct);
            }

            if (owners.Count < Owners)
            {
                break;
            }

            after = owners[^1];
        }

        return removed;
    }

    /// <summary>
    /// One person's rows of one table, in batches.
    ///
    /// <para>Each batch is deleted in the same transaction that writes the person's marker. Written
    /// afterwards, a gateway stopped between the two left the rows gone and the panel saying nothing
    /// - the quiet month this class exists to prevent. A batch that deleted nothing writes nothing:
    /// stamping the cutoff anyway would have an account three days old announce that history before
    /// last month had been trimmed - true of no data that ever existed.</para>
    /// </summary>
    private static async Task<int> TrimTableAsync(
        MySqlConnection connection, string owner, string table, DateTimeOffset cutoff, CancellationToken ct)
    {
        var total = 0;

        while (!ct.IsCancellationRequested)
        {
            await using var transaction = await connection.BeginAsync(ct);

            var deleted = await connection.ExecuteAsync(transaction,
                $"DELETE FROM {table} WHERE owner_id = @owner AND at < @cutoff ORDER BY at LIMIT {Batch}",
                ("@owner", owner), ("@cutoff", cutoff));

            if (deleted > 0)
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
