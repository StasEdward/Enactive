namespace Enactive.Engine.Tests;

using Enactive.Core.Chat;
using Enactive.Core.Events;
using Xunit;

/// <summary>
/// When the engine tells the model to change course, that is the last thing the model reads.
///
/// <para><b>Run 0947eb, 2026-09-28.</b> A local model on a coverage task re-read the same forty
/// lines of one file. The engine noticed, and said so at the foot of each repeated result: "This is
/// a call you have already made in this step, and it is being counted as no progress", rising on
/// the third to "one more turn that only repeats earlier calls and this step will be stopped". The
/// model wrote the same 1,710-character answer four times, and the step was stopped as stuck - with
/// the finding it had been asked for already written out in that answer.</para>
///
/// <para>The note was there every time. It was not LAST. After every tool result the engine moved
/// its snapshot of the commands this step had run to the end of the prompt, so the final message
/// the model read on each of those turns was that snapshot, ending "This snapshot is data, not a
/// request to repeat commands" - the same snapshot each time, because no command had run between
/// the reads. What the model answers is what it read last; the engine's warning was the message
/// before.</para>
///
/// <para>So the snapshot moves only when it changes. It is reference material, and an unchanged one
/// can stay where it already is.</para>
/// </summary>
public sealed class TheEnginesOwnWordComesLastTests
{
    private const string QuickPlan = """{"disposition":"quick_action","title":"look around"}""";
    private const string History = "Engine-owned command history";
    private const string Repeated = "you have already made in this step";

    private static int LastIndexOf(IReadOnlyList<ChatMessage> messages, string text)
    {
        for (var i = messages.Count - 1; i >= 0; i--)
            if (messages[i].Content?.Contains(text, StringComparison.Ordinal) == true)
                return i;
        return -1;
    }

    [Fact]
    public async Task A_repeat_warning_is_not_followed_by_an_unchanged_command_snapshot()
    {
        using var fx = new EngineFixture();
        fx.Write("a.txt", "one\ntwo\nthree\n");

        var provider = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("run_command", """{"command":"echo ready"}""", "c1"),
            Turn.Calls1("read_file", """{"path":"a.txt"}""", "r1"),
            Turn.Calls1("read_file", """{"path":"a.txt"}""", "r2"),
            Turn.Says("Read it; done."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "look around");

        // The request the model answered after its repeated read: the one carrying the warning.
        var warned = provider.Requests.Where(r => LastIndexOf(r.Messages, Repeated) >= 0).ToArray();
        Assert.NotEmpty(warned);

        foreach (var request in warned)
        {
            var warning = LastIndexOf(request.Messages, Repeated);
            var snapshot = LastIndexOf(request.Messages, History);

            // The snapshot is still there - the model has not lost what ran - but BEFORE the warning.
            Assert.True(snapshot >= 0, "the command snapshot should still be in the prompt");
            Assert.True(warning > snapshot,
                $"the repeat warning (message {warning}) must come after the unchanged command snapshot "
                + $"(message {snapshot}); the model answers what it read last");
            Assert.Equal(request.Messages.Count - 1, warning);
        }
    }

    /// <summary>
    /// The other half: when a command DOES run, the snapshot changes and is brought to the end, so
    /// the model sees what that command did. Moving it only when it changes must not mean never.
    /// </summary>
    [Fact]
    public async Task A_new_command_still_brings_the_snapshot_to_the_end()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickPlan),
            Turn.Calls1("run_command", """{"command":"echo first"}""", "c1"),
            Turn.Calls1("run_command", """{"command":"echo second"}""", "c2"),
            Turn.Says("Both ran; done."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "look around");

        // The request made right after the second command ends with a snapshot naming it.
        var afterSecond = provider.Requests.Last(r => r.Messages.Any(m => m.Content?.Contains("echo second", StringComparison.Ordinal) == true
                                                                            && m.Content.Contains(History, StringComparison.Ordinal)));
        var last = afterSecond.Messages[^1];
        Assert.Contains(History, last.Content ?? "", StringComparison.Ordinal);
        Assert.Contains("echo second", last.Content ?? "", StringComparison.Ordinal);
    }
}
