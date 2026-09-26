namespace Enactive.Tools;

/// <summary>One streaming loop and line-count convention for windows and previews.</summary>
internal static class FileTextScanner
{
    internal readonly record struct Scan(int Characters, int Lines, bool Complete);

    public static async Task<Scan> ReadAsync(TextReader reader, Action<char, int> consume,
        int? inputLimit, CancellationToken ct)
    {
        var buffer = new char[8192];
        var count = 0;
        var line = 1;
        while (inputLimit is null || count <= inputLimit)
        {
            ct.ThrowIfCancellationRequested();
            var room = inputLimit is { } max ? Math.Min(buffer.Length, max + 1 - count) : buffer.Length;
            var read = await reader.ReadAsync(buffer.AsMemory(0, room), ct);
            if (read == 0) return new(count, line, true);
            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];
                consume(c, line);
                count++;
                if (c == '\n') line++;
            }
        }
        return new(count, line, false);
    }
}
