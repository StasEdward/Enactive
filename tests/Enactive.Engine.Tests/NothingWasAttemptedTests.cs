namespace Enactive.Engine.Tests;

using Enactive.Core.Mail;
using Enactive.Core.Tools;
using Enactive.Tools;
using Xunit;

/// <summary>
/// A refusal that happens BEFORE a tool attempts anything says so — every tool, not the four that
/// happened to cost a run.
///
/// <para><b>Why a sweep and not four more one-offs.</b> Four runs died in eight hours (§9bj–§9bm),
/// in four different places, of one mistake made four times: a call that never happened, reported
/// as work that failed. Each fix was written where it was found, so the fifth place would have
/// waited for the fifth run. This file is the class.</para>
///
/// <para><b>Why the verdict matters more for some tools than others.</b> An open failure is closed
/// by DOING the thing: producing the file the call named, repeating the operation, calling a tool
/// of the same kind. So <c>write_file</c> is forgiving — any later write closes it. But
/// <c>send_email</c>, <c>compare_files</c>, <c>git</c> and <c>docker</c> name no file, are not
/// shells and change nothing, so a failure recorded against one of them can be closed by NOTHING
/// except re-sending the byte-identical call. For those, a wrong verdict is permanent.</para>
///
/// <para><b>What this does NOT do.</b> A call that was attempted and went wrong stays a failure,
/// and the second test says so. The step-level guard stays too: a call that never ran is forgiven
/// only in a step that CHANGED something, so a step which merely asked questions and got none
/// answered still cannot report success.</para>
/// </summary>
public sealed class NothingWasAttemptedTests
{
    private static MailAccount Configured(params string[] recipients)
        => new("smtp.test", 587, true, "me@test", "secret", "me@test", recipients);

    private static readonly MailAccount NotConfigured = new("", 0, false, "", "", "", []);

    public static TheoryData<string, string> Refusals()
    {
        var data = new TheoryData<string, string>();

        // The four that were measured, each in the run it ended.
        data.Add("search_files",  """{"pattern":"x","glob":"src/Enactive.Remote.*/*.cs"}""");
        data.Add("count_matches", """{"pattern":"x","glob":"src/*/*.cs"}""");
        data.Add("file_stats",    """{"glob":"src/*/*.cs"}""");
        data.Add("git",           """{"args":"[\"status\", \"--short\"]"}""");

        // Arguments that are simply absent: "not what the model meant to send".
        data.Add("write_file",    """{"path":"a.txt"}""");
        data.Add("edit_file",     """{"path":"a.txt","old_string":"a"}""");
        data.Add("search_files",  """{}""");
        data.Add("read_file",     """{}""");

        // A path outside the workspace - refused before anything is opened. Every tool that takes
        // one, because the fifth place is always the one nobody fixed.
        data.Add("compare_files",     """{"a":"../../secrets.txt","b":"a.txt"}""");
        data.Add("read_file",         """{"path":"../../secrets.txt"}""");
        data.Add("list_dir",          """{"path":"../../"}""");
        data.Add("write_file",        """{"path":"../../x.txt","content":"x"}""");
        data.Add("edit_file",         """{"path":"../../x.txt","old_string":"a","new_string":"b"}""");
        data.Add("create_directory",  """{"path":"../../x"}""");
        data.Add("move_file",         """{"from":"a.txt","to":"../../x.txt"}""");
        data.Add("copy_file",         """{"from":"a.txt","to":"../../x.txt"}""");
        data.Add("delete_file",       """{"path":"../../x.txt"}""");

        // Malformed JSON is the plainest case of all.
        data.Add("list_dir",      """{"path": }""");

        return data;
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task A_refusal_before_the_work_says_nothing_ran(string tool, string args)
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "a");

        var result = await fx.Invoke(EngineFixture.ToolNamed(tool), args);

        Assert.False(result.Success, $"{tool} was expected to refuse {args}");
        Assert.True(result.DidNotRun,
            $"{tool} refused {args} before doing anything, but reported it as work that failed: {result.Error}");
    }

    /// <summary>
    /// send_email separately, because its refusals are about the ACCOUNT rather than the arguments
    /// and it is the most expensive of the dead ends: nothing left the machine in any of these, and
    /// an unsent message recorded as a failed send poisons its step permanently.
    /// </summary>
    [Fact]
    public async Task Nothing_that_was_never_sent_is_a_failed_send()
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "a");

        var nowhere = await fx.Invoke(new SendEmailTool(NotConfigured),
                                      """{"subject":"hi","body":"there"}""");
        Assert.True(nowhere.DidNotRun, nowhere.Error);

        var unlisted = await fx.Invoke(new SendEmailTool(Configured("me@test")),
                                       """{"to":"someone@else","subject":"hi","body":"there"}""");
        Assert.True(unlisted.DidNotRun, unlisted.Error);

        var missing = await fx.Invoke(new SendEmailTool(Configured("me@test")),
                                      """{"subject":"hi","body":"there","attachments":["nope.txt"]}""");
        Assert.True(missing.DidNotRun, missing.Error);
    }

    /// <summary>
    /// THE BOUNDARY, and the reason none of this is a licence to report success. A call the tool
    /// ATTEMPTED and that went wrong is still a failure: the file that is not there to move, the
    /// passage that is not in the file, the command that ran and exited non-zero. Widening the
    /// sweep until these go green would let a step finish "Done" over work that never happened —
    /// which is the entire reason OpenFailures exists.
    /// </summary>
    [Theory]
    [InlineData("move_file",   """{"from":"no-such-file.txt","to":"b.txt"}""")]
    [InlineData("edit_file",   """{"path":"a.txt","old_string":"not in the file","new_string":"x"}""")]
    [InlineData("run_command", """{"command":"exit /b 3"}""")]
    public async Task A_call_that_was_attempted_and_failed_is_still_a_failure(string tool, string args)
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "a");

        var result = await fx.Invoke(EngineFixture.ToolNamed(tool), args);

        Assert.False(result.Success);
        Assert.False(result.DidNotRun,
            $"{tool} attempted {args} and it went wrong - that is a failure the step must keep: {result.Error}");
    }
}
