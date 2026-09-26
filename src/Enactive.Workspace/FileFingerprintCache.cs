namespace Enactive.Workspace;

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

/// <summary>Reuse hashes only when NTFS identity + change time say the same open file is unchanged.
/// LastWriteTime alone is insufficient: scripts can restore it after replacing same-sized content.
/// Other filesystems and failed native queries always rehash. Enumeration is never skipped.</summary>
internal sealed class FileFingerprintCache
{
    private readonly Dictionary<string, (Stamp Stamp, string Hash)> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _ntfs;
    internal int HashComputations { get; private set; }
    internal bool Enabled => _ntfs;

    internal FileFingerprintCache(string root)
    {
        try { _ntfs = OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(Path.GetFullPath(root))!)
            .DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase); }
        catch { _ntfs = false; }
    }

    internal async Task<string?> ReadAsync(string path, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            // Do not allow concurrent writers or replacement while validating/hashing this handle.
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var before = _ntfs ? ReadStamp(stream.SafeFileHandle) : null;
            if (before is { } stamp && _entries.TryGetValue(path, out var cached) && cached.Stamp == stamp)
                return cached.Hash;
            HashComputations++;
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, ct));
            var after = _ntfs ? ReadStamp(stream.SafeFileHandle) : null;
            if (before is not null && before == after)
            {
                // Also bound retention across repeatedly cancelled/incomplete scans.
                if (_entries.Count >= Enactive.Core.Context.WorkspaceCensus.MaxFilesScanned) _entries.Clear();
                _entries[path] = (before.Value, hash);
            }
            else _entries.Remove(path);
            return hash;
        }
        catch (IOException) { _entries.Remove(path); return null; }
        catch (UnauthorizedAccessException) { _entries.Remove(path); return null; }
    }

    internal void Retain(string root, IEnumerable<string> paths)
    {
        var live = paths.Select(p => Path.GetFullPath(Path.Combine(root, p)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var key in _entries.Keys.Where(k => !live.Contains(k)).ToArray()) _entries.Remove(key);
    }

    private readonly record struct Stamp(uint Volume, uint IdHigh, uint IdLow, long Change, long Write, long Size);
    private static Stamp? ReadStamp(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var identity)
            || !GetFileInformationByHandleEx(handle, 0, out var basic, (uint)Marshal.SizeOf<BasicInfo>())
            || basic.ChangeTime == 0) return null;
        return new(identity.VolumeSerial, identity.IndexHigh, identity.IndexLow,
            basic.ChangeTime, basic.LastWriteTime, ((long)identity.SizeHigh << 32) | identity.SizeLow);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicInfo
    {
        public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime;
        public uint Attributes;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct HandleInfo
    {
        public uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerial, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out HandleInfo info);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int informationClass,
        out BasicInfo info, uint size);
}
