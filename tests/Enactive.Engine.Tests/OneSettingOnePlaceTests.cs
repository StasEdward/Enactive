namespace Enactive.Engine.Tests;

using Xunit;

/// <summary>
/// A setting is edited in ONE screen. Two screens writing one value is not a convenience; it is a
/// race between them, and the loser is whichever the person did not open.
///
/// <para><b>What this cost, 2026-09-23.</b> <c>send_email</c> could be ticked in two places: under
/// the role, with every other tool, and in Settings → SMTP under "Who may send". The SMTP pane
/// built its rows when the window opened and, on every Save, called
/// <c>MailRoles.Apply(workers, ticked)</c> — and <c>Apply</c> TAKES the tool from every role it is
/// not given. So: tick the tool in the Team editor, press Apply, press Save, and the SMTP pane —
/// which was never opened, whose rows still said "nobody" — revoked it on the way out. The tick was
/// set twice, across two rebuilds and two restarts, and lost both times. Nothing was broken; two
/// screens simply disagreed and the stale one wrote last.</para>
///
/// <para>The SMTP pane now REPORTS who may send and no longer decides it. This test is the fence:
/// a view model calling <c>Apply</c> is that defect being rebuilt.</para>
/// </summary>
public sealed class OneSettingOnePlaceTests
{
    [Fact]
    public void No_view_model_grants_send_email_behind_the_role_editor()
    {
        var views = Path.Combine(RepositoryRoot(), "src", "Enactive.App.Ui");
        Assert.True(Directory.Exists(views), views + " is not there");

        var callers = Directory
            .EnumerateFiles(views, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => Code(f).Contains("MailRoles.Apply", StringComparison.Ordinal)
                        || Code(f).Contains("MailRoleRules.Apply", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToArray();

        Assert.True(callers.Length == 0,
            "These write the send_email grant from outside the role editor: "
            + string.Join(", ", callers)
            + ". Apply REVOKES the tool from every role it is not handed, so a screen calling it "
            + "with rows built before another screen's edit silently undoes that edit. Tools are "
            + "granted under the role, with every other tool, and nowhere else.");
    }

    /// <summary>
    /// The file with its line comments taken out. The note explaining WHY this must not happen
    /// names the call, and a check that cannot tell a warning from the thing it warns about is
    /// worse than none - it would be satisfied by deleting the explanation.
    /// </summary>
    private static string Code(string file)
        => string.Join(Environment.NewLine,
            File.ReadAllLines(file)
                .Select(line => line.TrimStart().StartsWith("//", StringComparison.Ordinal) ? "" : line));

    private static string RepositoryRoot() => TestRepository.Root;
}
