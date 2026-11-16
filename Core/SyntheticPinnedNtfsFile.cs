#if CHAPPY_TESTING
using System.ComponentModel;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ChappyStorageCompressor.Core;

/// <summary>
/// Part33 Windows API RESEARCH PROBE. Entire class is compiled OUT of shipped
/// builds. Never invoke on a user-selected path: only generated, direct-child
/// regular files under the Part23 sentinel can be opened or mutated.
/// It is not a production transaction/recovery mechanism.
/// </summary>
internal sealed class SyntheticPinnedNtfsFile : IDisposable
{
    private const uint DeleteAccess = 0x00010000;
    private const uint ReadAttributes = 0x00000080;
    private const uint ShareAll = 0x1 | 0x2 | 0x4;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const int FileRenameInfo = 3;
    private const int FileDispositionInfo = 4;
    private readonly SafeFileHandle _handle;
    private readonly string _root;
    private readonly string _originalPath;
    private readonly FileSnapshot _openingSnapshot;
    private readonly string _openingHash;

    [StructLayout(LayoutKind.Sequential)]
    private struct Win32ByHandleInfo
    {
        public uint Attributes;
        public FILETIME CreationTime;
        public FILETIME LastAccessTime;
        public FILETIME LastWriteTime;
        public uint VolumeSerial;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint LinkCount;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string name, uint desiredAccess, uint shareMode, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandle",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInfo(SafeFileHandle handle,
        out Win32ByHandleInfo info);

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle",
        SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInfo(SafeFileHandle handle,
        int infoClass, IntPtr data, uint bytes);

