namespace Enactive.Server.Accounts;

using System.Security.Claims;

/// <summary>What a change to an account came to: whether it was made, and what to tell the person either way.</summary>
/// <param name="Done">Made - a page closes its form; refused, the form stays for another try.</param>
internal sealed record AccountChange(bool Done, string Said);

/// <summary>
/// The accounts, administered from the browser by an Admin (server plan phase 3): add one, give a new password, change
/// a role, disable or enable. The FIRST administrator is still made only on the server (AccountCommands) - a service
/// listening on the network must not let its first visitor make themselves its owner; after that, an Admin may.
///
/// <para>The same rules as the console's: at least 12 characters, the last administrator can be neither disabled nor
/// demoted, and every change audited with who made it. Checked here, at the action, not by the page.</para>
/// </summary>
internal sealed class AccountAdmin(UserStore users)
{
    public static class Act
    {
        public const string Add = "add";
        public const string Password = "password";
        public const string Role = "role";
        public const string Disable = "disable";
        public const string Enable = "enable";
    }

    /// <summary>
    /// Why this person may not administer accounts now, or null for an Admin whose session still speaks for them. Asked
    /// at every act as well: a page shows its lists only to an Admin, but a role lowered while the page is open must not
    /// keep acting from it.
    /// </summary>
    public string? Refusal(ClaimsPrincipal user)
        => Admin(user) is not null ? null
            : ServerAuth.StillValid(users, user) is null ? "Your session has ended. Sign in again."
            : "Your role does not include administering accounts.";

    /// <summary>The accounts, for an Admin; nothing for anybody else.</summary>
    public IReadOnlyList<Account> List(ClaimsPrincipal user) => Admin(user) is null ? [] : users.All();

    /// <summary>The audit trail, newest first, for an Admin.</summary>
    public IReadOnlyList<(DateTimeOffset At, string Account, string Action, string? Detail, string? Remote)> Trail(ClaimsPrincipal user, int newest = 200)
        => Admin(user) is null ? [] : users.AuditTrail(newest);

    /// <summary>Does one change, as this person: made, or refused and why.</summary>
    public AccountChange Do(ClaimsPrincipal user, string act, string name, string? role, string? password, string? again)
    {
        if (Admin(user) is not { } admin)
            return new(false, Refusal(user) ?? "Your session has ended. Sign in again.");
        // Trimmed: a name pasted with a space around it was refused as a name with whitespace.
        name = name.Trim();

        string? problem;
        string done;
        switch (act)
        {
            case Act.Add:
                if (!TryRole(role, out var newRole))
                    return new(false, "A role is Admin, Operator or Viewer.");
                if (password != again)
                    return new(false, "The two passwords did not match; nothing was changed.");
                problem = users.Add(name, password ?? "", newRole);
                done = $"Added {name} ({newRole}).";
                break;
            case Act.Password:
                if (password != again)
                    return new(false, "The two passwords did not match; nothing was changed.");
                problem = users.SetPassword(name, password ?? "");
                done = $"New password set for {name}; its sessions are ended.";
                break;
            case Act.Role:
                if (!TryRole(role, out var changed))
                    return new(false, "A role is Admin, Operator or Viewer.");
                problem = users.SetRole(name, changed);
                done = $"{name} is now {changed}; its sessions are ended.";
                break;
            case Act.Disable:
                problem = users.SetDisabled(name, true);
                done = $"{name} is disabled; its sessions are ended.";
                break;
            case Act.Enable:
                problem = users.SetDisabled(name, false);
                done = $"{name} is enabled.";
                break;
            default:
                return new(false, "That is not something done to an account.");
        }

        if (problem is not null)
            return new(false, problem);
        users.Audit(admin.Name, "account " + act, name + (act == Act.Role || act == Act.Add ? $" ({role})" : ""));
        return new(true, done);
    }

    private Account? Admin(ClaimsPrincipal user)
        => ServerAuth.StillValid(users, user) is { Role: Accounts.Role.Admin } account ? account : null;

    private static bool TryRole(string? text, out Role role)
        => Enum.TryParse(text, ignoreCase: true, out role) && Enum.IsDefined(role);
}
