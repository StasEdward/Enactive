namespace Enactive.Server.Accounts;

/// <summary>
/// The accounts, managed on the server itself (ADR 0002 §4): the first administrator is made here, never from the
/// web - a fresh service listening on the network must not let its first visitor make themselves its owner.
///
/// <code>
///   Enactive.Server --add-user &lt;name&gt; [--role Admin|Operator|Viewer]
///   Enactive.Server --reset-password &lt;name&gt;
///   Enactive.Server --set-role &lt;name&gt; &lt;Admin|Operator|Viewer&gt;
///   Enactive.Server --disable-user &lt;name&gt;  |  --enable-user &lt;name&gt;
///   Enactive.Server --list-users
/// </code>
///
/// Run as the account the service runs as (its data folder is that account's), or with <c>--data</c>. A password is
/// read from the keyboard, twice, and never taken as an argument: an argument stays in the shell's history and the
/// process list.
/// </summary>
internal static class AccountCommands
{
    public static readonly string[] Verbs =
        ["--add-user", "--reset-password", "--set-role", "--disable-user", "--enable-user", "--list-users"];

    /// <param name="readPassword">Reads a password without showing it; a test supplies its own.</param>
    /// <returns>The exit code: 0 done, 64 a usage error, 65 refused.</returns>
    public static int Run(string[] args, UserStore users, Func<string, string> readPassword, TextWriter output, TextWriter errors)
    {
        string? After(string verb)
        {
            var at = Array.FindIndex(args, a => string.Equals(a, verb, StringComparison.OrdinalIgnoreCase));
            return at >= 0 && at + 1 < args.Length && !args[at + 1].StartsWith("--", StringComparison.Ordinal) ? args[at + 1] : null;
        }
        bool Has(string verb) => args.Contains(verb, StringComparer.OrdinalIgnoreCase);

        int Done(string? problem, string said, string name, string action)
        {
            if (problem is not null)
            {
                errors.WriteLine(problem);
                return 65;
            }
            users.Audit("(console)", action, name);
            output.WriteLine(said);
            return 0;
        }

        int Usage(string verb)
        {
            errors.WriteLine($"{verb} needs an account name.");
            return 64;
        }

        if (Has("--list-users"))
        {
            foreach (var account in users.All())
                output.WriteLine($"{account.Name,-24} {account.Role,-9}"
                                 + (account.Disabled ? " disabled" : "")
                                 + (account.LockedUntil is { } until && until > DateTimeOffset.UtcNow ? $" locked until {until.ToLocalTime():HH:mm}" : ""));
            return 0;
        }

        if (Has("--add-user"))
        {
            if (After("--add-user") is not { } name) return Usage("--add-user");
            var roleText = After("--role") ?? (users.All().Count == 0 ? nameof(Role.Admin) : nameof(Role.Operator));
            if (!Enum.TryParse<Role>(roleText, ignoreCase: true, out var role) || !Enum.IsDefined(role))
            {
                errors.WriteLine($"--role is Admin, Operator or Viewer, not '{roleText}'.");
                return 64;
            }
            // The first account is the one that can make the others; a service whose only account cannot is locked
            // out of its own administration until somebody edits the database.
            if (users.All().Count == 0 && role != Role.Admin)
            {
                errors.WriteLine("The first account is the administrator: add it with --role Admin.");
                return 65;
            }
            if (NewPassword(readPassword, errors) is not { } password) return 65;
            return Done(users.Add(name, password, role), $"Added {name} ({role}).", name, $"account added as {role}");
        }

        if (Has("--reset-password"))
        {
            if (After("--reset-password") is not { } name) return Usage("--reset-password");
            if (users.Find(name) is null) return Done($"There is no account '{name}'.", "", name, "");
            if (NewPassword(readPassword, errors) is not { } password) return 65;
            return Done(users.SetPassword(name, password), $"New password set for {name}; its sessions are ended.", name, "password reset");
        }

        if (Has("--set-role"))
        {
            var at = Array.FindIndex(args, a => string.Equals(a, "--set-role", StringComparison.OrdinalIgnoreCase));
            if (at + 2 >= args.Length) return Usage("--set-role");
            if (!Enum.TryParse<Role>(args[at + 2], ignoreCase: true, out var role) || !Enum.IsDefined(role))
            {
                errors.WriteLine($"A role is Admin, Operator or Viewer, not '{args[at + 2]}'.");
                return 64;
            }
            var name = args[at + 1];
            if (role != Role.Admin && IsLastAdmin(users, name))
                return Done("That is the last administrator; make another one first.", "", name, "");
            return Done(users.SetRole(name, role), $"{name} is now {role}; its sessions are ended.", name, $"role set to {role}");
        }

        if (Has("--disable-user"))
        {
            if (After("--disable-user") is not { } name) return Usage("--disable-user");
            if (IsLastAdmin(users, name))
                return Done("That is the last administrator; make another one first.", "", name, "");
            return Done(users.SetDisabled(name, true), $"{name} is disabled; its sessions are ended.", name, "account disabled");
        }

        if (Has("--enable-user"))
        {
            if (After("--enable-user") is not { } name) return Usage("--enable-user");
            return Done(users.SetDisabled(name, false), $"{name} is enabled.", name, "account enabled");
        }

        errors.WriteLine("No account command given.");
        return 64;
    }

    private static bool IsLastAdmin(UserStore users, string name)
        => users.All().Where(a => a is { Role: Role.Admin, Disabled: false }).Select(a => a.Name).ToList() is [var only]
           && string.Equals(only, name, StringComparison.OrdinalIgnoreCase);

    private static string? NewPassword(Func<string, string> readPassword, TextWriter errors)
    {
        var password = readPassword("New password (not shown): ");
        if (UserStore.PasswordProblem(password) is { } problem)
        {
            errors.WriteLine(problem);
            return null;
        }
        if (readPassword("Again: ") != password)
        {
            errors.WriteLine("The two did not match; nothing was changed.");
            return null;
        }
        return password;
    }

    /// <summary>Reads a line from the keyboard without echoing it.</summary>
    public static string ReadHidden(string prompt)
    {
        Console.Error.Write(prompt);
        // Piped in (a script that makes the account): there is no keyboard to read keys from, and ReadKey throws.
        if (Console.IsInputRedirected)
        {
            var line = Console.ReadLine() ?? "";
            Console.Error.WriteLine();
            return line;
        }
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter) break;
            if (key.Key == ConsoleKey.Backspace) { if (text.Length > 0) text.Length--; continue; }
            text.Append(key.KeyChar);
        }
        Console.Error.WriteLine();
        return text.ToString();
    }
}
