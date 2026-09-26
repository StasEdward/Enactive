namespace Enactive.Workspace;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Enactive.Core.Context;

/// <summary>
/// Read-only discovery of the local environment (PLAN_v2 §2.9). Host facts and the git branch/remote
/// come straight from the runtime and the .git files (no process). Docker containers, WSL distros and
/// git dirtiness come from short, time-boxed, read-only CLI queries (docker ps / wsl -l / git status),
/// run in parallel and entirely best-effort: a missing or slow tool yields an empty result and never
/// fails or slows a run beyond its small timeout. Nothing here ever writes or mutates anything.
/// </summary>
public sealed class EnvironmentProbe : IEnvironmentProbe
{
    private const int DockerTimeoutMs = 2000;
    private const int WslTimeoutMs = 2000;
    private const int GitTimeoutMs = 1500;

    public async Task<EnvironmentInfo> ProbeAsync(WorkspaceInfo workspace, CancellationToken ct)
    {
        var host = ProbeHost();
        var (branch, remote) = ReadGitHead(workspace.RootPath);
        var isRepo = branch is not null || remote is not null
            || Directory.Exists(Path.Combine(workspace.RootPath, ".git"));

        // Fire the process-based probes in parallel; each is independently best-effort.
        var dirtyTask = isRepo ? ProbeGitDirtyAsync(workspace.RootPath, ct) : Task.FromResult<bool?>(null);
        var dockerTask = ProbeDockerAsync(ct);
        var wslTask = ProbeWslAsync(ct);

        await Task.WhenAll(dirtyTask, dockerTask, wslTask).ConfigureAwait(false);

        var git = isRepo
            ? new[] { new GitRepo(workspace.RootPath, branch, remote, dirtyTask.Result) }
            : Array.Empty<GitRepo>();

        return new EnvironmentInfo(
            Id: Guid.NewGuid(),
            WorkspaceId: workspace.Id,
            Host: host,
            Git: git,
            Docker: dockerTask.Result,
            Wsl: wslTask.Result,
            Services: Array.Empty<ServiceInfo>());
    }

    private static HostInfo ProbeHost()
    {
        long? ramMb = null;
        try { ramMb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024); } catch { /* best-effort */ }

