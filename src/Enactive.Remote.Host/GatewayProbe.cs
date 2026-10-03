namespace Enactive.Remote.Host;

using Enactive.Remote.Contracts;
using Microsoft.AspNetCore.Http.Connections.Client;

/// <summary>What a check of the settings found. <paramref name="Detail"/> is for a person to read.</summary>
public sealed record GatewayCheck(bool Reached, string Detail);

/// <summary>
/// Answers "do these settings actually work", once, on demand.
///
/// <para>It exists because the alternative was what happened the first time somebody set this up:
/// the settings were right, the panel said Offline, and nothing anywhere said why. The connection
/// this service holds open reports through a status line that is only read if you go looking for
/// it, and only after a restart - so the one moment a person most needs an answer is the one moment
/// they could not get one.</para>
/// </summary>
public static class GatewayProbe
{
    /// <summary>
    /// Connects with these settings, says hello and syncs once.
    ///
    /// <para>It syncs rather than merely connecting because connecting proves less than it looks.
    /// The gateway marks a computer online from its last SYNC, so a check that stopped at the
    /// socket would report success while the panel went on saying Offline - which is precisely the
    /// confusion this is here to end. Sync also publishes the workspaces, which is the same call
    /// the running connection makes every fifteen seconds with the same list: repeating it early
    /// changes nothing about what the gateway holds.</para>
    /// </summary>
    /// <param name="workspaces">
    /// What this computer would publish. Passed in rather than left empty on purpose: Sync REPLACES
    /// the published set, so a check that sent nothing would unpublish every workspace and leave
    /// the panel unable to name one until the next sync repaired it.
    /// </param>
    public static async Task<GatewayCheck> CheckAsync(
        string? gatewayUrl,
        string? token,
        IReadOnlyList<WorkspaceRef> workspaces,
        CancellationToken ct = default,
        Action<HttpConnectionOptions>? configure = null)
    {
        Uri hub;

        try
        {
            hub = GatewayAddress.Hub(gatewayUrl);
        }
        catch (ArgumentException bad)
        {
            return new GatewayCheck(false, bad.Message);
        }

        if (string.IsNullOrEmpty(token))
        {
            return new GatewayCheck(false, "There is no device token to connect with.");
        }

        await using var connection = new SignalRGatewayConnection(hub, token, configure);

        try
        {
            await connection.StartAsync(ct);
        }
        catch (GatewayCredentialRefusedException refused)
        {
            // The one start failure that can be named for certain: the gateway answered, and said no.
            return new GatewayCheck(false, refused.Message);
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            // Anything else is an address that did not answer as a gateway - a typo, a server that is
            // down, a proxy in the way. Saying which would mean guessing from a message.
            return new GatewayCheck(false,
                $"Could not connect to {hub}. Check that the gateway is up and reachable from this "
                + $"computer. The connection said: {failure.Message}");
        }

        return await CheckAsync(connection, workspaces, ct);
    }

    /// <summary>
    /// The check on a connection that is open: hello, then one sync.
    ///
    /// <para>Hello first, because a gateway of another protocol would refuse the sync too, for a reason
    /// that names neither protocol - and would be handed a workspace list sealed in a format its
    /// browsers may not open.</para>
    /// </summary>
    public static async Task<GatewayCheck> CheckAsync(
        IGatewayConnection connection, IReadOnlyList<WorkspaceRef> workspaces, CancellationToken ct)
    {
        try
        {
            await connection.HelloAsync(RemoteProtocol.Version, ct);
            await connection.SyncAsync(workspaces, ct);
        }
        catch (GatewayRefusedException refused)
        {
            return new GatewayCheck(false,
                $"Connected, but the gateway refused this computer: {refused.Message} ({refused.Code})");
        }
        catch (Exception failure) when (failure is not OperationCanceledException)
        {
            return new GatewayCheck(false, $"Connected, but the first call failed: {failure.Message}");
        }

        return new GatewayCheck(true,
            workspaces.Count == 0
                ? "Connected. This computer is online, and has no workspaces to offer yet - add one "
                + "on the main window and it will appear on the phone within fifteen seconds."
                : $"Connected. This computer is online and offering {workspaces.Count} workspace(s).");
    }
}
