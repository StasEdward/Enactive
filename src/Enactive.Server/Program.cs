using Enactive.Core.Storage;
using Enactive.Secrets;
using Enactive.Server;

// Enactive as a Windows service on the local network (ADR 0002).
//
//   Enactive.Server                     run (as the service, or in a console to try it)
//   Enactive.Server --config <file>     another server.json than the account's %APPDATA%\Enactive\server.json
//   Enactive.Server --data <folder>     another data folder (its lock, its log) - to try a build without the account's
//   Enactive.Server --protect           protect a secret (the .pfx password) for THIS account, for server.json
//
// Everything that keeps it from starting safely is said and it exits: no settings, a setting that would open it to
// more than it should answer, no certificate it can use, another service on the same data folder.

const int ConfigProblem = 78;   // EX_CONFIG
const int Busy = 75;            // EX_TEMPFAIL

if (args.Contains("--protect", StringComparer.OrdinalIgnoreCase))
    return ProtectSecret();

var dataFolder = ArgumentAfter("--data") is { } data ? Path.GetFullPath(data) : ServerFolders.Default();
var configPath = ArgumentAfter("--config") ?? Path.Combine(dataFolder, "server.json");

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
    try
    {
        var logs = Path.Combine(dataFolder, "logs", "server");
        Directory.CreateDirectory(logs);
        File.AppendAllText(Path.Combine(logs, "start.log"), $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} did not start. {why}{Environment.NewLine}");
    }
    catch { /* stderr has it */ }
    return code;
}

// Read from the keyboard, never from an argument: an argument stays in the shell's history and the process list.
static int ProtectSecret()
{
    Console.Error.Write("Secret to protect for this account (not shown): ");
    var secret = new System.Text.StringBuilder();
    while (true)
    {
        var key = Console.ReadKey(intercept: true);
        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace) { if (secret.Length > 0) secret.Length--; continue; }
        secret.Append(key.KeyChar);
    }
    Console.Error.WriteLine();
    Console.WriteLine(Secret.Protect(secret.ToString()));
    return 0;
}