        return new HostInfo(
            Os: RuntimeInformation.OSDescription,
            Architecture: RuntimeInformation.OSArchitecture.ToString(),
            Machine: SafeGet(() => System.Environment.MachineName),
            User: SafeGet(() => System.Environment.UserName),
            CpuCount: System.Environment.ProcessorCount,
            DotnetVersion: RuntimeInformation.FrameworkDescription,
            TotalRamMb: ramMb);
    }

    // ── Git (file-based branch/remote + optional process for dirtiness) ──────────

    private static (string? Branch, string? Remote) ReadGitHead(string root)
    {
        try
        {
            var gitDir = Path.Combine(root, ".git");
            if (!Directory.Exists(gitDir))
                return (null, null);

            string? branch = null;
            var head = Path.Combine(gitDir, "HEAD");
            if (File.Exists(head))
            {
                var text = File.ReadAllText(head).Trim();
                const string prefix = "ref: refs/heads/";
                branch = text.StartsWith(prefix, StringComparison.Ordinal) ? text[prefix.Length..] : null;
            }
            return (branch, ReadOriginRemote(Path.Combine(gitDir, "config")));
        }
        catch
        {
            return (null, null);
        }
    }

    private static string? ReadOriginRemote(string configPath)
    {
        try
        {
            if (!File.Exists(configPath))
                return null;

            var inOrigin = false;
            foreach (var raw in File.ReadAllLines(configPath))
            {
                var line = raw.Trim();
                if (line.StartsWith("[remote ", StringComparison.Ordinal))
                    inOrigin = line.Contains("\"origin\"", StringComparison.Ordinal);
                else if (line.StartsWith("[", StringComparison.Ordinal))
                    inOrigin = false;
                else if (inOrigin && line.StartsWith("url", StringComparison.Ordinal))
                {
                    var eq = line.IndexOf('=');
                    if (eq >= 0)
                        return line[(eq + 1)..].Trim();
                }
            }
        }
        catch { /* best-effort */ }
        return null;
    }

    private static async Task<bool?> ProbeGitDirtyAsync(string root, CancellationToken ct)
    {
        var (exit, output) = await AutomaticGit.RunAsync(root, ["status", "--porcelain", "--ignore-submodules=all"], ct, GitTimeoutMs)
            .ConfigureAwait(false);
        return exit == 0 ? output.Trim().Length > 0 : (bool?)null;
    }

    // ── Docker ───────────────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<DockerContainer>> ProbeDockerAsync(CancellationToken ct)
    {
        var (ok, output) = await RunAsync(
            "docker", "ps -a --format {{.Names}}|{{.Image}}|{{.Status}}", null, DockerTimeoutMs, null, ct)
            .ConfigureAwait(false);
        if (!ok)
            return Array.Empty<DockerContainer>();

        var list = new List<DockerContainer>();
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0)
                continue;
            var parts = trimmed.Split('|');
            if (parts.Length >= 3)
                list.Add(new DockerContainer(parts[0], parts[1], parts[2]));
        }
        return list;
    }

    // ── WSL (Windows only) ────────────────────────────────────────────────────────

    private static async Task<IReadOnlyList<WslDistro>> ProbeWslAsync(CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Array.Empty<WslDistro>();

        // wsl.exe prints UTF-16LE; ask the reader to decode it as Unicode.
        var (ok, output) = await RunAsync("wsl", "-l -q", null, WslTimeoutMs, Encoding.Unicode, ct)
            .ConfigureAwait(false);
        if (!ok)
            return Array.Empty<WslDistro>();

        var list = new List<WslDistro>();
        var first = true;
        foreach (var line in output.Split('\n'))
        {
            var name = line.Replace("\0", "").Trim();
            if (name.Length == 0)
                continue;
            list.Add(new WslDistro(name, Default: first));
            first = false;
        }
        return list;
    }

    // ── Process helper ─────────────────────────────────────────────────────────────

    private static async Task<(bool Ok, string Output)> RunAsync(
        string file, string args, string? workDir, int timeoutMs, Encoding? stdoutEncoding, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workDir ?? Directory.GetCurrentDirectory()
            };
            if (stdoutEncoding is not null)
                psi.StandardOutputEncoding = stdoutEncoding;

            using var proc = new Process { StartInfo = psi };
            if (!proc.Start())
                return (false, string.Empty);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            var outputTask = proc.StandardOutput.ReadToEndAsync();
            try
            {
                await proc.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (false, string.Empty);
            }

            var output = await outputTask.ConfigureAwait(false);
            return (proc.ExitCode == 0, output);
        }
        catch
        {
            // Tool missing / not on PATH / any failure → treated as "no data".
            return (false, string.Empty);
        }
    }

    private static string SafeGet(Func<string> get)
    {
        try { return get(); }
        catch { return "?"; }
    }

    // ── Full report: static info (incl. services) + a point-in-time snapshot ─────────

    private const int ServiceTimeoutMs = 1000;

    // DevOps-relevant service names to look for (best-effort; missing ones are skipped).
    private static readonly string[] ServiceCandidates =
    {
        "mysql", "mysql80", "MySQL80", "mariadb", "docker", "com.docker.service",
        "ollama", "postgresql", "redis", "nginx", "MSSQLSERVER"
    };

    /// <summary>Probes the static environment (adding local services) and captures a live snapshot.</summary>
    public async Task<(EnvironmentInfo Info, EnvironmentSnapshot Snapshot)> ProbeFullAsync(
        WorkspaceInfo workspace, CancellationToken ct)
    {
        var info = await ProbeAsync(workspace, ct).ConfigureAwait(false);
        var services = await ProbeServicesAsync(ct).ConfigureAwait(false);
        if (services.Count > 0)
            info = info with { Services = services };
        return (info, CaptureSnapshot(info));
    }

    private static EnvironmentSnapshot CaptureSnapshot(EnvironmentInfo env)
    {
        double ramPercent = 0;
        try
        {
            var gc = GC.GetGCMemoryInfo();
            if (gc.TotalAvailableMemoryBytes > 0)
                ramPercent = 100.0 * gc.MemoryLoadBytes / gc.TotalAvailableMemoryBytes;
        }
        catch { /* best-effort */ }

        var containers = env.Docker.Select(ToContainerState).ToList();

        var metrics = new Dictionary<string, object?>();
        try { metrics["processes"] = Process.GetProcesses().Length; } catch { /* best-effort */ }
        try { metrics["uptimeHours"] = Math.Round(System.Environment.TickCount64 / 3_600_000.0, 1); } catch { /* best-effort */ }
        try { metrics["gcHeapMb"] = GC.GetTotalMemory(false) / (1024 * 1024); } catch { /* best-effort */ }

        return new EnvironmentSnapshot(
            Id: Guid.NewGuid(),
            EnvironmentId: env.Id,
            At: DateTimeOffset.UtcNow,
            CpuPercent: 0,           // system CPU% is not cheap cross-platform without extra deps; left for later
            RamPercent: ramPercent,
            Containers: containers,
            Metrics: metrics);
    }

    private static ContainerState ToContainerState(DockerContainer d)
    {
        var status = d.Status ?? string.Empty;
        if (status.StartsWith("Up", StringComparison.OrdinalIgnoreCase))
            return new ContainerState(d.Name, "running", null);

        if (status.StartsWith("Exited", StringComparison.OrdinalIgnoreCase))
        {
            int? code = null;
            var open = status.IndexOf('(');
            var close = status.IndexOf(')');
            if (open >= 0 && close > open && int.TryParse(status.AsSpan(open + 1, close - open - 1), out var c))
                code = c;
            return new ContainerState(d.Name, "exited", code);
        }

        var first = status.Split(' ')[0];
        return new ContainerState(d.Name, first.Length == 0 ? "unknown" : first.ToLowerInvariant(), null);
    }

    private static async Task<IReadOnlyList<ServiceInfo>> ProbeServicesAsync(CancellationToken ct)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return Array.Empty<ServiceInfo>();

        var tasks = ServiceCandidates.Select(name => QueryServiceAsync(name, ct)).ToArray();
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        // De-duplicate by name (candidate list has case/variant overlaps) and keep the ones that exist.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<ServiceInfo>();
        foreach (var svc in results)
            if (svc is not null && seen.Add(svc.Name))
                list.Add(svc);
        return list;
    }

    private static async Task<ServiceInfo?> QueryServiceAsync(string name, CancellationToken ct)
    {
        var (ok, output) = await RunAsync("sc", $"query {name}", null, ServiceTimeoutMs, null, ct).ConfigureAwait(false);
        if (!ok)
            return null;   // sc returns a non-zero exit code for a service that does not exist

        var state = output.Contains("RUNNING", StringComparison.Ordinal) ? "running"
            : output.Contains("STOPPED", StringComparison.Ordinal) ? "stopped"
            : "present";
        return new ServiceInfo(name, state);
    }
}
