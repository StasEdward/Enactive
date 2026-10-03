namespace Enactive.Engine.Tests;

using Enactive.Core.Tools;
using Xunit;

/// <summary>
/// Run bf24a5, 2026-10-04: the task allowed the command family "ledger-check" - in the run, "dotnet test" - and the
/// engine's own measurement of the tests before the work was refused by that very policy, because its command line
/// carries an argument written "console;verbosity=normal" and a semicolon anywhere was taken for a second command.
/// The run went on with no measurement to compare against, and a later attempt at the same task kept that. Inside
/// plain double quotes a semicolon is one argument's text to every shell this runs on; outside them it is refused
/// as before, and so is any line whose quoting the shells do not all read the same way.
/// </summary>
public sealed class ASeparatorInsideQuotesIsNotOneTests
{
    private static readonly TaskActionPolicy Policy = new(["run_command"], ["ledger-check"], "q", "r");

    [Theory]
    [InlineData("""ledger-check "books/2026.csv" --report "console;detail=full" """)]
    [InlineData("""ledger-check --report "a;b" --also "c;d" """)]
    public void A_semicolon_inside_plain_double_quotes_is_part_of_an_argument(string command)
        => Assert.True(Policy.AllowsCommand(command));

    [Theory]
    [InlineData("ledger-check ; other")]                             // outside quotes: a second command
    [InlineData("""ledger-check "a" ; other "b" """)]                // between two quoted arguments
    [InlineData("""ledger-check "unclosed ; other""")]               // quotes that do not pair
    [InlineData("""ledger-check \" ; other \" """)]                  // an escaped quote opens nothing
    [InlineData("""ledger-check '"' ; other '"' """)]                // single quotes make a double quote a character
    [InlineData("""ledger-check "a;b" & other""")]                   // every other separator, as before
    [InlineData("""ledger-check "a & b" """)]                        // and inside quotes too: only the semicolon is read
    [InlineData("""ledger-check "$(other);x" """)]
    public void Anything_the_shells_could_read_as_more_than_one_command_is_still_refused(string command)
        => Assert.False(Policy.AllowsCommand(command));
}
