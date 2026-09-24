namespace Enactive.Engine.Tests;

using Enactive.Core.Chat;
using Enactive.Core.Events;
using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// A tool name the engine does not have is a typo, and it is told as one — not as a permission the
/// model lacks.
///
/// <para><b>What this cost, twice in half an hour on 2026-09-22.</b> deepseek-flash, halfway
/// through writing a PowerShell script in which every cmdlet is <c>Verb-Noun</c>, carried the
/// hyphen into the tool name and sent <c>run-powershell</c> — seventeen times in one run, with
/// <c>run_powershell</c> sitting in the tool list in front of it. Nothing in this codebase spells
/// it with a hyphen; the model simply borrowed PowerShell's own naming.</para>
///
/// <para>The name fell through to the role gate, which is the only thing downstream that could
/// answer, and it answered <i>"not available to role 'Developer'"</i>. That is false, and false in
/// the most expensive direction: a model told it lacks PERMISSION goes looking for another route,
/// while a model told it has a typo fixes one character. In run <c>c4373d</c> the step had already
/// produced a 298-line report with five findings and file:line evidence, and was marked INCOMPLETE
/// for three calls that never happened — taking all four remaining steps with it.</para>
///
/// <para>Nothing ran, so this is <see cref="ToolResults.NeverRan"/>'s case exactly, the same
/// sentence the shells say about a word they do not have and git says about its own arguments.</para>
/// </summary>
public sealed class ANameWithNoToolBehindItTests
{
    private const string QuickAction = """{"disposition":"quick_action","title":"read the log"}""";

    private static string[] ToolMessages(FakeChatProvider provider)
        => provider.Requests
            .SelectMany(r => r.Messages)
            .Where(m => m.Role == ChatRole.Tool)
            .Select(m => m.Content ?? "")
            .ToArray();

    /// <summary>
    /// The reported shape, end to end. The name is refused, the REAL spelling is named, the model
    /// uses it — and the step is not left holding a call that never happened.
    /// </summary>
    [Fact]
    public async Task A_name_that_does_not_exist_is_told_the_real_spelling()
    {
        using var fx = new EngineFixture();
        fx.Write("log.txt", "all good\n");

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("run-powershell", """{"script":"Get-Content log.txt"}"""),
            Turn.Calls1("run_powershell", """{"script":"Get-Content log.txt"}""", "c2"),
            Turn.Says("The file says: all good."));

        var events = await fx.RunAsync(
            fx.Build(provider, EngineFixture.Role("developer")), "read log.txt");

        // THE POINT. Three of these ended run c4373d with every remaining step skipped.
        Assert.False(events.Has(EventKind.TaskFailed), events.Text());

        var told = ToolMessages(provider);

        Assert.Contains(told, m => m.Contains("no tool called 'run-powershell'", StringComparison.Ordinal)
                                   && m.Contains("run_powershell", StringComparison.Ordinal));

        // And it never says the word that sent the model looking for another route.
        Assert.DoesNotContain(told, m => m.Contains("not available to the", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE BOUNDARY. A tool that genuinely EXISTS and is genuinely not this role's is still answered
    /// by the role gate, with the role named. The writer may not run shells, and no amount of
    /// spelling it correctly changes that — widening the new check until this goes green would hand
    /// every role every tool.
    /// </summary>
    [Fact]
    public async Task A_real_tool_this_role_may_not_use_still_names_the_role()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("run_powershell", """{"script":"Get-Date"}"""),
            Turn.Says("I cannot run shell commands in this role."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("writer")), "what is the date");

        Assert.Contains(ToolMessages(provider),
                        m => m.Contains("not available to the", StringComparison.Ordinal)
                             && m.Contains("run_powershell", StringComparison.Ordinal));
    }

    /// <summary>
    /// THE SECOND BOUNDARY, and the one that keeps this honest. A name nobody corrected is still an
    /// open failure: the model meant to run something, never did, and said it was finished. The
    /// typo is forgiven because the work was done under the right name — not because it was a typo.
    /// </summary>
    [Fact]
    public async Task A_name_nobody_ever_corrects_still_fails_the_step()
    {
        using var fx = new EngineFixture();
        fx.Write("log.txt", "all good");

        var events = await fx.RunAsync(
            fx.Build(
                new FakeChatProvider(
                    Turn.Says(QuickAction),
                    Turn.Calls1("run-powershell", """{"script":"Get-Content log.txt"}"""),
                    Turn.Says("The file says: all good.")),
                EngineFixture.Role("developer")),
            "read log.txt");

        Assert.True(events.Has(EventKind.TaskFailed), events.Text());
    }

    /// <summary>
    /// And a name that resembles nothing gets no guess. A confident wrong suggestion costs a turn
    /// and lands the model back here; the match is separators and case only, which is where a typo
    /// of a name the model can SEE in its own tool list actually comes from.
    /// </summary>
    [Fact]
    public async Task A_name_resembling_nothing_is_not_guessed_at()
    {
        using var fx = new EngineFixture();

        var provider = new FakeChatProvider(
            Turn.Says(QuickAction),
            Turn.Calls1("summon_the_compiler", """{"what":"everything"}"""),
            Turn.Says("There is no such tool, so I used what there is."));

        await fx.RunAsync(fx.Build(provider, EngineFixture.Role("developer")), "build it");

        var told = ToolMessages(provider);

        Assert.Contains(told, m => m.Contains("no tool called 'summon_the_compiler'", StringComparison.Ordinal)
                                   && m.Contains("spelled exactly as it appears", StringComparison.Ordinal));
        Assert.DoesNotContain(told, m => m.Contains("Did you mean", StringComparison.Ordinal));
    }
}
