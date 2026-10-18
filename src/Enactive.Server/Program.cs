using System.Security.Cryptography;
using Enactive.Core.Storage;
using Enactive.Secrets;
using Enactive.Server;
using Enactive.Server.Accounts;

// Enactive as a Windows service on the local network (ADR 0002).
//
//   Enactive.Server                     run (as the service, or in a console to try it)
//   Enactive.Server --config <file>     another server.json than the account's %APPDATA%\Enactive\server.json
//   Enactive.Server --data <folder>     another data folder (its lock, its log) - to try a build without the account's
//   Enactive.Server --protect           protect a secret (the .pfx password) for THIS account, for server.json
//   Enactive.Server --add-user <name> [--role Admin|Operator|Viewer], --reset-password, --set-role, --disable-user,
//                   --enable-user, --list-users     the accounts, on this machine only (AccountCommands)
//
// Everything that keeps it from starting safely is said and it exits: settings that cannot be read, a setting that
// would open it to more than it should answer, no certificate it can use, another service on the same data folder.
// No server.json in the data folder at all is a first start: it writes one for this machine only (FirstStart).

const int ConfigProblem = 78;   // EX_CONFIG
const int Busy = 75;            // EX_TEMPFAIL

if (args.Contains("--protect", StringComparer.OrdinalIgnoreCase))
    return ProtectSecret();

var dataFolder = ArgumentAfter("--data") is { } data ? Path.GetFullPath(data) : ServerFolders.Default();
// The accounts are managed here, on the server, and need no settings or certificate to be.
if (AccountCommands.Verbs.Any(v => args.Contains(v, StringComparer.OrdinalIgnoreCase)))
    return AccountCommands.Run(args, new UserStore(dataFolder), AccountCommands.ReadHidden, Console.Out, Console.Error);

var namedConfig = ArgumentAfter("--config");
var configPath = namedConfig ?? Path.Combine(dataFolder, "server.json");

// A first start on a machine: settings for this machine only and a certificate of its own (FirstStart).
try
{
    if (FirstStart.IfMissing(namedConfig, dataFolder, configPath) is { } written)
        Note(written);
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException or SecretProtectionException)
{
    return Refuse($"There is no server.json at {configPath}, and one could not be written for this machine: {ex.Message}", ConfigProblem);
}

var options = ServerOptions.Load(configPath, out var unreadable);
if (options is null)
    return Refuse(unreadable!, ConfigProblem);

if (options.Problems() is { Count: > 0 } problems)
    return Refuse($"{configPath} does not let the service start safely:\n  " + string.Join("\n  ", problems), ConfigProblem);

using var certificate = ServerCertificate.Load(options.Certificate!, out var noCertificate);
if (certificate is null)
    return Refuse(noCertificate!, ConfigProblem);

using var folderLock = DataFolderLock.TryTake(dataFolder, out var holder);
if (folderLock is null)
    return Refuse($"Another Enactive service is using {dataFolder}"
                  + (holder is null ? "." : $": {holder.Describe()}."), Busy);

var app = ServerApp.Build(args, options, dataFolder, certificate);
await app.RunAsync();
return 0;

// ── helpers ────────────────────────────────────────────────────────────────

string? ArgumentAfter(string name)
{
    var at = Array.FindIndex(args, a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));
    return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
}

// Said on stderr for a person at a console, and into the service's start log for a service nobody watches start:
// a service that exits at start shows "stopped" and nothing else unless the reason is written somewhere.
int Refuse(string why, int code)
{
    Console.Error.WriteLine("Enactive.Server did not start. " + why);
    StartLog("did not start. " + why);
    return code;
}

// What the service did for itself before starting, said where a refusal would be: a service account nobody watches
// start would otherwise have new settings and a certificate it was never told about.
void Note(string what)
{
    Console.WriteLine(what);
    StartLog(what);
}

void StartLog(string line)
{
    try
    {
        var logs = Path.Combine(dataFolder, "logs", "server");
        Directory.CreateDirectory(logs);
        File.AppendAllText(Path.Combine(logs, "start.log"), $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {line}{Environment.NewLine}");
    }
    catch { /* the console has it */ }
}

// Read from the keyboard, never from an argument: an argument stays in the shell's history and the process list.
static int ProtectSecret()
{
    Console.WriteLine(Secret.Protect(AccountCommands.ReadHidden("Secret to protect for this account (not shown): ")));
    return 0;
}
