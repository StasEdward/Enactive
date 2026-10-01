namespace Enactive.Core.Context;

using System.Text;

// Environment awareness: the app knows the real world, not just a conversation.
// NOTE: the static "what exists" record is named EnvironmentInfo rather than the spec's bare
// "Environment" on purpose — a domain type called Environment would clash with System.Environment
// (imported everywhere via ImplicitUsings) and make the bare name ambiguous. EnvironmentSnapshot,
// the point-in-time reading, keeps its spec name. v1 fills Host + Git only; Docker/WSL/Services and
// live Metrics are defined here so they can be added later without changing the contract.

/// <summary>OS / machine facts.</summary>
public sealed record HostInfo(
    string Os,
    string Architecture,
    string Machine,
    string User,
    int CpuCount,
    string? DotnetVersion,
    long? TotalRamMb);

/// <summary>A git working copy discovered at or under the workspace root.</summary>
public sealed record GitRepo(
    string Path,
    string? Branch,
    string? Remote,
    bool? IsDirty);   // null = not computed in this slice

/// <summary>A docker container (populated in a later slice).</summary>
public sealed record DockerContainer(string Name, string Image, string Status);

/// <summary>A WSL distro (populated in a later slice).</summary>
public sealed record WslDistro(string Name, bool Default);

/// <summary>A local service (populated in a later slice).</summary>
public sealed record ServiceInfo(string Name, string Status);

/// <summary>A container's state within an <see cref="EnvironmentSnapshot"/>.</summary>
public sealed record ContainerState(string Name, string State, int? ExitCode);

/// <summary>Persistent model of the machine/world — "what exists".</summary>
public sealed record EnvironmentInfo(
    Guid Id,
    Guid WorkspaceId,
    HostInfo Host,
    IReadOnlyList<GitRepo> Git,
    IReadOnlyList<DockerContainer> Docker,
    IReadOnlyList<WslDistro> Wsl,
    IReadOnlyList<ServiceInfo> Services)
{
    /// <summary>A compact, human-readable block the agent can read in its prompt.</summary>
    public string Summary()
    {
        var lines = new List<string>();
        var ram = Host.TotalRamMb is { } mb ? $", ~{mb / 1024} GB RAM" : "";
        var net = Host.DotnetVersion is { } dv ? $", {dv}" : "";
        lines.Add($"Host: {Host.Os} ({Host.Architecture}), {Host.CpuCount} CPUs{net}{ram}; machine {Host.Machine}, user {Host.User}");

        if (Git.Count > 0)
        {
            foreach (var repo in Git)
            {
                var dirty = repo.IsDirty is null ? "" : repo.IsDirty.Value ? ", dirty" : ", clean";
                var remote = string.IsNullOrEmpty(repo.Remote) ? "" : $", remote {repo.Remote}";
                lines.Add($"Git: branch {repo.Branch ?? "(detached)"}{remote}{dirty}");
            }
        }
        else
        {
            lines.Add("Git: not a repository");
        }

        if (Docker.Count > 0)
            lines.Add("Docker: " + string.Join(", ", Docker.Select(d => $"{d.Name}={d.Status}")));
        if (Wsl.Count > 0)
            lines.Add("WSL: " + string.Join(", ", Wsl.Select(w => w.Name)));
        if (Services.Count > 0)
            lines.Add("Services: " + string.Join(", ", Services.Select(s => $"{s.Name}={s.Status}")));

        return string.Join("\n", lines);
    }

    /// <summary>A one-liner for event/log summaries.</summary>
    public string OneLine()
    {
        var git = Git.Count > 0 ? $", git {Git[0].Branch ?? "detached"}" : "";
        return $"{Host.Os} ({Host.Architecture}), {Host.CpuCount} CPUs{git}";
    }
}

/// <summary>Point-in-time reading — "how it is doing right now". Captured in a later slice.</summary>
public sealed record EnvironmentSnapshot(
    Guid Id,
    Guid EnvironmentId,
    DateTimeOffset At,
    double CpuPercent,
    double RamPercent,
    IReadOnlyList<ContainerState> Containers,
    IReadOnlyDictionary<string, object?> Metrics)
{
    /// <summary>A compact, human-readable rendering of this point-in-time reading.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Snapshot at {At.ToLocalTime():HH:mm:ss}");
        sb.AppendLine($"RAM in use: {RamPercent:F0}%" + (CpuPercent > 0 ? $"   CPU: {CpuPercent:F0}%" : ""));
        if (Containers.Count > 0)
        {
            sb.AppendLine("Containers:");
            foreach (var c in Containers)
                sb.AppendLine($"  {c.Name}: {c.State}" + (c.ExitCode is { } ec ? $" (exit {ec})" : ""));
        }
        foreach (var kv in Metrics)
            sb.AppendLine($"  {kv.Key} = {kv.Value}");
        return sb.ToString().TrimEnd();
    }
}

/// <summary>Discovers the local environment (read-only). Implementations live outside Core.</summary>
public interface IEnvironmentProbe
{
    Task<EnvironmentInfo> ProbeAsync(WorkspaceInfo workspace, CancellationToken ct);
}
