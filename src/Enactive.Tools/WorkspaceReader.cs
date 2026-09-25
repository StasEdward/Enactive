namespace Enactive.Tools;

using Enactive.Core.Tools;

/// <summary>A single read source: pending content takes precedence over the disk.</summary>
internal sealed record WorkspaceReader(TextReader Reader, bool Staged) : IDisposable
{
    // Callers resolve and validate paths before opening any source.
    public static async Task<WorkspaceReader?> OpenAsync(
        ToolContext ctx, string relative, string full, CancellationToken ct)
    {
        var pending = await ctx.Artifacts.TryReadPendingAsync(relative, ct);
        if (pending is not null)
            return new(new StringReader(pending), true);

        return File.Exists(full) ? new(new StreamReader(full), false) : null;
    }

    public void Dispose() => Reader.Dispose();
}
