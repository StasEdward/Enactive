namespace Enactive.Server.Accounts;

using Microsoft.AspNetCore.Identity;
using Microsoft.Data.Sqlite;

/// <summary>What a person may do in the interface. Each includes the one before it (ADR 0002 §4).</summary>
internal enum Role { Viewer = 1, Operator = 2, Admin = 3 }

/// <summary>An account, as the service keeps it. No password: only its hash ever leaves the store, and only to be checked.</summary>
/// <param name="Stamp">Changes whenever what the account may do changes - password, role, disabled - so a session
/// signed in before the change is recognised as stale and ended (revalidation).</param>
internal sealed record Account(string Name, Role Role, bool Disabled, DateTimeOffset? LockedUntil, string Stamp);

/// <summary>How a sign-in went. The page says only "not signed in" for every failure but a lockout - which names no account.</summary>
internal enum SignInVerdict { Ok, Wrong, Locked, Disabled }

/// <summary>
/// The service's accounts and its audit log, in <c>server.db</c> in its data folder (ADR 0002 §4: accounts kept by
/// Enactive, because the server is outside the domain and cannot check a Kerberos ticket).
///
/// <para><b>Passwords</b> are hashed by ASP.NET Core's <see cref="PasswordHasher{TUser}"/> (PBKDF2, salted, with its
/// own format version so a stronger default rehashes on the next sign-in). <b>Lockout</b>: after
/// <see cref="FailuresBeforeLockout"/> wrong passwords in a row the account is refused for
/// <see cref="LockoutFor"/>, whatever is typed - a person can wait a quarter of an hour, a guesser cannot afford to.</para>
///
/// <para>The file is the service account's; the installer leaves nobody else a way in. Nothing here is reachable from
/// the web before sign-in except <see cref="SignIn"/>.</para>
/// </summary>
internal sealed class UserStore
{
    public const int FailuresBeforeLockout = 5;
    public static readonly TimeSpan LockoutFor = TimeSpan.FromMinutes(15);

    /// <summary>Long enough that a guesser who gets past the subnet and the lockout still has years of work.</summary>
    public const int MinimumPasswordLength = 12;

    private static readonly PasswordHasher<string> Hasher = new();

    /// <summary>Hashed for a name that does not exist, so "no such account" takes as long to say as "wrong password".</summary>
    private static readonly string DecoyHash = Hasher.HashPassword("", Guid.NewGuid().ToString());

    private readonly string _connection;
    private readonly TimeProvider _clock;

    public UserStore(string dataFolder, TimeProvider? clock = null)
    {
        Directory.CreateDirectory(dataFolder);
        _connection = new SqliteConnectionStringBuilder { DataSource = Path.Combine(dataFolder, "server.db") }.ToString();
        _clock = clock ?? TimeProvider.System;
        using var db = Open();
        Execute(db, """
            CREATE TABLE IF NOT EXISTS users (
                name         TEXT PRIMARY KEY COLLATE NOCASE,
                hash         TEXT NOT NULL,
                role         INTEGER NOT NULL,
                disabled     INTEGER NOT NULL DEFAULT 0,
                failures     INTEGER NOT NULL DEFAULT 0,
                locked_until TEXT,
                stamp        TEXT NOT NULL,
                created      TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS audit (
                id      INTEGER PRIMARY KEY AUTOINCREMENT,
                at      TEXT NOT NULL,
                account TEXT NOT NULL,
                action  TEXT NOT NULL,
                detail  TEXT,
                remote  TEXT);
            """);
    }

    private SqliteConnection Open()
    {
        var db = new SqliteConnection(_connection);
        db.Open();
        return db;
    }

    private static void Execute(SqliteConnection db, string sql, params (string Name, object? Value)[] values)
    {
        using var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in values)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static string NewStamp() => Guid.NewGuid().ToString("N");

    /// <summary>Why this password cannot be one, or null.</summary>
    public static string? PasswordProblem(string password)
        => password.Length < MinimumPasswordLength
            ? $"A password is at least {MinimumPasswordLength} characters."
            : null;

