namespace Enactive.Remote.Gateway.Services;

using System.Globalization;
using System.Text;
using System.Text.Json;
using Enactive.Remote.Contracts.Crypto;
using Enactive.Remote.Gateway.Storage;
using Microsoft.AspNetCore.Antiforgery;
using MySqlConnector;

/// <summary>
/// A person taking out everything the service stores for their account, as one JSON file:
/// <c>{"exportedAt", "userId", "tables": {"hosts": [...], ...}}</c>, every table of theirs with every row of it,
/// metadata in clear and envelopes as stored. The gateway opens nothing; the panel opens what its keys can,
/// in the browser, before it offers the file.
///
/// <para><b>Every read names the owner.</b> Each query below has the account in its WHERE, as the panel's
/// snapshot does, or reaches its rows through the account's own identities (the admissions) or runs (a notice's
/// computer). There is no row of anybody else's to leave out, because none is read.</para>
///
/// <para><b>Columns are named, never <c>*</c>.</b> Two things of the person's are not theirs to get back in clear:
/// a computer's token hash, which is what its credential is checked against, and a session's id, which is what a
/// cookie names - one written into a file that is mailed around or left in a downloads folder would be a
/// credential lying about. A <c>SELECT *</c> would also take whatever column is added next, secret or not,
/// without anyone deciding it belongs in the file.</para>
///
/// <para><b>Streamed, one instant.</b> Rows are written to the response as they are read, so a long history is
/// never held whole in the gateway's memory. They are read in one REPEATABLE READ transaction, so the file is
/// one moment of the account: read table by table outside one, a run that ended while the export was being
/// written would be in the file without the events that ended it. The transaction is held for as long as the
/// download takes. The once-an-hour limit (<see cref="ExportLimit"/>) bounds how often one starts, not how long
/// one lasts: a slow download can still be open when the next hour's starts. What ends a stalled one is its
/// connection - MySQL drops a result left unread past its net_write_timeout, and Kestrel a reader slower than its
/// minimum data rate.</para>
///
/// <para><b>Asked for by the panel only.</b> This is a GET, and the session cookie goes with a top-level GET from
/// another site: a link in a mail or on a page would otherwise save the person's whole account into their
/// downloads unasked, and spend their hour so the export they then want is refused
/// (<see cref="RequirePanelAsync"/>).</para>
/// </summary>
public sealed class Export(Database database, TimeProvider clock)
{
    // Written to the network whenever this much is waiting. Without it the writer kept the whole file in its
    // buffer until the end, which is the memory streaming is there to save.
    private const int FlushAt = 32 * 1024;

