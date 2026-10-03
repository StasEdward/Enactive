namespace Enactive.Remote.Gateway.Accounts;

using System.Globalization;
using System.Text;
using Enactive.Remote.Gateway.Storage;
using Enactive.Remote.Gateway.Administration;

/// <summary>
/// The operator's command line, run on the gateway's machine with only its database configuration.
/// Parsing and presentation live here; AdministrationService owns the transactions shared with future
/// host adapters. Exit codes: 0 done, 1 no such account, 2 the command or identity was not understood.
/// </summary>
public static class AdminCommands
{
    private const string Usage =
        """
        Usage: admin <command>
          admissions                        list the identities waiting for approval
          approve <provider>:<subject>      let this identity create an account (e.g. github:12345)
          refuse <provider>:<subject>       turn this identity away
          disable <userId>                  stop an account: sign-ins, sessions and queued commands
          enable <userId>                   let a disabled account sign in again
          sessions revoke <userId>          sign the account out everywhere
          administrators grant <issuer> <subject>   grant or recover administrative access
          administrators revoke <issuer> <subject>  revoke administrative access and sessions
        """;

    public static async Task<int> RunAsync(
        string[] args, Database db, TextWriter output, TimeProvider? clock = null, CancellationToken ct = default)
    {
        var administration = new AdministrationService(db, clock ?? TimeProvider.System);
        switch (args)
        {
            case ["administrators", "grant", var issuer, var subject]
                when AdminIdentity.TryCreate(issuer, subject, out var identity):
                var id = await new AdminStore(db, clock ?? TimeProvider.System).GrantAsync(identity!, ct);
                await output.WriteLineAsync($"Administrator {id} granted. Earlier sessions stay ended.");
                return 0;

            case ["administrators", "revoke", var issuer, var subject]
                when AdminIdentity.TryCreate(issuer, subject, out var identity):
                var found = await new AdminStore(db, clock ?? TimeProvider.System).RevokeAsync(identity!, ct);
                await output.WriteLineAsync(found ? "Administrative access and sessions revoked." : "No such administrator.");
                return found ? 0 : 1;

            case ["admissions"]:
                return await ListWaitingAsync(administration, output, ct);

            case ["approve", var identity] when AdmissionIdentity.TryParse(identity, out var who):
                return await DecideAsync(administration, output, who, AdmissionState.Approved, ct);

            case ["refuse", var identity] when AdmissionIdentity.TryParse(identity, out var who):
                return await DecideAsync(administration, output, who, AdmissionState.Refused, ct);

            case ["disable", var userId]:
                if (await administration.DisableAccountAsync(userId, ct) is not { } disabled)
                    return await NoSuchAccountAsync(output, userId);
                await output.WriteLineAsync(
                    $"Disabled {userId}: sessions ended, {disabled.WithdrawnCommands} undelivered command(s) withdrawn.");
                return 0;

            case ["enable", var userId]:
                if (!await administration.EnableAccountAsync(userId, ct))
                    return await NoSuchAccountAsync(output, userId);
                await output.WriteLineAsync($"Enabled {userId}. Earlier sessions stay ended; the person signs in again.");
                return 0;

            case ["sessions", "revoke", var userId]:
                if (!await administration.RevokeSessionsAsync(userId, ct))
                    return await NoSuchAccountAsync(output, userId);
                await output.WriteLineAsync($"Every session of {userId} has ended.");
                return 0;

            default:
                await output.WriteLineAsync(Usage);
                return 2;
        }
    }

    private static async Task<int> ListWaitingAsync(
        AdministrationService administration, TextWriter output, CancellationToken ct)
    {
        var waiting = await administration.ListWaitingAsync(ct);
        if (waiting.Count == 0)
        {
            await output.WriteLineAsync("No identities are waiting.");
            return 0;
        }

        foreach (var request in waiting)
            await output.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                $"{request.Identity}  {Printable(request.Display)}  asked {request.RequestedAt:yyyy-MM-dd HH:mm:ss}Z"));
        return 0;
    }

    private static async Task<int> DecideAsync(
        AdministrationService administration, TextWriter output, AdmissionIdentity who, AdmissionState state,
        CancellationToken ct)
    {
        var result = await administration.DecideAdmissionAsync(who, state, ct);
        await output.WriteLineAsync($"{who} is now {state.ToString().ToLowerInvariant()}.");

        // Admission controls account creation. A refusal must not look like disabling an account
        // that already exists; the shared service reports the distinction to every adapter.
        if (result.HasAccount && state == AdmissionState.Refused)
            await output.WriteLineAsync(
                "Note: this identity already has an account, and refusing does not stop it signing in. "
                + "Use disable <userId> for that.");
        return 0;
    }

    private static async Task<int> NoSuchAccountAsync(TextWriter output, string userId)
    {
        await output.WriteLineAsync($"There is no account with id {Printable(userId)}.");
        return 1;
    }

    /// <summary>
    /// Display names are untrusted text. Control characters could clear the operator's terminal or
    /// forge another row. Sanitize at this adapter, leaving structured data intact for other renderers.
    /// </summary>
    private static string Printable(string text)
    {
        var shown = new StringBuilder(text.Length);
        foreach (var character in text)
            shown.Append(char.IsControl(character) ? '?' : character);
        return shown.ToString();
    }
}
