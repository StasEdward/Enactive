namespace Enactive.Core.Tools;

using Enactive.Core.Context;

/// <summary>
/// What a command printed, read whole - by what has to read all of it, where the model is shown only its start and end.
///
/// <para>A command's result text carries a status line, a marker, and what it printed, shortened to fit; the whole of
/// a long output goes to a file in the scratch (ProcessExec). A test run's failures sit in the middle of a long log -
/// exactly the part shortening drops - so the ecosystem that reads them (IEcosystem.DescribeTests) is handed this,
/// both by run_tests and by the step's loop when tests were run through the shell.</para>
/// </summary>
public static class CommandOutput
{
    /// <summary>The result's metadata key for where the whole output is kept, when it was too long to show.</summary>
    public const string KeptKey = "keptOutput";

    /// <summary>The line in a command's result under which what it printed begins.</summary>
    public const string Marker = "----- command output (this is the result) -----\n";

    /// <summary>Where the whole output is kept (workspace-relative), or null when it was all shown.</summary>
    public static string? KeptPath(ToolResult result)
        => result.Metadata.TryGetValue(KeptKey, out var k) && k is string path ? path : null;

    /// <summary>What the command printed: the kept file when it was too long to show, the result's own text otherwise.</summary>
    public static string? Whole(ToolResult result, string workspaceRoot)
    {
        if (KeptPath(result) is { } kept)
            try { return File.ReadAllText(WorkspaceGuard.ResolveInside(workspaceRoot, kept)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { }
        var text = result.Output ?? result.Error;
        if (text is null) return null;
        var at = text.IndexOf(Marker, StringComparison.Ordinal);
        return at < 0 ? text : text[(at + Marker.Length)..];
    }
}