    /// <summary>
    /// Every table that holds something of the person's, as the file names it, and how its rows are read. The
    /// names are the database's, in camelCase like every key of the API, so the panel finds a task's
    /// <c>hostId</c> and <c>workspaceId</c> where it finds them in a snapshot. The order is the order a reader
    /// would want: the account, its access, its computers and devices, then the work.
    /// </summary>
    private static readonly (string Table, string Sql)[] Tables =
    [
        ("users",
            "SELECT id, display_name, status, security_version, sealed_bytes, created_at FROM users WHERE id = @owner"),
        ("external_identities",
            """
            SELECT provider, subject, display, created_at FROM external_identities
            WHERE user_id = @owner ORDER BY provider, subject
            """),
        // An admission names an identity, not an account. The person's are those of their own identities;
        // anybody else's - the rest of the operator's list - is not theirs to see.
        ("admissions",
            """
            SELECT a.provider, a.subject, a.state, a.display, a.requested_at, a.decided_at
            FROM admissions a
            JOIN external_identities i ON i.provider = a.provider AND i.subject = a.subject
            WHERE i.user_id = @owner ORDER BY a.provider, a.subject
            """),
        // Without the id: see the class comment.
        ("user_sessions",
            """
            SELECT security_version, created_at, expires_at, revoked_at FROM user_sessions
            WHERE user_id = @owner ORDER BY created_at
            """),
        ("user_streams", "SELECT value, epoch FROM user_streams WHERE owner_id = @owner"),
        ("user_retention", "SELECT trimmed_before, trimmed_at FROM user_retention WHERE owner_id = @owner"),
        ("devices",
            """
            SELECT id, public_key, label, created_at, last_seen_at, revoked_at FROM devices
            WHERE owner_id = @owner ORDER BY created_at, id
            """),
        // Without the token hash: see the class comment.
        ("hosts",
            """
            SELECT id, label, key_epoch, signing_public, revoked, last_seen_at, created_at FROM hosts
            WHERE owner_id = @owner ORDER BY created_at, id
            """),
        ("host_workspaces",
            """
            SELECT host_id, workspace_id, sealed_name FROM host_workspaces
            WHERE owner_id = @owner ORDER BY host_id, workspace_id
            """),
        ("grants",
            """
            SELECT host_id, device_id, epoch, grant_json, created_at FROM grants
            WHERE owner_id = @owner ORDER BY host_id, device_id, epoch
            """),
        ("invites",
            """
            SELECT id, created_by_host, created_by_device, created_at, expires_at, consumed_at FROM invites
            WHERE owner_id = @owner ORDER BY created_at, id
            """),
        ("enrollments",
            """
            SELECT invite_id, device_id, mac, created_at, answered_at FROM enrollments
            WHERE owner_id = @owner ORDER BY created_at, invite_id
            """),
        ("tasks",
            """
            SELECT id, host_id, workspace_id, sealed, fingerprint, created_at FROM tasks
            WHERE owner_id = @owner ORDER BY created_at, id
            """),
        ("runs",
            """
            SELECT id, task_id, host_id, status, applied_sequence, created_at, ended_at, sealed_summary,
                   summary_sequence
            FROM runs WHERE owner_id = @owner ORDER BY created_at, id
            """),
        ("commands",
            """
            SELECT id, host_id, run_id, kind, payload, fingerprint, status, created_at, expires_at FROM commands
            WHERE owner_id = @owner ORDER BY created_at, id
            """),
        ("approvals",
            """
            SELECT id, host_id, run_id, tool_call_id, action_hash, remote_decidable, sealed_action, status,
                   requested_decision, created_at, expires_at
            FROM approvals WHERE owner_id = @owner ORDER BY created_at, id
            """),
        ("events",
            """
            SELECT id, host_id, run_id, sequence, kind, sealed_detail, at, ordinal FROM events
            WHERE owner_id = @owner ORDER BY ordinal
            """),
        // A notice about a run has no computer of its own; it is its run's, whose key opens the detail, read
        // through the run's owner key as the snapshot reads it. One about no run names its computer itself.
        ("notices",
            """
            SELECT n.id, n.run_id, COALESCE(r.host_id, n.host_id) AS host_id, n.kind, n.sealed_detail,
                   n.event_sequence, n.event_kind, n.at, n.is_read, n.ordinal
            FROM notices n
            LEFT JOIN runs r ON r.owner_id = n.owner_id AND r.id = n.run_id
            WHERE n.owner_id = @owner ORDER BY n.ordinal
            """),
        ("audit",
            "SELECT id, at, actor, action, target FROM audit WHERE owner_id = @owner ORDER BY at, id")
    ];

    /// <summary>
    /// Refuses with <see cref="GatewayFault.NotFromPanel"/> unless the request is the panel's: the browser says it
    /// came from this site (<c>Sec-Fetch-Site: same-origin</c>) or from the person typing the address or opening a
    /// bookmark (<c>none</c>), or it carries this session's antiforgery token, which the panel sends and no other
    /// site can read. A browser that sends no <c>Sec-Fetch-Site</c> is not trusted on that account; the token
    /// still lets its panel in. Checked before the hour is counted.
    /// </summary>
    public static async Task RequirePanelAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (context.Request.Headers["Sec-Fetch-Site"].ToString() is "same-origin" or "none")
        {
            return;
        }

