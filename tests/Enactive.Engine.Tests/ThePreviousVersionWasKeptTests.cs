namespace Enactive.Engine.Tests;

using Enactive.Tools;
using Xunit;

/// <summary>
/// A file this run created, replaced by a later step, still has its previous version - and
/// <c>write_file</c> says so.
///
/// <para><b>Measured 2026-09-24 15:37, run a2142be6.</b> Step 1 created <c>Docs/DRIFT_ollama.md</c>;
/// step 2 replaced it and was told:</para>
///
/// <code>
/// REPLACED the existing file 'Docs/DRIFT_ollama.md' (27626 bytes).
/// Its previous version could NOT be backed up and is gone.
/// </code>
///
/// <para>Untrue. Every replacing write takes a backup, and that one had just taken step 1's version.
/// <c>CanRestore</c> was reading the FIRST write of the run - "was this file here before the run?" -
/// which for a file the run made itself is always no. The reviewer is handed that sentence as
/// ground truth, and every rewrite of the report repeated it.</para>
/// </summary>
public sealed class ThePreviousVersionWasKeptTests
{
    [Fact]
    public async Task A_file_this_run_created_keeps_its_version_when_a_later_step_replaces_it()
    {
        using var fx = new EngineFixture();

        var stepOne = fx.Artifacts.BeginStep();
        var created = await fx.Invoke(new WriteFileTool(),
            """{"path":"report.md","content":"findings from step one"}""", stepOne);
        Assert.Contains("Created new file", created.Output, StringComparison.Ordinal);

        var stepTwo = fx.Artifacts.BeginStep();
        var replaced = await fx.Invoke(new WriteFileTool(),
            """{"path":"report.md","content":"findings from steps one and two"}""", stepTwo);

        Assert.Contains("was kept and can be restored", replaced.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("could NOT be backed up", replaced.Output, StringComparison.Ordinal);

        // And it is TRUE, which is the point of saying it: undoing step 2 gives back step 1's file.
        await stepTwo.RevertAsync(new[] { "report.md" }, default);
        Assert.Equal("findings from step one", fx.Read("report.md"));
    }

    /// <summary>
    /// THE BOUNDARY. A file that did not exist has no previous version, and the first write of it
    /// still says "Created" - nothing is promised that is not there.
    /// </summary>
    [Fact]
    public async Task A_new_file_promises_nothing()
    {
        using var fx = new EngineFixture();

        var created = await fx.Invoke(new WriteFileTool(),
            """{"path":"fresh.md","content":"new"}""", fx.Artifacts.BeginStep());

        Assert.Contains("Created new file", created.Output, StringComparison.Ordinal);
        Assert.False(fx.Artifacts.CanRestore("fresh.md"));
    }
}
