namespace Enactive.Remote.Gateway.Tests;

using Enactive.Remote.Gateway;
using Enactive.Remote.Gateway.Administration;
using Enactive.Remote.Gateway.Storage;

public sealed class AdminDirectoryTests(TestDatabase database) : IClassFixture<TestDatabase>
{
    private AdminDirectory Directory => new(new Database(database.ConnectionString));

    [Fact]
    public async Task User_pages_search_literal_text_and_survive_deletion_before_the_cursor()
    {
        var name = Guid.NewGuid().ToString("N") + "%_";
        var ids = new[] { Ids.New(), Ids.New(), Ids.New() }.Order(StringComparer.Ordinal).ToArray();
        foreach (var id in ids)
            await database.ExecuteAsync($"INSERT INTO users (id, display_name, status, created_at) VALUES ('{id}', '{name}', 'Disabled', UTC_TIMESTAMP(3))");
        var first = await Directory.UsersAsync(name, "Disabled", null, 1, default);
        Assert.Equal(ids[0], Assert.Single(first.Items).Id);
        Assert.Equal(ids[0], first.Next);
        await database.ExecuteAsync($"DELETE FROM users WHERE id = '{ids[0]}'");
        var second = await Directory.UsersAsync(name, "Disabled", first.Next, 2, default);
        Assert.Equal(ids.Skip(1), second.Items.Select(x => x.Id));
        Assert.Null(second.Next);
        Assert.Empty((await Directory.UsersAsync(name, "Active", null, 25, default)).Items);
        Assert.Empty((await Directory.UsersAsync("' OR 1=1 --", null, null, 25, default)).Items);
        Assert.Equal(ids[1], Assert.Single((await Directory.UsersAsync(ids[1], null, null, 25, default)).Items).Id);
    }

    [Fact]
    public async Task Registration_pages_use_the_entire_case_sensitive_identity_and_filter_state()
    {
        var name = Ids.New();
        var identities = new[] { ("github", name + "A:1"), ("github", name + "a:1"), ("google", name) };
        foreach (var (provider, subject) in identities)
            await database.ExecuteAsync($"INSERT INTO admissions (provider, subject, display, state, requested_at) VALUES ('{provider}', '{subject}', '{name}', 'Waiting', UTC_TIMESTAMP(3))");
        string? cursor = null;
        foreach (var (provider, subject) in identities)
        {
            var page = await Directory.RegistrationsAsync(name, "Waiting", cursor, 1, default);
            var item = Assert.Single(page.Items);
            Assert.Equal(provider, item.Provider); Assert.Equal(subject, item.Subject);
            Assert.Null(item.DecidedAt);
            cursor = page.Next;
        }
        Assert.Null(cursor);
        Assert.Empty((await Directory.RegistrationsAsync(name, "Refused", null, 25, default)).Items);
    }

    [Fact]
    public async Task Overview_and_details_report_existing_metadata_without_encrypted_payloads()
    {
        var before = await Directory.OverviewAsync(default);
        var id = Ids.New();
        await database.ExecuteAsync($"""
            INSERT INTO users (id, display_name, status, sealed_bytes, created_at)
            VALUES ('{id}', '<script>name</script>', 'Disabled', 12345, UTC_TIMESTAMP(3));
            INSERT INTO hosts (id, owner_id, label, token_hash, created_at, last_seen_at)
            VALUES ('{Ids.New()}', '{id}', 'Private computer', REPEAT('f', 64), UTC_TIMESTAMP(3), '2026-10-04 01:00:00');
            """);
        var after = await Directory.OverviewAsync(default);
        Assert.Equal(before.Users + 1, after.Users);
        Assert.Equal(before.DisabledUsers + 1, after.DisabledUsers);
        var detail = await Directory.UserAsync(id, default);
        Assert.Equal(12345, detail.User.SealedBytes);
        Assert.Equal("<script>name</script>", detail.User.DisplayName);
        Assert.Equal(1, detail.Hosts); Assert.Equal(0, detail.Devices);
        Assert.Equal(0, detail.Tasks); Assert.Equal(0, detail.Runs);
        Assert.Equal(new DateTimeOffset(2026, 10, 4, 1, 0, 0, TimeSpan.Zero), detail.LastHostSeenAt);
        await Assert.ThrowsAsync<GatewayFault>(() => Directory.UserAsync(Ids.New(), default));
    }

    [Theory]
    [InlineData(0, null, null)]
    [InlineData(101, null, null)]
    [InlineData(25, "Unknown", null)]
    [InlineData(25, null, "invalid-cursor")]
    public async Task Invalid_filters_and_cursors_are_refused(int size, string? state, string? cursor)
    {
        await Assert.ThrowsAsync<GatewayFault>(() => Directory.UsersAsync(null, state, cursor, size, default));
        await Assert.ThrowsAsync<GatewayFault>(() => Directory.RegistrationsAsync(null, state, cursor, size, default));
    }

    [Fact]
    public async Task Unbounded_search_is_refused()
    {
        await Assert.ThrowsAsync<GatewayFault>(() => Directory.UsersAsync(new string('x', 101), null, null, 25, default));
        await Assert.ThrowsAsync<GatewayFault>(() => Directory.RegistrationsAsync(new string('x', 101), null, null, 25, default));
    }
}
