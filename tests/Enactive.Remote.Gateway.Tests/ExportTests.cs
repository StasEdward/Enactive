namespace Enactive.Remote.Gateway.Tests;

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway.Accounts;
using Enactive.Remote.Gateway.Storage;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

/// <summary>
/// A person taking their data out (<c>GET /api/export</c>): every row the account owns, metadata and envelopes,
/// and nothing of anybody else's.
///
/// <para>Which tables must be in it is read from the schema itself - every table with an owner_id or a user_id
/// column - so a table added later fails here until the export writes it, rather than being left out of a file
/// that says it is everything.</para>
/// </summary>
public sealed class ExportTests(TestDatabase database) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private WebApplicationFactory<Program> _gateway = null!;

    private Database Db => new(database.ConnectionString);

    private static string Name(string stem) => stem + "-" + Guid.NewGuid().ToString("N")[..8];

    public Task InitializeAsync()
    {
        _gateway = TestGateway.Create(database);
        return Task.CompletedTask;
    }

    public Task DisposeAsync() => _gateway.DisposeAsync().AsTask();

    /// <summary>
    /// Alice's file holds a row for each of her rows in every owned table, her account, and the admission of the
    /// identity she signed in with; every id of hers is in it, her envelopes are in it as stored, and nothing
    /// of Bob's is - not an id, not his name, not his computer's label. What she must not get back is not in
    /// it either: her computer's token hash and the ids of her sessions, which are what a cookie and a
    /// computer's credential are checked against.
    /// </summary>
    [Fact]
    public async Task The_export_contains_everything_of_the_user_and_nothing_of_another()
    {
        var aliceName = Name("alice");
        var bobName = Name("bob");
        using var alice = await PanelClient.SignedInAsync(_gateway, aliceName);
        using var bob = await PanelClient.SignedInAsync(_gateway, bobName);
        await SeedEverythingAsync(alice, aliceName);
        await SeedEverythingAsync(bob, bobName);

        using var response = await alice.SendAsync(HttpMethod.Get, "/api/export");
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, text);

        using var document = JsonDocument.Parse(text);
        var root = document.RootElement;
        var exportedAt = DateTimeOffset.Parse(root.GetProperty("exportedAt").GetString()!, CultureInfo.InvariantCulture);
        Assert.Equal(alice.UserId, root.GetProperty("userId").GetString());

        // A file to keep, never a page to cache: it is the person's whole account.
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        Assert.Equal($"enactive-export-{exportedAt.UtcDateTime:yyyy-MM-dd}.json",
            response.Content.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());

        var tables = root.GetProperty("tables");

        // Every owned table, with exactly Alice's rows of it, and every id of hers among them.
        foreach (var (table, column) in await OwnerColumnsAsync())
        {
            var rows = Rows(tables, table);
            var owned = await database.ScalarLongAsync($"SELECT COUNT(*) FROM `{table}` WHERE `{column}` = '{alice.UserId}'");
            Assert.True(owned > 0, $"{table} holds nothing of Alice's to export.");
            Assert.True(rows.Count == owned, $"{table}: {rows.Count} row(s) exported, {owned} stored.");

            if (table != "user_sessions" && await HasColumnAsync(table, "id"))
            {
                var ids = await database.StringsAsync($"SELECT CAST(id AS CHAR) FROM `{table}` WHERE `{column}` = '{alice.UserId}'");
                var exported = rows.Select(row => row.GetProperty("id").ToString()).ToHashSet();
                Assert.All(ids, id => Assert.Contains(id, exported));
            }
        }

        // The account itself, and the admission of her own identity - not anybody else's.
        Assert.Equal(alice.UserId, Assert.Single(Rows(tables, "users")).GetProperty("id").GetString());
        Assert.Equal(aliceName, Assert.Single(Rows(tables, "admissions")).GetProperty("subject").GetString());

        // Envelopes as stored: the panel opens them, the gateway cannot.
        Assert.Contains($"sealed-task-{aliceName}", text);

        // Nothing of Bob's: his account, his name (the identity, the admission, the display name), his labels,
        // his envelopes, and every id he owns.
        Assert.DoesNotContain(bob.UserId, text);
        Assert.DoesNotContain(bobName, text);
        foreach (var (table, column) in await OwnerColumnsAsync())
        {
            if (await HasColumnAsync(table, "id") && table != "audit")
            {
                foreach (var id in await database.StringsAsync($"SELECT id FROM `{table}` WHERE `{column}` = '{bob.UserId}'"))
                {
                    Assert.DoesNotContain(id, text);
                }
            }
        }

        // What she must not get back in clear: the token hash a computer's credential is checked against, and
        // the session ids her cookies name. The sessions themselves are hers and are in the file, without ids.
        Assert.DoesNotContain("tokenHash", text);
        foreach (var hash in await database.StringsAsync($"SELECT token_hash FROM hosts WHERE owner_id = '{alice.UserId}'"))
        {
            Assert.DoesNotContain(hash, text);
        }

        foreach (var session in await database.StringsAsync($"SELECT id FROM user_sessions WHERE user_id = '{alice.UserId}'"))
        {
            Assert.DoesNotContain(session, text);
        }

        Assert.All(Rows(tables, "user_sessions"), row => Assert.False(row.TryGetProperty("id", out _)));
    }

    /// <summary>
    /// One export an hour per account, refused with the API's own 429 and when to come back; the rest of the
    /// person's API is not held up by it, and another person's export is not counted against theirs.
    /// </summary>
    [Fact]
    public async Task A_second_export_within_an_hour_is_refused()
    {
        using var alice = await PanelClient.SignedInAsync(_gateway, Name("alice"));
        using var bob = await PanelClient.SignedInAsync(_gateway, Name("bob"));

        using (var first = await alice.SendAsync(HttpMethod.Get, "/api/export"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        using var second = await alice.SendAsync(HttpMethod.Get, "/api/export");
        Assert.Equal(HttpStatusCode.TooManyRequests, second.StatusCode);
        Assert.Equal("rate-limited", (await second.Content.ReadFromJsonAsync<ErrorView>(RemoteJson.Options))!.Code);

        var retryAfter = second.Headers.RetryAfter?.Delta;
        Assert.NotNull(retryAfter);
        Assert.InRange(retryAfter.Value, TimeSpan.FromMinutes(59), TimeSpan.FromHours(1));

        using var state = await alice.SendAsync(HttpMethod.Get, "/api/state", csrf: false);
        Assert.Equal(HttpStatusCode.OK, state.StatusCode);

        using var bobs = await bob.SendAsync(HttpMethod.Get, "/api/export");
        Assert.Equal(HttpStatusCode.OK, bobs.StatusCode);
    }

    /// <summary>A browser with no session is nobody whose data there is to give.</summary>
    [Fact]
    public async Task An_export_needs_a_session()
    {
        using var stranger = new PanelClient(_gateway);
        await stranger.SessionAsync();

        using var refused = await stranger.SendAsync(HttpMethod.Get, "/api/export", csrf: false);

        Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
    }

    /// <summary>
    /// Retry-After is the time LEFT of the hour since the last export, not a whole hour from now: a person who
    /// exported at 10:00 and asks again at 10:50 is told 11:00, when the next one works, and at 11:01 it does.
    /// </summary>
    [Fact]
    public async Task A_refused_export_says_how_long_is_left_of_the_hour()
    {
        var clock = new MovableClock(DateTimeOffset.UtcNow);
        await using var gateway = TestGateway.Create(database, configure: builder =>
            builder.ConfigureTestServices(services => services.AddSingleton(new ExportLimit(clock))));
        using var alice = await PanelClient.SignedInAsync(gateway, Name("alice"));

        using (var first = await alice.SendAsync(HttpMethod.Get, "/api/export"))
        {
            Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        }

        clock.Now += TimeSpan.FromMinutes(50);
        using (var early = await alice.SendAsync(HttpMethod.Get, "/api/export"))
        {
            Assert.Equal(HttpStatusCode.TooManyRequests, early.StatusCode);
            Assert.Equal("rate-limited", (await early.Content.ReadFromJsonAsync<ErrorView>(RemoteJson.Options))!.Code);
            Assert.Equal(TimeSpan.FromMinutes(10), early.Headers.RetryAfter?.Delta);
        }

        clock.Now += TimeSpan.FromMinutes(11);
        using var later = await alice.SendAsync(HttpMethod.Get, "/api/export");
        Assert.Equal(HttpStatusCode.OK, later.StatusCode);
    }

    /// <summary>
    /// A link on another site, or in a mail, is a top-level GET that carries the session cookie. It must not
    /// start a download of the person's data, nor spend their hour: without the antiforgery token, and without
    /// the browser saying the request came from this site (or from the person typing the address), it is refused
    /// before the hour is counted, and the panel's own export right after it goes through.
    /// </summary>
    [Fact]
    public async Task An_export_from_another_site_is_refused_and_uses_no_hour()
    {
        using var alice = await PanelClient.SignedInAsync(_gateway, Name("alice"));

        foreach (var site in new string?[] { null, "cross-site", "same-site" })
        {
            using var refused = await alice.SendAsync(HttpMethod.Get, "/api/export", csrf: false,
                configure: request =>
                {
                    if (site is not null)
                    {
                        request.Headers.Add("Sec-Fetch-Site", site);
                    }
                });

            Assert.True(refused.StatusCode == HttpStatusCode.Forbidden, $"{site ?? "no Sec-Fetch-Site"}: {refused.StatusCode}");
            Assert.Equal("cross-site", (await refused.Content.ReadFromJsonAsync<ErrorView>(RemoteJson.Options))!.Code);
            Assert.Null(refused.Content.Headers.ContentDisposition);
        }

        // A token that is not this session's is no better than none.
        using var forged = await alice.SendAsync(HttpMethod.Get, "/api/export", csrf: false,
            configure: request => request.Headers.Add("X-CSRF-TOKEN", "forged"));
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);

        // The browser saying the request is the page's own is enough, as is the token (the other tests).
        using var own = await alice.SendAsync(HttpMethod.Get, "/api/export", csrf: false,
            configure: request => request.Headers.Add("Sec-Fetch-Site", "same-origin"));
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);
    }

    // ── plumbing ────────────────────────────────────────────────────────────

    /// <summary>A clock a test moves by hand.</summary>
    private sealed class MovableClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    /// <summary>A table's rows as the file names it: the table's name in camelCase, as every key in it.</summary>
    private static List<JsonElement> Rows(JsonElement tables, string table)
    {
        var name = string.Concat(table.Split('_').Select((part, i) => i == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..]));
        Assert.True(tables.TryGetProperty(name, out var rows), $"The export has no '{name}' for the table {table}.");
        return rows.EnumerateArray().ToList();
    }

    /// <summary>Every table with an owner_id or a user_id column, and that column.</summary>
    private async Task<List<(string Table, string Column)>> OwnerColumnsAsync()
    {
        var columns = await database.StringsAsync(
            """
            SELECT CONCAT(TABLE_NAME, '.', COLUMN_NAME) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND COLUMN_NAME IN ('owner_id', 'user_id')
            ORDER BY TABLE_NAME
            """);

        // A schema read wrongly would make every assertion above pass on nothing.
        Assert.True(columns.Count >= 17, $"Only {columns.Count} owned tables were found.");
        return columns.Select(column => (column.Split('.')[0], column.Split('.')[1])).ToList();
    }

    private async Task<bool> HasColumnAsync(string table, string column)
        => await database.ScalarLongAsync(
            $"""
            SELECT COUNT(*) FROM information_schema.COLUMNS
            WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = '{table}' AND COLUMN_NAME = '{column}'
            """) > 0;

    /// <summary>
    /// A row of the person's in every table that has an owner, as in the deletion tests: what signing in made,
    /// a computer registered through the API under a label naming the person, and the rest written straight
    /// into the tables, with envelopes that name the person too, so a row of theirs in somebody else's file
    /// would show.
    /// </summary>
    private async Task SeedEverythingAsync(PanelClient person, string name)
    {
        var owner = person.UserId;
        await database.ExecuteAsync("INSERT INTO user_quotas (owner_id, revision, settings) VALUES (@owner, 1, JSON_OBJECT('HostsPerUser', 5))", ("@owner", owner));
        var host = (await person.PostAsync<RegisteredHost>("/api/hosts", new { name = $"PC of {name}" })).Id;
        var device = Ids.New();
        var invite = Ids.New();
        var task = Guid.NewGuid().ToString();
        var run = Ids.New();
        var now = DateTime.UtcNow;

        Assert.Equal(0, await AdminCommands.RunAsync(["approve", $"{DevelopmentSignIn.Provider}:{name}"], Db, TextWriter.Null));

        await database.ExecuteAsync(
            $"""
            INSERT INTO devices (id, owner_id, public_key, label, created_at)
            VALUES ('{device}', '{owner}', X'04', 'Laptop of {name}', @now);
            INSERT INTO host_workspaces (owner_id, host_id, workspace_id, sealed_name)
            VALUES ('{owner}', '{host}', 'workspace-1', 'sealed-name-{name}');
            INSERT INTO grants (owner_id, host_id, device_id, epoch, grant_json, created_at)
            VALUES ('{owner}', '{host}', '{device}', 1, @json, @now);
            INSERT INTO invites (id, owner_id, created_by_device, created_at, expires_at)
            VALUES ('{invite}', '{owner}', '{device}', @now, @now);
            INSERT INTO enrollments (invite_id, owner_id, device_id, mac, created_at)
            VALUES ('{invite}', '{owner}', '{device}', 'mac', @now);
            INSERT INTO tasks (owner_id, id, host_id, workspace_id, sealed, fingerprint, created_at)
            VALUES ('{owner}', '{task}', '{host}', 'workspace-1', 'sealed-task-{name}', REPEAT('0', 64), @now);
            INSERT INTO runs (id, owner_id, task_id, host_id, status, created_at, sealed_summary, summary_sequence)
            VALUES ('{run}', '{owner}', '{task}', '{host}', 'Completed', @now, 'sealed-summary-{name}', 2);
            INSERT INTO commands (owner_id, id, host_id, run_id, kind, payload, fingerprint, status, created_at, expires_at)
            VALUES ('{owner}', '{Ids.New()}', '{host}', '{run}', 'CancelRun', @json, REPEAT('0', 64), 'PendingDelivery', @now, @now);
            INSERT INTO approvals (owner_id, host_id, id, run_id, tool_call_id, action_hash, remote_decidable,
                                   sealed_action, status, created_at, expires_at)
            VALUES ('{owner}', '{host}', '{Ids.New()}', '{run}', 'call-1', REPEAT('0', 64), 1, 'sealed-action-{name}', 'Pending', @now, @now);
            INSERT INTO events (owner_id, host_id, id, run_id, sequence, kind, sealed_detail, at, ordinal)
            VALUES ('{owner}', '{host}', '{Ids.New()}', '{run}', 1, 'Running', 'sealed-event-{name}', @now, 1);
            INSERT INTO notices (id, owner_id, run_id, kind, sealed_detail, event_sequence, event_kind, at, ordinal)
            VALUES ('{Ids.New()}', '{owner}', '{run}', 'run-finished', 'sealed-notice-{name}', 2, 'Completed', @now, 1);
            """,
            ("@now", now), ("@json", "{}"));
    }

    /// <summary>What every refusal on the API answers with. Both fields, because the wire refuses unknown ones.</summary>
    private sealed record ErrorView(string Code, string Error);

    private sealed record RegisteredHost(string Id, string Name, string Token);
}
