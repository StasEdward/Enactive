namespace Enactive.Workspace;

using System.Security.Cryptography;
using System.Text;

/// <summary>Immutable staged bytes, kept outside the managed heap. The owning handle deletes the
/// private temporary file on disposal (also on process exit). Readers have independent positions.</summary>
internal sealed class StagedContent : IDisposable
{
    private readonly FileStream _owner;
    private StagedContent(FileStream owner, string hash) { _owner = owner; Hash = hash; }
    internal long Length => _owner.Length;
    internal string Hash { get; }
    internal Stream Open() => new FileStream(_owner.Name, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.SequentialScan);

    internal static async Task<StagedContent> CaptureAsync(Func<Stream, Task> write, CancellationToken ct)
    {
        var stream = new FileStream(Path.Combine(Path.GetTempPath(), $"enactive-stage-{Guid.NewGuid():N}"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete,
            8192, FileOptions.DeleteOnClose | FileOptions.Asynchronous);
        try
        {
            ct.ThrowIfCancellationRequested();
            await using (var writer = new FileStream(stream.Name, FileMode.Open, FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete, 8192, FileOptions.Asynchronous))
                await write(writer).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await stream.FlushAsync(ct).ConfigureAwait(false);
            stream.Position = 0;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct).ConfigureAwait(false));
            return new StagedContent(stream, hash);
        }
        catch { await stream.DisposeAsync().ConfigureAwait(false); throw; }
    }

    internal static StagedContent FromText(string text)
    {
        // Compatibility constructor; remain synchronous even under a UI synchronization context.
        var stream = new FileStream(Path.Combine(Path.GetTempPath(), $"enactive-stage-{Guid.NewGuid():N}"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete,
            8192, FileOptions.DeleteOnClose);
        try
        {
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false), 8192, leaveOpen: true))
                writer.Write(text);
            stream.Position = 0;
            return new StagedContent(stream, Convert.ToHexString(SHA256.HashData(stream)));
        }
        catch { stream.Dispose(); throw; }
    }

    internal string ReadText()
    {
        using var stream = Open();
        using var reader = new StreamReader(stream, Encoding.UTF8, true);
        return reader.ReadToEnd();
    }

    internal bool IsBinary()
    {
        using var stream = Open();
        using var reader = new StreamReader(stream, new UTF8Encoding(false, true), true);
        var buffer = new char[8192];
        try
        {
            int count;
            while ((count = reader.Read(buffer, 0, buffer.Length)) != 0)
                for (var i = 0; i < count; i++)
                    if (char.IsControl(buffer[i]) && buffer[i] is not ('\r' or '\n' or '\t')) return true;
            return false;
        }
        catch (DecoderFallbackException) { return true; }
    }
    public void Dispose() => _owner.Dispose();
}
