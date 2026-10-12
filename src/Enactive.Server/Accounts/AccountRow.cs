namespace Enactive.Server.Accounts;

using System.Globalization;

/// <summary>What stops an account now, if anything: the edge of its card on the accounts page (Looks.Account).</summary>
internal enum AccountStanding { Active, Locked, Disabled }

/// <summary>
/// An account as the accounts page lists it: its name, its role, and what stops it - disabled, or locked after too many
/// wrong passwords and until when.
/// </summary>
/// <param name="State">"active", "disabled" or "locked until 14:05", in the service's own time zone.</param>
internal sealed record AccountRow(string Name, Role Role, AccountStanding Standing, string State)
{
    public static AccountRow Of(Account account, DateTimeOffset now)
    {
        // Disabled first: a disabled account that is also locked cannot sign in when the lock runs out either.
        if (account.Disabled)
            return new(account.Name, account.Role, AccountStanding.Disabled, "disabled");
        if (account.LockedUntil is { } until && until > now)
            // Invariant: the current culture's time separator made it "14.05" on one machine and "14:05" on another.
            return new(account.Name, account.Role, AccountStanding.Locked,
                "locked until " + until.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture));
        return new(account.Name, account.Role, AccountStanding.Active, "active");
    }

    /// <summary>The card's second line: the role, then what stops the account.</summary>
    public string Meta => $"{Role} · {State}";
}
