namespace Enactive.Engine.Tests;

using System.Globalization;
using Enactive.Application;
using Enactive.Core.Context;
using Enactive.Core.History;
using Enactive.Core.Memory;
using Enactive.Workspace;
using Xunit;

/// <summary>
/// The reports a workspace is shown as a document (WorkspaceReports) - its project memory and its machine - read once for
/// both hosts, the window's viewer and the service's pages, with the same words when there is no workspace and when the
/// read fails; and their times in the same digits on every machine.
/// </summary>
public sealed class WorkspaceReportsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-reports", Guid.NewGuid().ToString("N"));

    public WorkspaceReportsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private static readonly EnvironmentInfo Info = new(Guid.NewGuid(), Guid.NewGuid(),
        new HostInfo("Windows 11", "X64", "build-01", "svc", 8, ".NET 10", 16384), [], [], [], []);

    private static readonly EnvironmentSnapshot Snapshot = new(Guid.NewGuid(), Info.Id,
        new DateTimeOffset(2026, 10, 12, 9, 30, 15, TimeSpan.Zero), 0, 42, [], new Dictionary<string, object?> { ["uptimeHours"] = 12.5 });

    [Fact]
    public async Task The_environment_is_the_probe_s_summary_and_snapshot()
    {
        var report = await WorkspaceReports.EnvironmentAsync(_root, (_, _) => Task.FromResult((Info, Snapshot)), CancellationToken.None);

        Assert.Equal(Info.Summary() + "\n\n" + Snapshot.Describe(), report.Text);
        Assert.Equal(Info.OneLine(), report.OneLine);
    }

    [Fact]
    public async Task With_no_workspace_the_environment_says_to_set_one()
    {
        var probed = false;
        var report = await WorkspaceReports.EnvironmentAsync("  ", (_, _) => { probed = true; return Task.FromResult((Info, Snapshot)); },
            CancellationToken.None);

        Assert.Equal("Set a workspace first.", report.Text);
        Assert.Null(report.OneLine);
        Assert.False(probed);
    }

    [Fact]
    public async Task A_probe_that_fails_is_said()
    {
        var report = await WorkspaceReports.EnvironmentAsync(_root,
            (_, _) => throw new InvalidOperationException("docker is not answering"), CancellationToken.None);

        Assert.Equal("Probe failed: docker is not answering", report.Text);
        Assert.Null(report.OneLine);
    }

    [Fact]
    public async Task The_timeline_is_the_workspace_s_project_memory()
    {
        var workspace = WorkspaceInfo.For(_root);
        await MemoryStoreFactory.Create(workspace).AppendAsync(
            new MemoryEntry(Guid.NewGuid(), workspace.Id, "decision", "Keep the digest weekly.", null, DateTimeOffset.UtcNow), CancellationToken.None);

        var text = await WorkspaceReports.TimelineAsync(_root, CancellationToken.None);

        var runs = await RunStoreFactory.Create(workspace).LoadSummariesAsync(CancellationToken.None);
        var entries = await MemoryStoreFactory.Create(workspace).LoadAllAsync(CancellationToken.None);
        Assert.Equal(ProjectMemory.Render(runs, entries, workspace.RootPath), text);
        Assert.Contains("Keep the digest weekly.", text);
    }

    /// <summary>
    /// The times in both reports read the same under any regional settings: a format's ":" is the culture's time
    /// separator, its year the culture's calendar's, and its decimal mark the culture's.
    /// </summary>
    [Fact]
    public void The_reports_times_are_the_same_under_any_culture()
    {
        var at = new DateTimeOffset(2026, 10, 12, 9, 30, 15, TimeSpan.Zero);
        var local = at.ToLocalTime();
        var was = CultureInfo.CurrentCulture;
        try
        {
            // A culture whose time separator, decimal mark and calendar are all another's, made so whatever the machine says.
            var odd = (CultureInfo)CultureInfo.GetCultureInfo("th-TH").Clone();
            odd.DateTimeFormat.TimeSeparator = ".";
            odd.NumberFormat.NumberDecimalSeparator = ",";
            CultureInfo.CurrentCulture = odd;

            var snapshot = Snapshot.Describe();
            Assert.Contains("Snapshot at " + local.ToString("HH:mm:ss", CultureInfo.InvariantCulture), snapshot);
            Assert.Contains("uptimeHours = 12.5", snapshot);

            var memory = ProjectMemory.Render(
                [new RunSummary(Guid.NewGuid(), Guid.NewGuid(), "weekly digest", null, at, at, "Succeeded", ["digest.md"], ["kept it short"])],
                [new MemoryEntry(Guid.NewGuid(), Guid.NewGuid(), "note", "a note", null, at)], _root);
            Assert.Contains(local.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + "  [note] a note", memory);
            Assert.Contains("  " + local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + "  digest.md", memory);
            Assert.Contains("    " + local.ToString("HH:mm", CultureInfo.InvariantCulture) + "  Succeeded  weekly digest", memory);
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
    }
}
