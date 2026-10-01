namespace Enactive.Remote.Gateway.Tests;

using Enactive.Remote.Contracts;
using Enactive.Remote.Gateway.Services;
using Enactive.Remote.Host;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

/// <summary>
/// "Test connection", against the real gateway.
///
/// <para>It is tested here rather than beside the Host's other tests because the only claim worth
/// making about it is that it agrees with the gateway: a check that answered from a fake would be
/// certain about a server that does not exist, which is the exact failure this button was added to
/// end. The button exists because the first person to set remote access up did everything right,
/// the panel said Offline, and nothing anywhere would say why.</para>
/// </summary>
public sealed class GatewayProbeTests(TestDatabase database) : IClassFixture<TestDatabase>, IAsyncLifetime
{
    private WebApplicationFactory<Program> _gateway = null!;
    private PanelClient _owner = null!;

    public async Task InitializeAsync()
    {
        _gateway = TestGateway.Create(database);
        _owner = await PanelClient.SignedInAsync(_gateway, "owner-" + Guid.NewGuid().ToString("N")[..8]);
    }

    public Task DisposeAsync()
    {
        _owner.Dispose();
        return _gateway.DisposeAsync().AsTask();
    }

    // ── the check ───────────────────────────────────────────────────────────

    /// <summary>
    /// The decisive one, and the reason the check SYNCS rather than merely connecting.
    ///
    /// <para>A computer is online in the panel by its last sync, not by having a socket open. A
    /// check that stopped at the connection would have reported success while the panel went on
    /// saying Offline - reporting exactly the contradiction it was written to explain.</para>
    /// </summary>
    [Fact]
    public async Task A_check_that_succeeds_makes_the_computer_online()
    {
        var device = await _owner.PostAsync<DeviceView>("/api/hosts", new { name = "Studio PC" });
        var browser = new TestBrowser(device.Id);
        var name = browser.Computer.Sealer().WorkspaceName("workspace-1", "Enactive");

        var check = await Probe(device.Token, [new WorkspaceRef("workspace-1", name)]);

        Assert.True(check.Reached, check.Detail);

        var host = Assert.Single((await _owner.GetAsync<GatewaySnapshot>("/api/state")).Hosts, h => h.Id == device.Id);

        Assert.True(host.Online);
        Assert.Equal("Enactive", browser.OpenWorkspace(Assert.Single(host.Workspaces)));
    }

    /// <summary>
    /// A token the gateway does not know. The whole point of the button: this is the answer the
    /// person needs and the one the panel cannot give, because to the panel a computer that never
    /// connected and a computer connecting with a bad token look identical.
    /// </summary>
    [Fact]
    public async Task A_token_the_gateway_does_not_know_is_reported_as_a_failure()
    {
        var check = await Probe("not-a-token-this-gateway-issued", []);

        Assert.False(check.Reached);
        Assert.Contains("revoked", check.Detail, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A revoked computer. Same shape as an unknown token deliberately: the gateway must not say
    /// which of the two it was, or the button becomes a way to find out whether a guessed token
    /// used to be real.
    /// </summary>
    [Fact]
    public async Task A_revoked_computer_can_no_longer_check_in()
    {
        var device = await _owner.PostAsync<DeviceView>("/api/hosts", new { name = "Old laptop" });

        Assert.True((await Probe(device.Token, [])).Reached);

        await _owner.PostAsync($"/api/hosts/{device.Id}/revoke", new { });

        Assert.False((await Probe(device.Token, [])).Reached);
    }

    // The cases that must never reach a gateway - a malformed address, a missing token - are in
    // Enactive.Engine.Tests.GatewayProbeTests, where they run without a database. Proving that an
    // empty address is refused should not require a MySQL container.

    // ── plumbing ────────────────────────────────────────────────────────────

    private Task<GatewayCheck> Probe(string token, IReadOnlyList<WorkspaceRef> workspaces)
        => GatewayProbe.CheckAsync(
            _gateway.Server.BaseAddress.ToString(), token, workspaces, default,
            options =>
            {
                options.HttpMessageHandlerFactory = _ => _gateway.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
            });

    private sealed record DeviceView(string Id, string Name, string Token);
}