    // The normal Safety.ComputeSha256 intentionally opens FileShare.Read
    // (no delete sharing). That conflicts with our *own* intentional pinned
    // DELETE-access handle. For the test-only pinned lab reopen with
    // FileShare.Read | FileShare.Delete: still refuse concurrent writers.
    private static string HashWhilePinned(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read | FileShare.Delete, 128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal FileIdentity OpeningIdentity { get; }
    internal string OpeningHash => _openingHash;
    internal FileIdentity CurrentHandleIdentity => FromHandle(_handle);

    private static FileIdentity FromHandle(SafeFileHandle handle)
    {
        if (!GetFileInfo(handle, out var info))
            throw new Win32Exception(Marshal.GetLastWin32Error(),
                "Part33 pinned NTFS handle identity unreadable");
        return new FileIdentity(info.VolumeSerial,
            ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow, info.LinkCount);
    }

    private SyntheticPinnedNtfsFile(string root, string path, FileSnapshot original,
        string hash, SafeFileHandle handle)
    {
        _root = root;
        _originalPath = path;
        _openingSnapshot = original;
        _openingHash = hash;
        _handle = handle;
        OpeningIdentity = FromHandle(handle);
    }

    internal static string RequireRoot(string input)
    {
        string root = Safety.ValidateRoot(input);
        string temp = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        if (!root.StartsWith(temp + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !root.Contains("ChappyStorageSmoke_", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(Path.Combine(root, ".chappy-part23-generated-only")) ||
            File.ReadAllText(Path.Combine(root, ".chappy-part23-generated-only")) !=
                "Part23 synthetic data only")
            throw new InvalidOperationException(
                "Part33 handle operations require generated-only NTFS TEMP root");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Part33 root is a reparse point");
        return root;
    }

    internal static string RequireDirectFile(string root, string path,
        bool mustExist = true)
    {
        string full = Path.GetFullPath(path);
        if (!string.Equals(Path.GetDirectoryName(full), root,
                StringComparison.OrdinalIgnoreCase) ||
            !full.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetExtension(full).Equals(".mp4",
                StringComparison.OrdinalIgnoreCase) ||
            !Path.GetFileName(full).StartsWith("part33-",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Part33 only permits direct-child generated .mp4 fixtures");
        if (mustExist)
        {
            if (!File.Exists(full) ||
                (File.GetAttributes(full) & (FileAttributes.ReparsePoint |
                    FileAttributes.Directory)) != 0 ||
                NativeFile.HasNamedDataStreams(full))
                throw new InvalidDataException("Part33 requires regular NTFS file without ADS");
        }
        return full;
    }

    internal static SyntheticPinnedNtfsFile Open(string rootInput, string path)
    {
        string root = RequireRoot(rootInput);
        string full = RequireDirectFile(root, path);
        var snapshot = Safety.Capture(full);
        if (snapshot.Identity.LinkCount != 1 ||
            !Safety.SameSnapshot(snapshot))
            throw new InvalidDataException("Part33 may pin only a single-link exact original");
        string hash = Safety.ComputeSha256(full, CancellationToken.None);
        var handle = CreateFile(full, DeleteAccess | ReadAttributes, ShareAll,
            IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new Win32Exception(error, "Part33 could not pin DELETE-access NTFS handle");
        }
        try
        {
            var pinned = new SyntheticPinnedNtfsFile(root, full, snapshot, hash, handle);
            if (!pinned.OpeningIdentity.SameFile(snapshot.Identity) ||
                !pinned.OriginalPathStillMatches())
                throw new InvalidDataException("File changed while opening pinned handle");
            return pinned;
        }
        catch { handle.Dispose(); throw; }
    }

    internal bool OriginalPathStillMatches()
    {
        try
        {
            string root = RequireRoot(_root);
            RequireDirectFile(root, _originalPath);
            return CurrentHandleIdentity.SameFile(OpeningIdentity) &&
                NativeFile.GetIdentity(_originalPath).SameFile(OpeningIdentity) &&
                Safety.SameSnapshot(_openingSnapshot) &&
                HashWhilePinned(_originalPath) == _openingHash;
        }
        catch { return false; }
    }

    /// <summary>
    /// Always targets the pinned NTFS ID, NOT whichever object currently
    /// occupies _originalPath. Destination replacement is always FALSE.
    /// afterLastPathCheck is ONLY for a generated child-process race demo.
    /// </summary>
    internal void RenameNoReplace(string destination,
        Action? afterLastPathCheck = null)
    {
        string target = RequireDirectFile(RequireRoot(_root), destination, mustExist: false);
        if (File.Exists(target) || !OriginalPathStillMatches())
            throw new InvalidDataException("Part33 rename preflight: old or target path changed");
        afterLastPathCheck?.Invoke();
        if (!CurrentHandleIdentity.SameFile(OpeningIdentity))
            throw new InvalidDataException("Pinned NTFS ID changed before handle rename");

        // FILE_RENAME_INFO (FileRenameInfo=3): union starts at offset 0;
        // RootDirectory follows pointer alignment; FileNameLength precedes
        // variable UTF-16 FileName. Always ReplaceIfExists=FALSE.
        byte[] nameBytes = Encoding.Unicode.GetBytes(target);
        int rootOffset = IntPtr.Size == 8 ? 8 : 4;
        int lengthOffset = IntPtr.Size == 8 ? 16 : 8;
        int nameOffset = lengthOffset + 4;
        byte[] buffer = new byte[nameOffset + nameBytes.Length + 2];
        BitConverter.GetBytes(nameBytes.Length).CopyTo(buffer, lengthOffset);
        nameBytes.CopyTo(buffer, nameOffset);
        IntPtr native = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            Marshal.Copy(buffer, 0, native, buffer.Length);
            if (!SetFileInfo(_handle, FileRenameInfo, native, (uint)buffer.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Part33 handle rename failed or refused occupied target");
        }
        finally { Marshal.FreeHGlobal(native); }
        if (!NativeFile.GetIdentity(target).SameFile(OpeningIdentity) ||
            HashWhilePinned(target) != OpeningHash)
            throw new InvalidDataException("Handle rename postcheck failed: retain evidence");
    }

    /// <summary>
    /// Only test-owned, unchanged single-link fixture. Even here deletion
    /// remains separate from rename; not an atomic commit or crash-proof Undo.
    /// </summary>
    internal void DeleteOnClose()
    {
        if (!OriginalPathStillMatches() ||
            CurrentHandleIdentity.LinkCount != 1)
            throw new InvalidDataException("Part33 refused delete: name or link count changed");
        IntPtr native = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(native, 1);
            if (!SetFileInfo(_handle, FileDispositionInfo, native, 4))
                throw new Win32Exception(Marshal.GetLastWin32Error(),
                    "Part33 handle disposition refused");
        }
        finally { Marshal.FreeHGlobal(native); }
        _handle.Dispose();
    }

    public void Dispose() => _handle.Dispose();
}
#endif
