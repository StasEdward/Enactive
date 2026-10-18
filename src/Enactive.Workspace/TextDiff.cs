namespace Enactive.Workspace;

/// <summary>
/// The staging preview's line diff. The comparison itself is Core's (<see cref="Enactive.Core.Execution.LineDiff"/>), so the
/// engine shows a reviewer a staged protected file's change the same way (Plan Phase 3.4); the preview keeps its own
/// advice for a pair of files too large to compare.
/// </summary>
public static class TextDiff
{
    public static string Unified(string? oldText, string newText, string path)
        => Enactive.Core.Execution.LineDiff.Unified(oldText, newText, path,
            "Open the file to review it. Apply and Reject still work from here.");
}
