namespace Enactive.Engine.Tests;

using Enactive.Core.Context;
using Xunit;

/// <summary>
/// Two more ways of coming back with nothing, both from run <c>9fe6aa</c>, 2026-09-23.
///
/// <para>§9ap decided on 2026-09-20 that a lookup told "not there" is an ANSWER, and reads that out
/// of PowerShell's structured error record — <c>FullyQualifiedErrorId : PathNotFound,…Select
/// StringCommand</c>. Two things say the same thing and are invisible to that reading.</para>
///
/// <para><b>Prose.</b> <c>where smartctl &amp; where nvme &amp; where wmic</c> printed
/// <i>INFO: Could not find files for the given pattern(s).</i> three times and exited 1. Asking
/// whether a tool is installed before reaching for it is what a careful agent should do.</para>
///
/// <para><b>Refusal.</b> <c>Get-PhysicalDisk | Get-StorageReliabilityCounter</c> answered
/// <i>PermissionDenied</i> without elevation. The step collected what it could by other means,
/// wrote "Access denied (non-elevated)" into the report, sent it — and was failed for having been
/// told no. Twice in one night.</para>
/// </summary>
public sealed class ToldNoIsAnAnswerTests
{
    /// <summary>The output of the reported call, verbatim.</summary>
    private const string WhereFoundNothing =
        "[stderr]\n"
        + "INFO: Could not find files for the given pattern(s).\n"
        + "INFO: Could not find files for the given pattern(s).\n"
        + "INFO: Could not find files for the given pattern(s).";

    /// <summary>The other one, verbatim, wrapped exactly as PowerShell wrapped it.</summary>
    private const string AccessDenied =
        "[stderr]\n"
        + "Get-StorageReliabilityCounter : Access to a CIM resource was not available to the client.\n"
        + "At line:2 char:20\n"
        + "+ Get-PhysicalDisk | Get-StorageReliabilityCounter | Select-Object Devi ...\n"
        + "+                    ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~\n"
        + "    + CategoryInfo          : PermissionDenied: (PS_StorageCmdlets:ROOT/Microsoft/..._StorageCmdlets) [Get-StorageReli \n"
        + "   abilityCounter], CimException\n"
        + "    + FullyQualifiedErrorId : MI RESULT 2,Get-StorageReliabilityCounter";

    [Fact]
    public void A_name_that_is_not_on_the_path_is_an_answer()
        => Assert.Equal(ShellVerdict.FoundNothing,
                        ShellOutcome.Of("where smartctl & where nvme & where wmic", WhereFoundNothing));

    [Fact]
    public void A_read_the_system_refused_is_an_answer()
        => Assert.Equal(ShellVerdict.FoundNothing,
                        ShellOutcome.Of(
                            "Get-PhysicalDisk | Get-StorageReliabilityCounter | Select-Object DeviceId",
                            AccessDenied));

    /// <summary>
    /// THE BOUNDARY on the first one. A <c>where</c> that found two names of three prints the paths
    /// it found, and a line that is a path is not "could not find" — so the call is what it was
    /// before: something that partly worked and exited non-zero.
    /// </summary>
    [Fact]
    public void A_lookup_that_found_something_is_not_a_miss()
        => Assert.Equal(ShellVerdict.Ran,
                        ShellOutcome.Of(
                            "where git & where nvme",
                            "C:/Program Files/Git/cmd/git.exe\n"
                            + "[stderr]\nINFO: Could not find files for the given pattern(s)."));

    /// <summary>
    /// THE BOUNDARY on the second, and the one that keeps it narrow. PowerShell's verbs are a
    /// contract: a denied <c>Get-</c> is a question left unanswered, a denied <c>Remove-</c> is
    /// work the step wanted to HAPPEN and which did not.
    /// </summary>
    [Fact]
    public void A_refused_change_is_not_an_answer()
        => Assert.Equal(ShellVerdict.Ran,
                        ShellOutcome.Of(
                            "Remove-Item C:/Windows/System32/drivers/etc/hosts",
                            "[stderr]\n"
                            + "Remove-Item : Access to the path is denied.\n"
                            + "    + CategoryInfo          : PermissionDenied: (hosts:FileInfo) [Remove-Item], UnauthorizedAccessException\n"
                            + "    + FullyQualifiedErrorId : RemoveFileSystemItemUnAuthorizedAccess,Microsoft.PowerShell.Commands.RemoveItemCommand"));

    /// <summary>
    /// And a script refused one thing and BROKEN on another is still broken. Every error record has
    /// to be the refusal, or the reading is forgiving a failure it cannot see.
    /// </summary>
    [Fact]
    public void One_refusal_does_not_cover_a_real_error_beside_it()
        => Assert.Equal(ShellVerdict.Ran,
                        ShellOutcome.Of(
                            "Get-StorageReliabilityCounter; Get-Content missing.txt",
                            AccessDenied + "\n"
                            + "Get-Content : Cannot find path 'missing.txt' because it does not exist.\n"
                            + "    + CategoryInfo          : ObjectNotFound: (missing.txt:String) [Get-Content], ItemNotFoundException"));
}
