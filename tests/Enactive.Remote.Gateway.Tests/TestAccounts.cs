namespace Enactive.Remote.Gateway.Tests;

using Enactive.Remote.Gateway.Accounts;
using Enactive.Remote.Gateway.Storage;

/// <summary>
/// People for tests: a real account through the real provisioning path, with a session row, so a
/// test that needs "Alice" gets everything Alice would have after signing in and not a stub.
/// </summary>
internal static class TestAccounts
{
    public const string Provider = "test";

    /// <summary>The account for <paramref name="name"/>, made if it does not exist yet, with a fresh session.</summary>
    public static async Task<UserAccess> CreateAsync(TestDatabase database, string name)
    {
        var db = new Database(database.ConnectionString);
        var userId = await new AccountService(db, TimeProvider.System)
            .ProvisionAsync(Provider, name, name, CancellationToken.None);

        var sessionId = Ids.New();
        await database.ExecuteAsync(
            """
            INSERT INTO user_sessions (id, user_id, security_version, created_at, expires_at)
            VALUES (@id, @user, 1, UTC_TIMESTAMP(3), UTC_TIMESTAMP(3) + INTERVAL 8 HOUR)
            """,
            ("@id", sessionId), ("@user", userId));

        return new UserAccess(userId, sessionId);
    }
}
