namespace Enactive.Remote.Gateway.Tests;

using Enactive.Remote.Gateway.Accounts;
using Enactive.Remote.Gateway.Storage;

/// <summary>
/// People for tests: a real account through the real provisioning path, with a session opened the way
/// signing in opens one, so a test that needs "Alice" gets everything Alice would have after signing in
/// and not a stub.
/// </summary>
internal static class TestAccounts
{
    public const string Provider = "test";

    /// <summary>The account for <paramref name="name"/>, made if it does not exist yet, with a fresh session.</summary>
    public static async Task<UserAccess> CreateAsync(TestDatabase database, string name)
    {
        var db = new Database(database.ConnectionString);
        var userId = await new AccountService(db, TimeProvider.System)
            .ProvisionWithoutAdmissionAsync(Provider, name, name, CancellationToken.None);

        return await new SessionStore(db, TimeProvider.System).OpenAsync(userId, Provider, CancellationToken.None);
    }
}
