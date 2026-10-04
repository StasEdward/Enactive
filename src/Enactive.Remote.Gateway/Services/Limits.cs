namespace Enactive.Remote.Gateway.Services;

using Enactive.Remote.Gateway.Storage;
using MySqlConnector;

/// <summary>
/// How much one account may hold or start. A public service shared by many people needs a ceiling on
/// each, or one account's runaway script fills the database and the queue for everybody.
///
/// <para>Each is checked where the thing is created, inside the creating transaction and after the
/// account's row is locked (see <see cref="Quota"/>), so ten requests made at once cannot all read
/// "one place left" and all take it.</para>
/// </summary>
public sealed record Limits(
    int HostsPerUser,
    int DevicesPerUser,
    int ActiveRunsPerUser,
    int QueuedCommandsPerHost,
    int TasksPerDay,
    int OpenInvitesPerUser,
    long SealedBytesPerUser)
{
    /// <summary>
    /// No ceiling on anything. For tests about something else; the gateway itself reads
    /// <see cref="FromConfiguration"/>, and refuses open admission with these.
    /// </summary>
    public static Limits Unlimited { get; } = new(
        int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, int.MaxValue, long.MaxValue);

    /// <summary>
    /// What ending a run may store: its last event, the copy kept as the run's summary, and the copy a notice
    /// carries - three envelopes, each at most <see cref="HostService.MaxSealedDetail"/>.
    /// </summary>
    internal const long EndingBytes = 3L * HostService.MaxSealedDetail;

    /// <summary>
    /// The room kept past <see cref="SealedBytesPerUser"/> for ending the runs that can be in progress at
    /// once. Only a run's end may use it, so a full account's runs still end, and the account is bounded all
    /// the same: no more than the limit and this.
    /// </summary>
    // The count is an int and EndingBytes a long: the product fits in 64 bits even at the largest count.
    public long EndingReserve => ActiveRunsPerUser * EndingBytes;

    /// <summary>
    /// The starting values. Guesses at what one person uses, generous enough not to be met by ordinary
    /// use, to be revised from measurement once people use the service; each can be set without a build.
    /// </summary>
    public static Limits Defaults { get; } = new(
        HostsPerUser: 5,
        DevicesPerUser: 10,
        ActiveRunsPerUser: 3,
        QueuedCommandsPerHost: 50,
        TasksPerDay: 200,
        OpenInvitesPerUser: 5,
        SealedBytesPerUser: 200L * 1024 * 1024);

    public const string HostsSetting = "ENACTIVE_LIMIT_HOSTS_PER_USER";
    public const string DevicesSetting = "ENACTIVE_LIMIT_DEVICES_PER_USER";
    public const string ActiveRunsSetting = "ENACTIVE_LIMIT_ACTIVE_RUNS_PER_USER";
    public const string QueuedCommandsSetting = "ENACTIVE_LIMIT_QUEUED_COMMANDS_PER_HOST";
    public const string TasksPerDaySetting = "ENACTIVE_LIMIT_TASKS_PER_DAY";
    public const string OpenInvitesSetting = "ENACTIVE_LIMIT_OPEN_INVITES_PER_USER";
    public const string SealedBytesSetting = "ENACTIVE_LIMIT_SEALED_BYTES_PER_USER";

    /// <summary>
    /// The limits from <c>ENACTIVE_LIMIT_*</c>, each falling back to <see cref="Defaults"/> when unset.
    ///
    /// <para>A value that is not a whole number, or is below one, stops the start with a sentence naming
    /// the setting. Read as the default instead, a typo in a limit would go unnoticed until somebody hit a
    /// ceiling nobody chose; and a zero would refuse every account its first computer.</para>
    /// </summary>
    public static Limits FromConfiguration(IConfiguration configuration) => new(
        (int)Read(configuration, HostsSetting, Defaults.HostsPerUser, int.MaxValue),
        (int)Read(configuration, DevicesSetting, Defaults.DevicesPerUser, int.MaxValue),
        (int)Read(configuration, ActiveRunsSetting, Defaults.ActiveRunsPerUser, int.MaxValue),
        (int)Read(configuration, QueuedCommandsSetting, Defaults.QueuedCommandsPerHost, int.MaxValue),
        (int)Read(configuration, TasksPerDaySetting, Defaults.TasksPerDay, int.MaxValue),
        (int)Read(configuration, OpenInvitesSetting, Defaults.OpenInvitesPerUser, int.MaxValue),
        Read(configuration, SealedBytesSetting, Defaults.SealedBytesPerUser, long.MaxValue));

    private static long Read(IConfiguration configuration, string setting, long fallback, long max)
    {
        var value = configuration[setting];

        if (string.IsNullOrWhiteSpace(value))
        {
            return fallback;
        }

        if (!long.TryParse(value.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed < 1 || parsed > max)
        {
            throw new InvalidOperationException(
                $"{setting} is '{value}'; it must be a whole number from 1 to {max}.");
        }

        return parsed;
    }
}

/// <summary>
/// The account's lock, and the counts taken under it.
///
/// <para>Every check here comes after <see cref="LockAccountAsync"/> in the same transaction. Counting
/// alone does not stop two creations that both read "one place left" and both insert. And the lock comes
/// first in the transaction, before any row of the account's: the first plain read of a REPEATABLE READ
/// transaction fixes its snapshot, so a count read before the lock would not see a creation that committed
/// while this one waited; and the account first is the lock order every path keeps (see
/// <see cref="UserService"/> and <see cref="HostService"/>).</para>
/// </summary>
internal static class Quota
{
    /// <summary>
    /// Locks the account's row for update. Shared would not do: an insert into a table with a foreign key to
    /// the account takes a shared lock on this row, and two transactions each holding it shared and then
    /// wanting it exclusive - to count, or to add to its byte total - deadlock instead of queueing.
    /// </summary>
    public static async Task LockAccountAsync(
        MySqlConnection connection, MySqlTransaction transaction, string ownerId)
    {
        // The session is checked at the door, so the account can only be gone if it was deleted since; a
        // clean refusal, where a later insert would have failed on its foreign key as a 500.
        if (!await connection.ExistsAsync(transaction,
                "SELECT 1 FROM users WHERE id = @owner FOR UPDATE", ("@owner", ownerId)))
        {
            throw GatewayFault.Unauthenticated();
        }
    }

    /// <summary>
    /// Adds <paramref name="bytes"/> of sealed text the PERSON is creating to the account's total, or refuses
    /// when it would pass the limit. The caller holds the account's lock and writes the rows in the same
    /// transaction, so the total and the rows commit together or not at all.
    ///
    /// <para>The sealed columns are ASCII, so a string's length is its size in bytes, which is what
    /// <c>LENGTH()</c> gives back when retention subtracts them again.</para>
    /// </summary>
    /// <param name="retentionDays">How long until retention gives space back, for the refusal to say.</param>
    public static async Task ChargeSealedAsync(
        MySqlConnection connection, MySqlTransaction transaction, string ownerId, long bytes, Limits limits,
        int retentionDays)
    {
        if (bytes <= 0)
        {
            return;
        }

        var held = await connection.ReadOneAsync(transaction,
            "SELECT sealed_bytes FROM users WHERE id = @owner FOR UPDATE",
            reader => reader.GetInt64(0), ("@owner", ownerId));

        limits = await QuotaSettings.ResolveAsync(connection, transaction, ownerId, limits);
        if (bytes > limits.SealedBytesPerUser - held)
        {
            throw GatewayFault.StorageFull(limits.SealedBytesPerUser, retentionDays);
        }

        await AddSealedAsync(connection, transaction, ownerId, bytes);
    }

    /// <summary>
    /// Adds what a COMPUTER stores about a run to the account's total, or refuses it when the account is full.
    ///
    /// <para>Two earlier rules each failed one way. Refusing every report at the limit, with the code a
    /// computer waits out, stopped its whole outbox: the run's end waited behind a progress line, the run
    /// stayed "running" on the panel and kept its active-run place. Never refusing any (ruling I2 of Task
    /// 8.1) let one registered computer with one run write without end - over 2 MB into an account limited
    /// to 4 KB in the review of 2026-10-03, by sending progress; the rate limit only set how fast the shared
    /// disk filled.</para>
    ///
    /// <para>So: a report of a run IN PROGRESS - progress, a permission request, the notice either raises -
    /// is refused once it would pass the limit, with <see cref="FaultCode.StorageFull"/>, which the computer
    /// drops and goes on from. What ENDS a run is admitted while it fits <see cref="Limits.EndingReserve"/>
    /// past the limit, room no other report can use; so a full account's runs still end, and the account
    /// stays bounded by its admitted work. A later quota reduction preserves existing bytes and still
    /// permits the bounded final reports of those existing runs. Retention gives the bytes back with the run.</para>
    /// </summary>
    /// <param name="ending">Whether this is stored for the event that ends the run.</param>
    public static async Task AdmitFromComputerAsync(
        MySqlConnection connection, MySqlTransaction transaction, string ownerId, long bytes, Limits limits,
        bool ending)
    {
        if (bytes <= 0)
        {
            return;
        }

        limits = await QuotaSettings.ResolveAsync(connection, transaction, ownerId, limits);
        // No limit, no question - and no arithmetic on long.MaxValue.
        if (limits.SealedBytesPerUser != long.MaxValue)
        {
            var held = await connection.ReadOneAsync(transaction,
                "SELECT sealed_bytes FROM users WHERE id = @owner FOR UPDATE",
                reader => reader.GetInt64(0), ("@owner", ownerId));

            var reserve = ending ? limits.EndingReserve : 0;
            var room = reserve >= long.MaxValue - limits.SealedBytesPerUser
                ? long.MaxValue
                : limits.SealedBytesPerUser + reserve;

            // Reductions preserve existing bytes. Each terminal event is admitted once by
            // the run lifecycle, with at most three bounded envelopes; existing runs must
            // still finish even when their old usage exceeds the newly selected ceiling.
            if (bytes > room - held && !(ending && bytes <= Limits.EndingBytes))
            {
                throw GatewayFault.HistoryFull();
            }
        }

        await AddSealedAsync(connection, transaction, ownerId, bytes);
    }

    /// <summary>Adds to the account's total what has already been admitted, or is the person's own and checked.</summary>
    public static Task AddSealedAsync(
        MySqlConnection connection, MySqlTransaction transaction, string ownerId, long bytes)
        => bytes <= 0
            ? Task.CompletedTask
            : connection.ExecuteAsync(transaction,
                "UPDATE users SET sealed_bytes = sealed_bytes + @bytes WHERE id = @owner",
                ("@bytes", bytes), ("@owner", ownerId));

    /// <summary>The length of each sealed field, none counting nothing.</summary>
    public static long SizeOf(params string?[] sealedFields)
        => sealedFields.Sum(field => (long)(field?.Length ?? 0));
}
