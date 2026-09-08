namespace Enactive.Remote.Gateway.Services;

using Enactive.Remote.Gateway.Storage;
using MySqlConnector;

/// <summary>
/// Throws away history that is past its window, and records that it did.
///
/// <para>The recording is the part that matters. A gateway that quietly deletes a month of events
/// shows a panel that looks like a quiet month, and there is nothing on the screen to say which of
/// the two it is - which is the same failure the log retention in the desktop had, and it got the
/// same answer: say so.</para>
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
    /// Deleted per pass. A first run against a long-neglected database would otherwise be one
    /// transaction holding row locks on a hundred thousand rows while the gateway is serving.
    /// </summary>
    private const int Batch = 1000;

    public int Days { get; } = days > 0
        ? days
        : throw new ArgumentOutOfRangeException(nameof(days), days, "Retention must be at least one day.");

    /// <summary>
    /// One pass. Returns how many rows went, so the caller and a test can both tell the difference
    /// between "nothing was old enough" and "it ran".
    /// </summary>
    public async Task<int> TrimAsync(CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-Days);
        await using var connection = await database.OpenAsync(ct);

        var removed = 0;
        removed += await DeleteAsync(connection, "events", cutoff, ct);
        removed += await DeleteAsync(connection, "notices", cutoff, ct);

        if (removed == 0)
        {
            // Nothing was discarded, so nothing is missing, so the panel is told nothing. Stamping
            // the cutoff anyway would have a three-day-old gateway announce that history before
            // last month had been trimmed - true of no data that ever existed.
            return 0;
        }

        await connection.ExecuteAsync(null,
            """
            UPDATE retention_state
            SET trimmed_before = GREATEST(COALESCE(trimmed_before, @cutoff), @cutoff),
                trimmed_at = @now
            WHERE id = 1
            """,
            ("@cutoff", cutoff), ("@now", DateTimeOffset.UtcNow));

        return removed;
    }

    private static async Task<int> DeleteAsync(
        MySqlConnection connection, string table, DateTimeOffset cutoff, CancellationToken ct)
    {
        var total = 0;

        while (!ct.IsCancellationRequested)
        {
            var deleted = await connection.ExecuteAsync(null,
                $"DELETE FROM {table} WHERE at < @cutoff ORDER BY at LIMIT {Batch}",
                ("@cutoff", cutoff));

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
