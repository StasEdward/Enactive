namespace Enactive.Engine.Tests;

using Enactive.Tools;
using Xunit;

/// <summary>
/// Reading PowerShell's serialised streams back into words.
///
/// <para>Both blobs below were captured from a real powershell.exe on 2026-09-10, not written by
/// hand: the progress-only one is what every search in the Total Commander run produced, and the
/// error one is what a genuine failure looks like. A test whose input was invented would pin this
/// decoder to my idea of the format rather than to the format.</para>
/// </summary>
public sealed class CliXmlTests
{
    /// <summary>What a script with nothing to report produced - twice, in front of every answer.</summary>
    private const string ProgressOnly =
        """
        #< CLIXML
        <Objs Version="1.1.0.1" xmlns="http://schemas.microsoft.com/powershell/2004/04"><Obj S="progress" RefId="0"><TN RefId="0"><T>System.Management.Automation.PSCustomObject</T><T>System.Object</T></TN><MS><I64 N="SourceId">1</I64><PR N="Record"><AV>Preparing modules for first use.</AV><AI>0</AI><Nil /><PI>-1</PI><PC>-1</PC><T>Completed</T><SR>-1</SR><SD> </SD></PR></MS></Obj></Objs>
        """;

    /// <summary>A real failure: Get-Content on a path that is not there.</summary>
    private const string RealError =
        """
        #< CLIXML
        <Objs Version="1.1.0.1" xmlns="http://schemas.microsoft.com/powershell/2004/04"><Obj S="progress" RefId="0"><TN RefId="0"><T>System.Management.Automation.PSCustomObject</T><T>System.Object</T></TN><MS><I64 N="SourceId">1</I64><PR N="Record"><AV>Preparing modules for first use.</AV><AI>0</AI><Nil /><PI>-1</PI><PC>-1</PC><T>Completed</T><SR>-1</SR><SD> </SD></PR></MS></Obj><S S="Error">Get-Content : Cannot find path 'C:\no\such.txt' because it does not exist._x000D__x000A_</S><S S="Error">At line:1 char:1_x000D__x000A_</S><S S="Error">+ Get-Content 'C:\no\such.txt'_x000D__x000A_</S><S S="Error">    + FullyQualifiedErrorId : PathNotFound,Microsoft.PowerShell.Commands.GetContentCommand_x000D__x000A_</S></Objs>
        """;

    /// <summary>
    /// The decisive one. Progress is not an error, and a script that only ever emitted progress had
    /// nothing to report - so what comes back is nothing, and every rule downstream that asks "did
    /// it say anything went wrong" now gets the right answer.
    /// </summary>
    [Fact]
    public void A_blob_of_only_progress_decodes_to_nothing()
        => Assert.Equal(string.Empty, CliXml.ToText(ProgressOnly));

    /// <summary>And the other half: a real error survives, in words, with its newlines back.</summary>
    [Fact]
    public void An_error_decodes_to_the_message_a_person_would_read()
    {
        var text = CliXml.ToText(RealError);

        Assert.Contains("Cannot find path 'C:\\no\\such.txt'", text, StringComparison.Ordinal);
        Assert.Contains("PathNotFound", text, StringComparison.Ordinal);

        // The escapes are gone, and so is the XML around them.
        Assert.DoesNotContain("_x000D_", text, StringComparison.Ordinal);
        Assert.DoesNotContain("<S S=", text, StringComparison.Ordinal);
        Assert.DoesNotContain("CLIXML", text, StringComparison.Ordinal);

        // Several records, so several lines - not one run-on sentence.
        Assert.Contains('\n', text);

        // Progress dropped even when there is a real error beside it.
        Assert.DoesNotContain("Preparing modules", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Anything that is not CLIXML is not touched. Most of what these tools capture is ordinary
    /// text, and a decoder that rewrote it would be a new way to lose output.
    /// </summary>
    [Fact]
    public void Ordinary_text_comes_back_unchanged()
    {
        const string plain = "error CS0246: The type or namespace name 'Foo' could not be found\r\n";

        Assert.Equal(plain, CliXml.ToText(plain));
        Assert.Equal(string.Empty, CliXml.ToText(string.Empty));
    }

    /// <summary>
    /// A blob cut short by the capture ceiling comes back whole rather than empty. The one line
    /// saying what went wrong may be in the part that survived, and losing it to a parse error
    /// would be worse than showing the XML.
    /// </summary>
    [Fact]
    public void A_truncated_blob_is_returned_rather_than_lost()
    {
        var cut = RealError[..(RealError.Length / 2)];

        Assert.Equal(cut, CliXml.ToText(cut));
    }
}