        try
        {
            // ValidateRequestAsync and not IsRequestValidAsync: the latter answers yes to any GET without looking.
            await antiforgery.ValidateRequestAsync(context);
        }
        catch (AntiforgeryValidationException)
        {
            throw GatewayFault.NotFromPanel();
        }
    }

    /// <summary>The file's name for an export made at <paramref name="at"/>: the day, in UTC.</summary>
    public static string FileName(DateTimeOffset at)
        => string.Create(CultureInfo.InvariantCulture, $"enactive-export-{at.UtcDateTime:yyyy-MM-dd}.json");

    /// <summary>
    /// Writes the account <paramref name="user"/> is signed in to into <paramref name="response"/>, as a file to
    /// save. A failure part-way cannot be answered as a refusal any more - the headers have gone - so the
    /// connection is cut, and the panel gets a file that does not parse rather than one that looks whole.
    /// </summary>
    public async Task WriteAsync(UserAccess user, HttpResponse response, CancellationToken ct)
    {
        var at = clock.GetUtcNow();

        response.ContentType = "application/json; charset=utf-8";
        response.Headers.ContentDisposition = $"attachment; filename=\"{FileName(at)}\"";

        await using var connection = await database.OpenAsync(ct);
        await using var transaction = await connection.BeginAsync(ct);
        await using var writer = new Utf8JsonWriter(response.Body);

        writer.WriteStartObject();
        writer.WriteString("exportedAt", at.UtcDateTime);
        writer.WriteString("userId", user.UserId);
        writer.WriteStartObject("tables");

        foreach (var (table, sql) in Tables)
        {
            writer.WriteStartArray(Camel(table));
            await WriteRowsAsync(connection, transaction, writer, sql, user.UserId, ct);
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
        await writer.FlushAsync(ct);

        await transaction.CommitAsync(ct);
    }

    private static async Task WriteRowsAsync(
        MySqlConnection connection, MySqlTransaction transaction, Utf8JsonWriter writer, string sql, string owner,
        CancellationToken ct)
    {
        await using var command = connection.Command(transaction, sql, ("@owner", owner));
        await using var reader = await command.ExecuteReaderAsync(ct);

        var names = Enumerable.Range(0, reader.FieldCount).Select(i => Camel(reader.GetName(i))).ToArray();

        while (await reader.ReadAsync(ct))
        {
            writer.WriteStartObject();

            for (var i = 0; i < names.Length; i++)
            {
                WriteValue(writer, names[i], reader.GetValue(i));
            }

            writer.WriteEndObject();

            if (writer.BytesPending > FlushAt)
            {
                await writer.FlushAsync(ct);
            }
        }
    }

    /// <summary>
    /// One column, as the API writes its kind of value: times as ISO 8601 in UTC, keys in base64url, flags as
    /// true and false. A column of a type not listed here fails the export rather than being written as
    /// whatever its <c>ToString</c> happens to give, which would be a file that parses and says the wrong thing.
    /// </summary>
    private static void WriteValue(Utf8JsonWriter writer, string name, object value)
    {
        switch (value)
        {
            case DBNull:
                writer.WriteNull(name);
                break;
            case string text:
                writer.WriteString(name, text);
                break;
            case bool flag:
                writer.WriteBoolean(name, flag);
                break;
            case int or long or uint or short or ushort or byte or sbyte:
                writer.WriteNumber(name, Convert.ToInt64(value, CultureInfo.InvariantCulture));
                break;
            case ulong number:
                writer.WriteNumber(name, number);
                break;
            case DateTime stamp:
                // Stored as UTC without saying so (Sql.Command converts on the way in).
                writer.WriteString(name, DateTime.SpecifyKind(stamp, DateTimeKind.Utc));
                break;
            case byte[] bytes:
                writer.WriteString(name, B64.Url(bytes));
                break;
            default:
                throw new InvalidOperationException(
                    $"The export does not know how to write '{name}', a {value.GetType().Name}.");
        }
    }

    /// <summary><c>host_id</c> as <c>hostId</c>: the database's names in the API's case.</summary>
    private static string Camel(string snake)
    {
        var name = new StringBuilder(snake.Length);
        var upper = false;

        foreach (var c in snake)
        {
            if (c == '_')
            {
                upper = true;
                continue;
            }

            name.Append(upper ? char.ToUpperInvariant(c) : c);
            upper = false;
        }

        return name.ToString();
    }
}
