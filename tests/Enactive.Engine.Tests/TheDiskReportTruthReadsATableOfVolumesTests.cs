namespace Enactive.Engine.Tests;

using System.Diagnostics;
using Enactive.Bench;
using Xunit;

/// <summary>
/// The disk-report scenario's truth names every fixed volume of the machine it runs on. It wanted each as "C:", and
/// every run of 2026-10-17/18 wrote a right table that named them "C" and "D" - a column of volumes, as Get-PSDrive prints
/// it - and was scored FALSE PASS. A bare letter heading a row of such a table names the volume; a letter inside a word
/// or a path does not. Checked with the scenario's own file and this machine's own volumes, as the bench checks it.
/// </summary>
public sealed class TheDiskReportTruthReadsATableOfVolumesTests : IDisposable
{
    private readonly string _workspace = Directory.CreateTempSubdirectory("enactive-disk-report-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_workspace, true); } catch (IOException) { }
    }

    private static string ScenarioFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "bench", "scenarios", "disk-report", "scenario.json")))
                return Path.Combine(dir.FullName, "bench", "scenarios", "disk-report");
        throw new InvalidOperationException("bench/scenarios/disk-report not found above " + AppContext.BaseDirectory);
    }

    /// <summary>The machine's fixed volumes, as the truth's own command finds them.</summary>
    private static string[] Volumes()
    {
        var psi = new ProcessStartInfo("powershell.exe")
        {
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command",
                "Get-Volume | Where-Object { $_.DriveLetter -and $_.DriveType -eq 'Fixed' -and $_.Size -gt 0 } | ForEach-Object { $_.DriveLetter }" },
            RedirectStandardOutput = true, UseShellExecute = false
        };
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private async Task<bool> DrivesNamedAsync(string report)
    {
        File.WriteAllText(Path.Combine(_workspace, "disk_report.txt"), report);
        var results = await Truth.CheckAsync(Scenario.Load(ScenarioFolder()), _workspace, CancellationToken.None);
        return results.Single(r => r.Id == "every-drive-named").Passed;
    }

    [Fact]
    public async Task A_table_of_volumes_by_bare_letter_names_them()
    {
        if (!OperatingSystem.IsWindows()) return;
        var volumes = Volumes();
        Assert.NotEmpty(volumes);

        // As 20261018-004059 wrote it, from Get-PSDrive.
        Assert.True(await DrivesNamedAsync("Name Used(GB) Free(GB) Total(GB)\n---- -------- -------- ---------\n"
            + string.Concat(volumes.Select(v => $"{v}      248,61  3476,32   3724,93\n"))));
        Assert.True(await DrivesNamedAsync("| Диск | Занято |\n|---|---|\n" + string.Concat(volumes.Select(v => $"| {v} | 13,1 % |\n"))));
        Assert.True(await DrivesNamedAsync(string.Concat(volumes.Select(v => $"| {v}: | 13,1 % |\n"))));
    }

    [Fact]
    public async Task A_letter_inside_a_word_or_a_path_names_nothing()
    {
        if (!OperatingSystem.IsWindows()) return;
        var volumes = Volumes();

        Assert.False(await DrivesNamedAsync(string.Concat(volumes.Select(v => $"{v}# x/{v} 12 x\\{v} 3 {v}{v} 10 {v}2{v} 5\n"))));
    }
}