    /// <summary>Why this name cannot be an account's, or null.</summary>
    public static string? NameProblem(string name)
        => string.IsNullOrWhiteSpace(name) || name.Length > 64 || name.Any(c => char.IsWhiteSpace(c) || char.IsControl(c))
            ? "A name is 1 to 64 characters, without spaces."
            : null;

    /// <summary>Adds an account; why not, or null.</summary>
    public string? Add(string name, string password, Role role)
    {
        if ((NameProblem(name) ?? PasswordProblem(password)) is { } problem)
            return problem;
        if (Find(name) is not null)
            return $"There is already an account '{name}'.";

        using var db = Open();
        Execute(db, "INSERT INTO users (name, hash, role, stamp, created) VALUES ($name, $hash, $role, $stamp, $created)",
            ("$name", name), ("$hash", Hasher.HashPassword(name, password)), ("$role", (int)role),
            ("$stamp", NewStamp()), ("$created", _clock.GetUtcNow().ToString("O")));
        return null;
    }

    public Account? Find(string name)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT name, role, disabled, locked_until, stamp FROM users WHERE name = $name";
        command.Parameters.AddWithValue("$name", name);
        using var row = command.ExecuteReader();
        return row.Read() ? Read(row) : null;
    }

    public IReadOnlyList<Account> All()
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT name, role, disabled, locked_until, stamp FROM users ORDER BY name";
        using var row = command.ExecuteReader();
        var all = new List<Account>();
        while (row.Read())
            all.Add(Read(row));
        return all;
    }

    private static Account Read(SqliteDataReader row)
        => new(row.GetString(0), (Role)row.GetInt32(1), row.GetInt32(2) != 0,
            row.IsDBNull(3) ? null : DateTimeOffset.Parse(row.GetString(3)), row.GetString(4));

    /// <summary>Checks a password. Counts a wrong one toward the lockout; a right one clears the count.</summary>
    public (SignInVerdict Verdict, Account? Account) SignIn(string name, string password)
    {
        using var db = Open();
        using var transaction = db.BeginTransaction();
        string? hash = null;
        int failures = 0;
        Account? account = null;
        using (var command = db.CreateCommand())
        {
            command.CommandText = "SELECT name, role, disabled, locked_until, stamp, hash, failures FROM users WHERE name = $name";
            command.Parameters.AddWithValue("$name", name);
            using var row = command.ExecuteReader();
            if (row.Read())
            {
                account = Read(row);
                hash = row.GetString(5);
                failures = row.GetInt32(6);
            }
        }

        var now = _clock.GetUtcNow();
        if (account is null)
        {
            Hasher.VerifyHashedPassword("", DecoyHash, password);
            return (NoSuchAccount(name, now), null);
        }

        // Locked first, and without looking at the password: a right guess during the lockout must not be told apart
        // from a wrong one, or the lockout only slows the guesser down to the speed of reading the answer.
        if (account.LockedUntil is { } until && until > now)
            return (SignInVerdict.Locked, account);

        var result = Hasher.VerifyHashedPassword(account.Name, hash!, password);
        if (result == PasswordVerificationResult.Failed)
        {
            failures++;
            var lockUntil = failures >= FailuresBeforeLockout ? now + LockoutFor : (DateTimeOffset?)null;
            Execute(db, "UPDATE users SET failures = $failures, locked_until = $until WHERE name = $name",
                ("$failures", lockUntil is null ? failures : 0), ("$until", lockUntil?.ToString("O")), ("$name", account.Name));
            transaction.Commit();
            return (lockUntil is null ? SignInVerdict.Wrong : SignInVerdict.Locked, account);
        }

        if (account.Disabled)
            return (SignInVerdict.Disabled, account);

        Execute(db, "UPDATE users SET failures = 0, locked_until = NULL" +
                    (result == PasswordVerificationResult.SuccessRehashNeeded ? ", hash = $hash" : "") + " WHERE name = $name",
            ("$hash", Hasher.HashPassword(account.Name, password)), ("$name", account.Name));
        transaction.Commit();
        return (SignInVerdict.Ok, account);
    }

    /// <summary>
    /// A name that is no account, counted as an account's wrong passwords are: only accounts could be locked, so five
    /// wrong passwords told a guesser whether a name was one. Kept in memory - nothing is written for a name nobody has -
    /// and at most <see cref="NamesCounted"/> of them, so a guesser trying names cannot grow it without end.
    /// </summary>
    private SignInVerdict NoSuchAccount(string name, DateTimeOffset now)
    {
        lock (_unknown)
        {
            if (_unknown.Count >= NamesCounted)
                foreach (var spent in _unknown.Where(n => n.Value.LockedUntil is not { } until || until <= now).Select(n => n.Key).ToList())
                    _unknown.Remove(spent);
            // Every one of them locked: a guesser is trying names faster than the lockouts run out. Forgetting them costs
            // only the counts; keeping them all is memory without end.
            if (_unknown.Count >= NamesCounted)
                _unknown.Clear();
            var (failures, lockedUntil) = _unknown.GetValueOrDefault(name);
            if (lockedUntil is { } until && until > now)
                return SignInVerdict.Locked;
            if (++failures < FailuresBeforeLockout)
            {
                _unknown[name] = (failures, null);
                return SignInVerdict.Wrong;
            }
            _unknown[name] = (0, now + LockoutFor);
            return SignInVerdict.Locked;
        }
    }

    private const int NamesCounted = 10_000;
    private readonly Dictionary<string, (int Failures, DateTimeOffset? LockedUntil)> _unknown = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The only administrator who can still act. Disabling or demoting that one locks the service out of its own
    /// administration until somebody edits the database - refused from the console and the browser alike.
    /// </summary>
    public bool IsLastAdmin(string name)
        => All().Where(a => a is { Role: Role.Admin, Disabled: false }).Select(a => a.Name).ToList() is [var only]
           && string.Equals(only, name, StringComparison.OrdinalIgnoreCase);

    /// <summary>A new password; ends the account's sessions and lifts a lockout. Why not, or null.</summary>
    public string? SetPassword(string name, string password)
        => PasswordProblem(password) ?? Change(name, "hash = $hash, failures = 0, locked_until = NULL", ("$hash", Hasher.HashPassword(name, password)));

    /// <summary>Another role; ends the account's sessions, so a lowered role is lowered now.</summary>
    public string? SetRole(string name, Role role) => Change(name, "role = $role", ("$role", (int)role));

    /// <summary>Disabled or enabled; disabling ends the account's sessions.</summary>
    public string? SetDisabled(string name, bool disabled) => Change(name, "disabled = $disabled", ("$disabled", disabled ? 1 : 0));

    private string? Change(string name, string assignments, (string, object?) value)
    {
        if (Find(name) is null)
            return $"There is no account '{name}'.";
        using var db = Open();
        Execute(db, $"UPDATE users SET {assignments}, stamp = $stamp WHERE name = $name", value, ("$stamp", NewStamp()), ("$name", name));
        return null;
    }

    // ── audit ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Every start, cancel, permission answer, settings change and sign-in, with the account (ADR 0002 §4). Written
    /// and never edited: there is no update or delete for this table anywhere.
    /// </summary>
    public void Audit(string account, string action, string? detail = null, string? remote = null)
    {
        using var db = Open();
        Execute(db, "INSERT INTO audit (at, account, action, detail, remote) VALUES ($at, $account, $action, $detail, $remote)",
            ("$at", _clock.GetUtcNow().ToString("O")), ("$account", account), ("$action", action), ("$detail", detail), ("$remote", remote));
    }

    public IReadOnlyList<(DateTimeOffset At, string Account, string Action, string? Detail, string? Remote)> AuditTrail(int newest = 200)
    {
        using var db = Open();
        using var command = db.CreateCommand();
        command.CommandText = "SELECT at, account, action, detail, remote FROM audit ORDER BY id DESC LIMIT $n";
        command.Parameters.AddWithValue("$n", newest);
        using var row = command.ExecuteReader();
        var trail = new List<(DateTimeOffset, string, string, string?, string?)>();
        while (row.Read())
            trail.Add((DateTimeOffset.Parse(row.GetString(0)), row.GetString(1), row.GetString(2),
                row.IsDBNull(3) ? null : row.GetString(3), row.IsDBNull(4) ? null : row.GetString(4)));
        return trail;
    }
}
