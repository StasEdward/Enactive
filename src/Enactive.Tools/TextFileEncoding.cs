namespace Enactive.Tools;

using System.Text;
using Enactive.Core.Artifacts;

/// <summary>Preserves Unicode BOMs when editing either a pending proposal or the disk file.</summary>
internal static class TextFileEncoding
{
    internal sealed record Snapshot(string? Text, Encoding Encoding, ArtifactVersion Version);

    public static async Task<Snapshot> ReadSnapshotAsync(IArtifactStore store, string path, string fullPath, CancellationToken ct)
    {
        Stream? source = await store.TryOpenPendingAsync(path, ct);
        if (source is null)
        {
            try { source = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 4096, FileOptions.Asynchronous); }
            catch (FileNotFoundException) { return new(null, new UTF8Encoding(false, true), new(null)); }
            catch (DirectoryNotFoundException) { return new(null, new UTF8Encoding(false, true), new(null)); }
        }
        await using var stream = source;
        if (!stream.CanSeek) throw new NotSupportedException("Editing requires a seekable pending artifact stream.");
        var hash = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream, ct));
        stream.Position = 0;
        var encoding = await Detect(stream, ct);
        stream.Position = encoding.GetPreamble().Length;
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return new(await reader.ReadToEndAsync(ct), encoding, new(hash));
    }

    public static async Task<string> ReadTextAsync(IArtifactStore store, string path, string fullPath, CancellationToken ct)
    {
        await using var stream = await store.TryOpenPendingAsync(path, ct)
            ?? new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous);
        if (!stream.CanSeek) throw new NotSupportedException("Editing requires a seekable pending artifact stream.");
        var encoding = await Detect(stream, ct);
        stream.Position = encoding.GetPreamble().Length;
        // Automatic BOM detection would substitute a lenient decoder and silently replace bad bytes.
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        return await reader.ReadToEndAsync(ct);
    }

    public static async Task<Encoding> ReadAsync(IArtifactStore store, string path, string fullPath, CancellationToken ct)
    {
        await using var pending = await store.TryOpenPendingAsync(path, ct);
        if (pending is not null) return await Detect(pending, ct);
        if (!File.Exists(fullPath)) return new UTF8Encoding(false, true);
        // Encoding inspection must not prevent an independent step's atomic replacement.
        await using var disk = new FileStream(fullPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
        return await Detect(disk, ct);
    }

    internal static async Task<Encoding> Detect(Stream stream, CancellationToken ct)
    {
        var bytes = new byte[4];
        var count = await stream.ReadAtLeastAsync(bytes, 4, throwOnEndOfStream: false, cancellationToken: ct);
        if (count >= 4 && bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0xff, 0xfe, 0, 0 })) return new UTF32Encoding(false, true, true);
        if (count >= 4 && bytes.AsSpan(0, 4).SequenceEqual(new byte[] { 0, 0, 0xfe, 0xff })) return new UTF32Encoding(true, true, true);
        if (count >= 3 && bytes.AsSpan(0, 3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf })) return new UTF8Encoding(true, true);
        if (count >= 2 && bytes[0] == 0xff && bytes[1] == 0xfe) return new UnicodeEncoding(false, true, true);
        if (count >= 2 && bytes[0] == 0xfe && bytes[1] == 0xff) return new UnicodeEncoding(true, true, true);
        return new UTF8Encoding(false, true);
    }

    public static byte[] Encode(string text, Encoding encoding)
        => [.. encoding.GetPreamble(), .. encoding.GetBytes(text)];
}
