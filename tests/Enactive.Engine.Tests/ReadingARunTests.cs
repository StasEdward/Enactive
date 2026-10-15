namespace Enactive.Engine.Tests;

using System.Diagnostics;
using Enactive.Application;
using Enactive.Application.Projections;
using Enactive.Core.Events;
using Enactive.Core.History;
using Enactive.Settings;
using Xunit;

/// <summary>
/// Reading a finished run, as the window and the service both show it (server plan 1.6): its head (RunDigest), where
/// its models ran (ModelReaches), and a file it wrote - only ever from inside its workspace (WorkspaceFiles).
/// </summary>
public sealed class ReadingARunTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "enactive-reading", Guid.NewGuid().ToString("N"));
    private readonly string _workspace;

    public ReadingARunTests()
    {
        _workspace = Path.Combine(_root, "project");
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* a temp folder */ }
    }

    private static RunRecord Record(RunUsage? usage = null, RunSettings? settings = null, params string[] artifacts)
    {
        var at = new DateTimeOffset(2026, 10, 11, 9, 0, 0, TimeSpan.Zero);
        return new RunRecord(Guid.NewGuid(), Guid.NewGuid(), "digest the inbox", "qwen", at, at.AddSeconds(95), "Completed",
            [
                new RunEventRecord(at, nameof(EventKind.IntentReceived), "Intent", Payload: WorkEventPayload.RequestPayload("digest the inbox")),
                new RunEventRecord(at, nameof(EventKind.ToolInvoked), "read_file a.md"),
                new RunEventRecord(at, nameof(EventKind.ToolInvoked), "write_file digest.md")
            ],
            artifacts, [], settings, usage);
    }

    // ── the head of a past run ──────────────────────────────────────────────

    /// <summary>A run that reported no usage says so; it is not a run that cost nothing.</summary>
    [Fact]
    public void A_run_with_no_usage_says_nothing_was_reported_not_zero()
    {
        var digest = RunDigest.Of(Record());

        Assert.Equal("—", digest.TokensText);
        Assert.Equal("nothing reported", digest.TokensDetail);
    }

    [Fact]
    public void The_head_says_how_long_how_many_calls_and_what_it_was_allowed()
    {
        var digest = RunDigest.Of(Record(new RunUsage(1200, 300), new RunSettings(2, "Ask before writing", "writer", Staged: false)));

        Assert.Equal("1m 35s", digest.ElapsedText);
        Assert.Equal(2, digest.ToolCalls);
        Assert.Equal("1.5k", digest.TokensText);
        Assert.Equal("1.2k in · 300 out", digest.TokensDetail);
        Assert.True(digest.HasSettings);
        Assert.Equal(("Ask before writing", "writer", "changes were applied directly"), (digest.AutonomyText, digest.WorkerText, digest.StagingText));
        Assert.True(digest.CanRetry);
    }

    /// <summary>A run from before its settings were kept says nothing about them - today's level is not its history.</summary>
    [Fact]
    public void A_run_recorded_before_its_settings_were_kept_claims_none()
    {
        var digest = RunDigest.Of(Record());

        Assert.False(digest.HasSettings);
        Assert.Equal("", digest.AutonomyText);
    }

    /// <summary>Older records kept "FileSet: path"; only a known kind is taken off the front, never a path's own colon.</summary>
    [Fact]
    public void A_recorded_file_is_its_path()
        => Assert.Equal(["notes/a.md", "odd: name.md"],
            RunDigest.Of(Record(artifacts: ["FileSet: notes/a.md", "odd: name.md"])).Artifacts);

    // ── where a model ran ───────────────────────────────────────────────────

    /// <summary>A provider the settings no longer have is unknown - not cloud, which would put made-up figures beside real ones.</summary>
    [Fact]
    public void A_provider_is_local_cloud_or_unknown_by_the_settings()
    {
        var settings = new AppSettings();
        settings.Providers.Clear();
        settings.Providers.Add(new ProviderConfig { Id = "box", BaseUrl = "http://127.0.0.1:8080" });
        settings.Providers.Add(new ProviderConfig { Id = "bought", BaseUrl = "https://api.example.com" });

        Assert.Equal(ModelReach.Local, ModelReaches.Of(settings, "box"));
        Assert.Equal(ModelReach.Cloud, ModelReaches.Of(settings, "BOUGHT"));
        Assert.Equal(ModelReach.Unknown, ModelReaches.Of(settings, "renamed"));
        Assert.Equal(ModelReach.Unknown, ModelReaches.Of(settings, null));
    }

    // ── a file it wrote ─────────────────────────────────────────────────────

    [Fact]
    public void A_text_file_of_the_workspace_is_read()
    {
        File.WriteAllText(Path.Combine(_workspace, "digest.md"), "# Inbox\nthree mails");

        Assert.Equal(("# Inbox\nthree mails", (string?)null), WorkspaceFiles.Read(_workspace, "digest.md"));
    }

    [Theory]
    [InlineData("..\\secret.txt")]
    [InlineData("../secret.txt")]
    [InlineData("C:\\Windows\\win.ini")]
    public void A_path_out_of_the_workspace_is_not_read(string path)
    {
        File.WriteAllText(Path.Combine(_root, "secret.txt"), "not the workspace's");

        var (text, problem) = WorkspaceFiles.Read(_workspace, path);

        Assert.Null(text);
        Assert.Equal("That path is not in this workspace.", problem);
    }

    /// <summary>
    /// "docs" pointed at a folder elsewhere is inside the workspace by name and outside it by fact. A real junction:
    /// one made by mklink /J, which needs no administrator.
    /// </summary>
    [Fact]
    public void A_file_reached_through_a_junction_is_not_read()
    {
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(elsewhere, "secret.txt"), "not the workspace's");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(_workspace, "docs")}\" \"{elsewhere}\"")
               { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true })!)
            mklink.WaitForExit();
        Assert.True(Directory.Exists(Path.Combine(_workspace, "docs")), "the junction was not made");

        var (text, problem) = WorkspaceFiles.Read(_workspace, "docs\\secret.txt");

        Assert.Null(text);
        Assert.Contains("'docs' is a link", problem);
    }

    [Fact]
    public void A_file_too_large_for_a_page_is_named_with_its_size()
    {
        File.WriteAllBytes(Path.Combine(_workspace, "huge.log"), new byte[WorkspaceFiles.MaxBytes + 1].Select(_ => (byte)'x').ToArray());

        Assert.Contains("a page shows files up to 1 MB", WorkspaceFiles.Read(_workspace, "huge.log").Problem);
    }

    [Fact]
    public void A_binary_file_is_not_shown_as_text()
    {
        File.WriteAllBytes(Path.Combine(_workspace, "tool.exe"), [0x4D, 0x5A, 0x00, 0x01]);

        Assert.Equal("The file is not text.", WorkspaceFiles.Read(_workspace, "tool.exe").Problem);
    }

    [Fact]
    public void A_file_that_is_gone_is_said_to_be_gone()
        => Assert.Equal("The file is not there any more.", WorkspaceFiles.Read(_workspace, "gone.md").Problem);
}
