namespace Enactive.Tools;

using System.Text;
using Enactive.Core;
using Enactive.Core.Execution;

/// <summary>Bounded scan and retained head/tail, shared by disk and staged readers.</summary>
/// <param name="Spans">Where the file's own text is in <paramref name="Text"/> and which lines it holds - the whole of it,
/// or its start and its end around the marker - for the reviewer's account of what of the file it is shown (FileRead).</param>
internal sealed record FilePreview(string Text, int Characters, int Lines, bool Complete, IReadOnlyList<FileRead.Span> Spans)
{
    internal const int MaxInputChars = 1024 * 1024;

    public static async Task<FilePreview> ReadAsync(TextReader reader, int budget, CancellationToken ct)
    {
        var head = new StringBuilder(budget);
        var tail = new char[budget];
        var count = 0;
        var headSize = Math.Max(1, (int)(budget * Shortening.FileHead));
        // The character just past the head, kept to say whether the head's last line is whole in it.
        var afterHead = '\0';
        var scan = await FileTextScanner.ReadAsync(reader, (c, _) =>
        {
            if (head.Length < budget) head.Append(c);
            if (count == headSize) afterHead = c;
            tail[count % budget] = c;
            count++;
        }, MaxInputChars, ct);
        var lines = scan.Lines;
        var complete = scan.Complete;
        if (count <= budget)
            return new(head.ToString(), count, lines, complete, [new(0, count, 1, lines, true, complete)]);

        var tailSize = budget - headSize;
        var end = new StringBuilder(tailSize);
        for (var i = count - tailSize; i < count; i++) end.Append(tail[i % budget]);
        var marker = complete
            ? $"\n… ({Invariant.N0(count - budget)} characters not shown here; the end follows) …\n"
            : "\n… (middle not shown here; end of scanned prefix follows, NOT the file end) …\n";
        var start = head.ToString(0, headSize);
        var ending = end.ToString();
        // The end runs to the last line scanned, so it begins as many lines before that as it holds line breaks. The
        // character before it is still in the ring (the tail is shorter than the budget by the head).
        var beforeTail = tail[(count - tailSize - 1) % budget];
        return new(start + marker + ending, count, lines, complete,
        [
            new(0, start.Length, 1, 1 + start.Count(c => c == '\n'), true, afterHead == '\n'),
            new(start.Length + marker.Length, ending.Length, lines - ending.Count(c => c == '\n'), lines, beforeTail == '\n', complete)
        ]);
    }

    // By value, spans included: a list compares by reference, and the same file read from disk and from staging is the
    // same preview (BoundedFilePreviewTests).
    public bool Equals(FilePreview? other)
        => other is not null && Text == other.Text && Characters == other.Characters && Lines == other.Lines
           && Complete == other.Complete && Spans.SequenceEqual(other.Spans);

    public override int GetHashCode() => HashCode.Combine(Text, Characters, Lines, Complete, Spans.Count);
}
