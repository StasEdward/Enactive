namespace Enactive.Tools;

using System.Text;
using Enactive.Core.Execution;

/// <summary>Bounded scan and retained head/tail, shared by disk and staged readers.</summary>
internal sealed record FilePreview(string Text, int Characters, int Lines, bool Complete)
{
    internal const int MaxInputChars = 1024 * 1024;

    public static async Task<FilePreview> ReadAsync(TextReader reader, int budget, CancellationToken ct)
    {
        var head = new StringBuilder(budget);
        var tail = new char[budget];
        var buffer = new char[8192];
        var count = 0;
        var lines = 1;
        var complete = false;
        while (count <= MaxInputChars)
        {
            ct.ThrowIfCancellationRequested();
            var read = await reader.ReadAsync(buffer.AsMemory(0,
                Math.Min(buffer.Length, MaxInputChars + 1 - count)), ct);
            if (read == 0) { complete = true; break; }
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                if (head.Length < budget) head.Append(c);
                tail[count % budget] = c;
                count++;
                if (c == '\n') lines++;
            }
        }
        if (count <= budget) return new(head.ToString(), count, lines, complete);

        var headSize = Math.Max(1, (int)(budget * Shortening.FileHead));
        var tailSize = budget - headSize;
        var end = new StringBuilder(tailSize);
        for (var i = count - tailSize; i < count; i++) end.Append(tail[i % budget]);
        var marker = complete
            ? $"\n… ({count - budget:N0} characters not shown here; the end follows) …\n"
            : "\n… (middle not shown here; end of scanned prefix follows, NOT the file end) …\n";
        return new(head.ToString(0, headSize) + marker + end, count, lines, complete);
    }
}
